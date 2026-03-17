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
        Cooling
    }

    public class MapComponent_Ship : MapComponent
    {
        // ── Serialized state ──────────────────────────────────────────────────
        public OrbitalTransferState status = OrbitalTransferState.Idle;
        public ITravelingShip shipWorldObject;
        public List<Thing> thrusterPlacements = new List<Thing>();

        // ── Warm-up bookkeeping ───────────────────────────────────────────────
        private const int WarmUpDurationTicks = GenTicks.TicksPerRealSecond * 5;
        private int warmUpTicksRemaining = 0;
        private float progressAtWorkStart = 0f;
        private List<Thing> pendingThrusters = new List<Thing>();
        private ITravelingShip pendingShipWorldObject;

        // ── Travel ────────────────────────────────────────────────────────────
        private PlanetTile initialTile = PlanetTile.Invalid;
        private const float TravelSpeed = 0.00025f;

        // ── Camera lerp ───────────────────────────────────────────────────────
        private const float CameraLerpDuration = 2f;
        private float cameraLerpProgress = 1f;
        private Quaternion cameraLerpStartRotation = Quaternion.identity;

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
                Log.Warning("[DMSE] MapComponent_Ship.FinalizeInit: no GravEngine found on map — resetting state.");
                status = OrbitalTransferState.Idle;
                return;
            }

            var engine = (ThingWithComps)engineThings[0];
            var facilities = engine.GetComp<CompAffectedByFacilities>()
                .LinkedFacilitiesListForReading
                .FindAll(t =>
                    t.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp &&
                    comp.Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster);

            Init(facilities, shipWorldObject);
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();

            if (status != OrbitalTransferState.Working
                || shipWorldObject == null
                || exhaustFleckSystem == null
                || Find.CurrentMap != map
                || !WorldRendererUtility.DrawingMap)
                return;

            float rawProgress = shipWorldObject.progress;
            float remapped = (rawProgress - progressAtWorkStart)
                / Mathf.Max(1f - progressAtWorkStart, 0.0001f);
            Draw(Mathf.Clamp01(1f - remapped));

            WorldCameraDriver cam = Find.WorldCameraDriver;
            Vector3 shipPos = ((WorldObject)shipWorldObject).DrawPos;
            Quaternion targetRotation = Quaternion.Inverse(Quaternion.LookRotation(-shipPos.normalized));

            if (cameraLerpProgress < 1f)
            {
                cameraLerpProgress = Mathf.Min(
                    cameraLerpProgress + Time.deltaTime / CameraLerpDuration, 1f);
                float t = cameraLerpProgress * cameraLerpProgress * (3f - 2f * cameraLerpProgress);
                cam.sphereRotation = Quaternion.Slerp(cameraLerpStartRotation, targetRotation, t);
            }
            else
            {
                cam.sphereRotation = targetRotation;
            }
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

                case OrbitalTransferState.Cooling:
                    // TODO: implement cooling-down behaviour.
                    status = OrbitalTransferState.Idle;
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
            Scribe_Values.Look(ref cameraLerpProgress, "cameraLerpProgress", 1f);
            Scribe_Values.Look(ref cameraLerpStartRotation, "cameraLerpStartRotation", Quaternion.identity);

            WorldObject shipWO = shipWorldObject as WorldObject;
            Scribe_References.Look(ref shipWO, "shipWorldObject");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                shipWorldObject = shipWO as ITravelingShip;

            WorldObject pendingWO = pendingShipWorldObject as WorldObject;
            Scribe_References.Look(ref pendingWO, "pendingShipWorldObject");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                pendingShipWorldObject = pendingWO as ITravelingShip;

            Scribe_Collections.Look(ref thrusterPlacements, "thrusterPlacements", LookMode.Reference);
            Scribe_Collections.Look(ref pendingThrusters, "pendingThrusters", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (thrusterPlacements == null) thrusterPlacements = new List<Thing>();
                if (pendingThrusters == null) pendingThrusters = new List<Thing>();
            }
        }

        // ── Public API ────────────────────────────────────────────────────────
        public void StartWarmUp(Thing core, List<Thing> thrusters, ITravelingShip wo)
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

        public void Init(List<Thing> thrusters, ITravelingShip wo)
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
            status = OrbitalTransferState.Cooling;
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

            progressAtWorkStart = pendingShipWorldObject.progress;
            cameraLerpStartRotation = Find.WorldCameraDriver.sphereRotation;
            cameraLerpProgress = 0f;
            initialTile = ((WorldObject)pendingShipWorldObject).Tile;
            Init(pendingThrusters, pendingShipWorldObject);
            pendingThrusters.Clear();
            pendingShipWorldObject = null;
            status = OrbitalTransferState.Working;
        }

        private void TickWorking()
        {
            Find.CameraDriver.shaker.DoShake(0.1f);

            // ── Drive travel progress ─────────────────────────────────
            if (shipWorldObject != null)
            {
                float step = ComputeTravelStep();
                shipWorldObject.progress = Mathf.Clamp01(shipWorldObject.progress + step);

                if (shipWorldObject.progress >= 1f)
                {
                    ITravelingShip arrived = shipWorldObject;
                    End();
                    arrived.Arrive();
                    return;
                }
            }

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

        private float ComputeTravelStep()
        {
            WorldObject wo = (WorldObject)shipWorldObject;
            Vector3 start = Find.WorldGrid.GetTileCenter(initialTile);
            Vector3 end = Find.WorldGrid.GetTileCenter(shipWorldObject.destinationTile);
            if (start == end)
                return 1f;
            float dist = GenMath.SphericalDistance(start.normalized, end.normalized);
            if (dist == 0f)
                return 1f;
            return TravelSpeed / dist;
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