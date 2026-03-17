using RimWorld.Planet;
using RimWorld;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.Sound;
using System.ComponentModel;

namespace DMSE
{
    public enum OrbitalTransferState
    {
        Idle,
        WarmUp,
        Working,
    }

    public class MapComponent_Ship : MapComponent
    {
        // ── Serialized state ──────────────────────────────────────────────────
        public OrbitalTransferState status = OrbitalTransferState.Idle;
        public TravelingObject shipWorldObject;
        public List<Thing> thrusterPlacements = new List<Thing>();

        // ── Warm-up bookkeeping ───────────────────────────────────────────────
        private const int WarmUpDurationTicks = GenTicks.TicksPerRealSecond * 5;
        private int warmUpTicksRemaining = 0;
        private float progressAtWorkStart = 0f;
        private List<Thing> pendingThrusters = new List<Thing>();
        private TravelingObject pendingShipWorldObject;

        // ── Travel ────────────────────────────────────────────────────────────
        private PlanetTile initialTile = PlanetTile.Invalid;

        // ── Rendering ─────────────────────────────────────────────────────────
        private MaterialPropertyBlock thrusterFlameBlock = new MaterialPropertyBlock();
        private MaterialPropertyBlock flareBlock = new MaterialPropertyBlock();
        private Dictionary<Thing, EventQueue> exhaustTimers = new Dictionary<Thing, EventQueue>();
        private FleckSystem exhaustFleckSystem;
        private EventQueue manualTicker;
        private DrawBatch drawBatch = new DrawBatch();

        private static readonly int ShaderPropertyColor2 = Shader.PropertyToID("_Color2");
        private static readonly Material MatGravshipLensFlare =
            MatLoader.LoadMat("Map/Gravship/GravshipLensFlare", -1);

        // ── Constructor ───────────────────────────────────────────────────────
        public MapComponent_Ship(Map map) : base(map)
        {
            manualTicker = new EventQueue(1f / 60f);
        }

        // ── MapComponent overrides ────────────────────────────────────────────
        public override void FinalizeInit()
        {
            base.FinalizeInit();

            if (status != OrbitalTransferState.Working)
                return;

            var engineThings = map.listerThings.ThingsOfDef(ThingDefOf.GravEngine);
            if (engineThings.Count == 0)
            {
                status = OrbitalTransferState.Idle;
                return;
            }

            var engine = (ThingWithComps)engineThings[0];
            var facilities = engine.GetComp<CompAffectedByFacilities>()
                .LinkedFacilitiesListForReading
                .FindAll(t =>
                    t.TryGetComp<CompGravshipFacility>().Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster);

            Init(facilities, shipWorldObject);
        }


        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (Find.CurrentMap != map) return;
            if (Find.World.renderer.wantedMode != WorldRenderMode.None) { Log.Message("wantermode"); return; }
            if (status != OrbitalTransferState.Working) { Log.Message("status"); return; }
            if (shipWorldObject == null) { Log.Message("shipWorldObject"); return; }
            if (thrusterPlacements == null || thrusterPlacements.Count == 0) { Log.Message("no thruster"); return; }

            Draw(Mathf.Clamp01(1f - shipWorldObject.Progress));
        }
        public override void MapComponentTick()
        {
            base.MapComponentTick();

            switch (status)
            {
                case OrbitalTransferState.Idle:
                    break;

                case OrbitalTransferState.WarmUp:
                    TickWarmUp();
                    break;

                case OrbitalTransferState.Working:
                    TickWorking();
                    break;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                //thrusterPlacements.RemoveAll(t => t == null || !t.Spawned);
                pendingThrusters.RemoveAll(t => t == null || !t.Spawned);
            }

            Scribe_Values.Look(ref status, "workingStatus");
            Scribe_Values.Look(ref warmUpTicksRemaining, "warmUpTicksRemaining");
            Scribe_Values.Look(ref progressAtWorkStart, "progressAtWorkStart");
            Scribe_Values.Look(ref initialTile, "initialTile");

            WorldObject shipWO = shipWorldObject;
            Scribe_References.Look(ref shipWO, "shipWorldObject");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                shipWorldObject = shipWO as TravelingObject;

            WorldObject pendingWO = pendingShipWorldObject;
            Scribe_References.Look(ref pendingWO, "pendingShipWorldObject");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                pendingShipWorldObject = pendingWO as TravelingObject;

            //Scribe_Collections.Look(ref thrusterPlacements, "thrusterPlacements", LookMode.Reference);
            Scribe_Collections.Look(ref pendingThrusters, "pendingThrusters", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                //if (thrusterPlacements == null) thrusterPlacements = new List<Thing>();
                if (pendingThrusters == null) pendingThrusters = new List<Thing>();
            }
        }

