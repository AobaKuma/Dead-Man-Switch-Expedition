using RimWorld.Planet;
using RimWorld;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.Sound;

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
                thrusterPlacements.RemoveAll(t => t == null || !t.Spawned);
                pendingThrusters.RemoveAll(t => t == null || !t.Spawned);
            }

            Scribe_Values.Look(ref status, "workingStatus");
            Scribe_Values.Look(ref warmUpTicksRemaining, "warmUpTicksRemaining");
            Scribe_Values.Look(ref progressAtWorkStart, "progressAtWorkStart");
            Scribe_Values.Look(ref initialTile, "initialTile");

            WorldObject shipWO = shipWorldObject as WorldObject;
            Scribe_References.Look(ref shipWO, "shipWorldObject");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                shipWorldObject = shipWO as TravelingObject;

            WorldObject pendingWO = pendingShipWorldObject as WorldObject;
            Scribe_References.Look(ref pendingWO, "pendingShipWorldObject");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                pendingShipWorldObject = pendingWO as TravelingObject;

            Scribe_Collections.Look(ref thrusterPlacements, "thrusterPlacements", LookMode.Reference);
            Scribe_Collections.Look(ref pendingThrusters, "pendingThrusters", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (thrusterPlacements == null) thrusterPlacements = new List<Thing>();
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
            thrusterPlacements = thrusters;
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

        // ── Rendering helpers ─────────────────────────────────────────────────
        public void BeginUpdate()
        {
            manualTicker.Push(Time.deltaTime);
            while (manualTicker.Pop())
                exhaustFleckSystem.Tick();
            exhaustFleckSystem.Update(Time.deltaTime);
        }

        public void EndUpdate()
        {
            exhaustFleckSystem.ForceDraw(drawBatch);
            drawBatch.Flush(true);
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
            pendingShipWorldObject.StartTraveling();
            initialTile = (pendingShipWorldObject).Tile;
            status = OrbitalTransferState.Working;
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

        // ── Private draw helpers ──────────────────────────────────────────────
        private void Draw(float cutsceneProgressPercent)
        {
            if (exhaustFleckSystem == null) return;

            BeginUpdate();

            Color color = Color.white * Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise1D(cutsceneProgressPercent * 100f));
            color.a = Mathf.InverseLerp(0f, 0.1f, cutsceneProgressPercent);
            MatGravshipLensFlare.SetColor(ShaderPropertyColor2, color);

            foreach (Thing thing in thrusterPlacements)
            {
                CompGravshipThruster comp = thing.TryGetComp<CompGravshipThruster>();
                if (comp == null) continue;

                CompProperties_GravshipThruster props = comp.Props;
                float flameSize = thing.def.size.x * props.flameSize;
                Vector3 flameOffset = thing.Rotation.AsQuat * props.flameOffsetsPerDirection[thing.Rotation.AsInt];
                Vector3 drawPos = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f)
                    - thing.Rotation.AsIntVec3.ToVector3()
                        * (thing.def.size.z * 0.5f + flameSize * 0.5f)
                    + flameOffset;
                drawPos = drawPos.SetToAltitude(AltitudeLayer.Skyfaller).WithYOffset(0.07317074f);

                Material flameMat = MaterialPool.MatFrom(new MaterialRequest(props.FlameShaderType.Shader)
                {
                    renderQueue = 3201
                });

                thrusterFlameBlock.Clear();
                thrusterFlameBlock.SetColor(ShaderPropertyColor2, color);
                foreach (ShaderParameter param in props.flameShaderParameters)
                    param.Apply(thrusterFlameBlock);

                GenDraw.DrawQuad(flameMat, drawPos, thing.Rotation.AsQuat, flameSize, thrusterFlameBlock);

                flareBlock.SetVector(ShaderPropertyIDs.DrawPos,
                    Find.Camera.WorldToViewportPoint(drawPos));
                DrawLayer(
                    MatGravshipLensFlare,
                    drawPos.SetToAltitude(AltitudeLayer.MetaOverlays).WithYOffset(0.03658537f),
                    flareBlock);

                CompProperties_GravshipThruster.ExhaustSettings ex = props.exhaustSettings;
                if (ex != null && ex.enabled
                    && exhaustTimers.TryGetValue(thing, out EventQueue eventQueue))
                {
                    eventQueue.Push(Time.deltaTime);
                    while (eventQueue.Pop())
                        EmitSmoke(ex, drawPos, Rot4.North.AsQuat, thing.Rotation.AsQuat);
                }
            }

            EndUpdate();
        }

        private void EmitSmoke(
            CompProperties_GravshipThruster.ExhaustSettings settings,
            Vector3 position,
            Quaternion gravshipRotation,
            Quaternion thrusterRotation)
        {
            Quaternion rotation = Quaternion.identity;
            if (settings.inheritThrusterRotation)
                rotation = thrusterRotation * rotation;
            if (settings.inheritGravshipRotation)
                rotation = gravshipRotation * rotation;

            exhaustFleckSystem.CreateFleck(new FleckCreationData
            {
                def = settings.ExhaustFleckDef,
                spawnPosition = position
                    + rotation * settings.spawnOffset
                    + UnityEngine.Random.insideUnitSphere.WithY(0f).normalized
                        * settings.spawnRadiusRange.RandomInRange,
                scale = settings.scaleRange.RandomInRange,
                velocity = rotation
                    * Quaternion.Euler(0f, settings.velocityRotationRange.RandomInRange, 0f)
                    * (settings.velocity * settings.velocityMultiplierRange.RandomInRange),
                rotationRate = settings.rotationOverTimeRange.RandomInRange,
                ageTicksOverride = -1
            });
        }

        private void DrawLayer(Material mat, Vector3 position, MaterialPropertyBlock props)
        {
            float size = Find.Camera.orthographicSize * 2f;
            Vector3 s = new Vector3(size * Find.Camera.aspect, 1f, size);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(position, Quaternion.identity, s), mat, 0, null, 0, props);
        }
    }
}