        // ── Public API ────────────────────────────────────────────────────────
        public void StartWarmUp(Thing core, List<Thing> thrusters, TravelingObject wo)
        {
            pendingThrusters = thrusters;
            pendingShipWorldObject = wo;
            warmUpTicksRemaining = WarmUpDurationTicks;
            progressAtWorkStart = 0f;
            status = OrbitalTransferState.WarmUp;

            SoundInfo info = SoundInfo.InMap(
                new TargetInfo(core.Position, core.Map, false), MaintenanceType.None);
            SoundDefOf.ShipReactor_Startup.PlayOneShot(info);

            Messages.Message(
                "DMSE_ShipWarmUpBegin".Translate(),
                MessageTypeDefOf.CautionInput,
                historical: false);
        }

        public void Init(List<Thing> thrusters, TravelingObject wo)
        {
            status = OrbitalTransferState.Working;
            Log.Message("Initializing ship map component with " + thrusters.Count + " thrusters and world object " + wo);
            foreach (var thruster in thrusters)
            {
                thrusterPlacements.Add(thruster);
            }
            shipWorldObject = wo;

            exhaustFleckSystem = new FleckSystemThrown(map.flecks);
            exhaustTimers.Clear();

            foreach (Thing thing in thrusterPlacements)
            {
                if (!thing.TryGetComp(out CompGravshipThruster comp))
                    continue;

                CompProperties_GravshipThruster.ExhaustSettings ex = comp.Props.exhaustSettings;
                if (ex == null || !ex.enabled || ex.ExhaustFleckDef == null)
                    continue;

                exhaustFleckSystem.handledDefs.AddUnique(ex.ExhaustFleckDef);
                exhaustTimers.Add(thing, new EventQueue(1f / ex.emissionsPerSecond));
            }
            wo.StartTraveling();
        }

        public void End()
        {
            status = OrbitalTransferState.Idle;
            shipWorldObject = null;
            initialTile = PlanetTile.Invalid;
            thrusterPlacements.Clear();
            exhaustTimers.Clear();
            flareBlock.Clear();
        }

        public override void MapComponentDraw()
        {
            base.MapComponentDraw();
        }

        // ── Private tick helpers ──────────────────────────────────────────────
        private void TickWarmUp()
        {
            int secondsLeft = Mathf.CeilToInt((float)warmUpTicksRemaining / GenTicks.TicksPerRealSecond);
            if (warmUpTicksRemaining % GenTicks.TicksPerRealSecond == 0 && secondsLeft > 0)
            {
                Messages.Message(
                    "DMSE_ShipWarmUpCountdown".Translate(secondsLeft),
                    MessageTypeDefOf.CautionInput,
                    historical: false);
            }

            warmUpTicksRemaining--;

            if (warmUpTicksRemaining > 0)
                return;
            initialTile = (pendingShipWorldObject).Tile;
            Init(pendingThrusters, pendingShipWorldObject);

            pendingThrusters.Clear();
            pendingShipWorldObject = null;
        }

        private void TickWorking()
        {
            Find.CameraDriver.shaker.DoShake(0.1f);

            // ── Vaporize pawns near thrusters ─────────────────────────
            foreach (Pawn pawn in map.mapPawns.AllPawns.ListFullCopy())
            {
                if (pawn == null || !pawn.Spawned) continue;

                foreach (Thing thing in thrusterPlacements)
                {
                    CompGravshipThruster comp = thing.TryGetComp<CompGravshipThruster>();
                    if (comp == null) continue;

                    CompProperties_GravshipThruster props = comp.Props;
                    float flameRadius = thing.def.size.x * props.flameSize;
                    Vector3 flameOffset = thing.Rotation.AsQuat * props.flameOffsetsPerDirection[thing.Rotation.AsInt];
                    Vector3 drawPos = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f)
                        - thing.Rotation.AsIntVec3.ToVector3()
                            * (thing.def.size.z * 0.5f + flameRadius * 0.5f)
                        + flameOffset;

                    if (pawn.Position.DistanceTo(drawPos.ToIntVec3()) < thing.def.size.Magnitude / 1.5f)
                        pawn.TakeDamage(new DamageInfo(DamageDefOf.Vaporize, 1000f));
                }
            }
        }
        public void BeginUpdate()
        {
            this.exhaustFleckSystem.parent = Find.CurrentMap.flecks;
            manualTicker.Push(Time.deltaTime);
            while (manualTicker.Pop())
            {
                this.exhaustFleckSystem.Tick();
            }
            this.exhaustFleckSystem.Update(Time.deltaTime);
        }

        private void Draw(float cutsceneProgressPercent)
        {
            BeginUpdate();
            Color color = new Color(1f, 1f, 1f, 1f);
            color *= Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise1D(cutsceneProgressPercent * 100f));
            color.a = Mathf.InverseLerp(0f, 0.1f, cutsceneProgressPercent);
            MatGravshipLensFlare.SetColor(ShaderPropertyColor2, color);
            foreach (Thing thing in this.thrusterPlacements)
            {
                CompProperties_GravshipThruster props = thing.TryGetComp<CompGravshipThruster>().Props;
                float num = (float)thing.def.size.x * props.flameSize;
                Vector3 b2 = thing.Rotation.AsQuat * props.flameOffsetsPerDirection[thing.Rotation.AsInt];
                Vector3 drawPos = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f)
                    - thing.Rotation.AsIntVec3.ToVector3() *
                    ((float)thing.def.size.z * 0.5f + num * 0.5f) + b2;
                Material material = MaterialPool.MatFrom(new MaterialRequest(props.FlameShaderType.Shader)
                {
                    renderQueue = 3201
                });
                this.thrusterFlameBlock.Clear();
                this.thrusterFlameBlock.SetColor(ShaderPropertyColor2, color);
                foreach (ShaderParameter shaderParameter in props.flameShaderParameters)
                {
                    shaderParameter.Apply(this.thrusterFlameBlock);
                }
                var rotation = thing.Rotation;
                GenDraw.DrawQuad(material, drawPos, thing.Rotation.AsQuat, num, this.thrusterFlameBlock);
                this.flareBlock.SetVector(ShaderPropertyIDs.DrawPos, drawPos);
                this.DrawLayer(MatGravshipLensFlare, drawPos.SetToAltitude(AltitudeLayer.MetaOverlays).WithYOffset(0.03658537f), this.flareBlock);
                if (props.exhaustSettings.enabled)
                {
                    EventQueue eventQueue = this.exhaustTimers[thing];
                    eventQueue.Push(Time.deltaTime);
                    while (eventQueue.Pop())
                    {
                        CompProperties_GravshipThruster.ExhaustSettings exhaustSettings = props.exhaustSettings;
                        Vector3 position3 = drawPos;
                        rotation = thing.Rotation;
                        this.EmitSmoke(exhaustSettings, position3, Rot4.North.AsQuat, rotation.AsQuat);
                    }
                }
            }
            this.EndUpdate();
        }
        private void EmitSmoke(CompProperties_GravshipThruster.ExhaustSettings settings,
            Vector3 position, Quaternion gravshipRotation, Quaternion thrusterRotation)
        {
            Quaternion quaternion = Quaternion.identity;
            if (settings.inheritThrusterRotation)
            {
                quaternion = thrusterRotation * quaternion;
            }
            if (settings.inheritGravshipRotation)
            {
                quaternion = gravshipRotation * quaternion;
            }
            this.exhaustFleckSystem.CreateFleck(new FleckCreationData
            {
                def = settings.ExhaustFleckDef,
                spawnPosition = position + quaternion * settings.spawnOffset + UnityEngine.Random.insideUnitSphere.WithY(0f).normalized * settings.spawnRadiusRange.RandomInRange,
                scale = settings.scaleRange.RandomInRange,
                velocity = new Vector3?(quaternion * Quaternion.Euler(0f, settings.velocityRotationRange.RandomInRange, 0f) * (settings.velocity * settings.velocityMultiplierRange.RandomInRange)),
                rotationRate = settings.rotationOverTimeRange.RandomInRange,
                ageTicksOverride = -1
            });
        }
        private void DrawLayer(Material mat, Vector3 position, MaterialPropertyBlock props)
        {
            float num = Find.Camera.orthographicSize * 2f;
            Vector3 s = new Vector3(num * Find.Camera.aspect, 1f, num);
            Matrix4x4 matrix = Matrix4x4.TRS(position, Quaternion.identity, s);
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0, null, 0, props);
        }
        public void EndUpdate()
        {
            this.exhaustFleckSystem.ForceDraw(this.drawBatch);
            this.drawBatch.Flush(true);
        }
    }
}