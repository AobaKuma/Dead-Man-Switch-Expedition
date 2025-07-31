using RimWorld.Planet;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using System.Diagnostics;
using static HarmonyLib.Code;

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
        public OrbitalTransferState status = OrbitalTransferState.Idle;
        public MapComponent_Ship(Map map) : base(map)
        {
            MapComponent_Ship.manualTicker = new EventQueue(0.0166666675f);
        }
        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (status is OrbitalTransferState.Working && this.wo != null && Find.CurrentMap == this.map
                && Find.World.renderer.wantedMode == WorldRenderMode.None)
            {
                Draw(Mathf.Clamp01(1f - this.wo.progress));
            }
        }
        private void PreWarmUp()
        {
            //TODO這部分用一個tickStamp來做發射前倒數計時與
        }
        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (status is OrbitalTransferState.Working && this.map.IsHashIntervalTick(5))
            {
                Find.CameraDriver.shaker.DoShake(0.3f);
                foreach (var pawn in this.map.mapPawns.AllPawns.ListFullCopy())
                {
                    if (pawn != null && pawn.Spawned)
                    {
                        foreach (var thing in this.thrusterPlacements)
                        {
                            CompProperties_GravshipThruster props = thing.TryGetComp<CompGravshipThruster>().Props;
                            float num = (float)thing.def.size.x * props.flameSize;
                            Vector3 b2 = thing.Rotation.AsQuat *
                                props.flameOffsetsPerDirection[thing.Rotation.AsInt];
                            Vector3 drawPos = GenThing.TrueCenter(thing.Position, thing.Rotation, thing.def.size, 0f)
                                - thing.Rotation.AsIntVec3.ToVector3() *
                                ((float)thing.def.size.z * 0.5f + num * 0.5f) + b2;
                            if (pawn.Position.DistanceTo(drawPos.ToIntVec3()) < thing.def.Size.Magnitude / 1.5f)
                            {
                                pawn.TakeDamage(new DamageInfo(DamageDefOf.Vaporize, 10f));
                            }
                        }
                    }
                }
            }
        }
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (status is OrbitalTransferState.Working)
            { 
                this.Init(((ThingWithComps)map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).First()).GetComp<CompAffectedByFacilities>()
                        .LinkedFacilitiesListForReading.FindAll(
                        thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0 &&
                        comp0.Props.componentTypeDef == PRDefOf.AAA), this.wo);
            }
        }
        public void Init(List<Thing> things, WorldObject_Transfer wo)
        {
            this.thrusterPlacements = things;
            this.exhaustFleckSystem = new FleckSystemThrown(this.map.flecks);
            this.exhaustTimers.Clear();
            foreach (Thing thing in this.thrusterPlacements)
            {
                CompGravshipThruster compGravshipThruster;
                if (thing.TryGetComp(out compGravshipThruster) && compGravshipThruster.Props.exhaustSettings != null && compGravshipThruster.Props.exhaustSettings.enabled && compGravshipThruster.Props.exhaustSettings.ExhaustFleckDef != null)
                {
                    this.exhaustFleckSystem.handledDefs.AddUnique(compGravshipThruster.Props.exhaustSettings.ExhaustFleckDef);
                    this.exhaustTimers.Add(thing, new EventQueue(1f / compGravshipThruster.Props.exhaustSettings.emissionsPerSecond));
                }
            }
            this.wo = wo;
        }
        public void End()
        {
            this.status = OrbitalTransferState.Cooling;
            this.thrusterPlacements.Clear();
            this.exhaustTimers.Clear();
            this.flareBlock.Clear();
        }
        private void Draw(float cutsceneProgressPercent)
        {
            this.BeginUpdate();
            Color color = new Color(1f, 1f, 1f, 1f);
            color *= Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise1D(cutsceneProgressPercent * 100f));
            color.a = Mathf.InverseLerp(0f, 0.1f, cutsceneProgressPercent);
            MapComponent_Ship.MatGravshipLensFlare.SetColor(MapComponent_Ship.ShaderPropertyColor2, color);
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
                this.thrusterFlameBlock.SetColor(MapComponent_Ship.ShaderPropertyColor2, color);
                foreach (ShaderParameter shaderParameter in props.flameShaderParameters)
                {
                    shaderParameter.Apply(this.thrusterFlameBlock);
                }
                var rotation = thing.Rotation;
                GenDraw.DrawQuad(material, drawPos, thing.Rotation.AsQuat, num, this.thrusterFlameBlock);
                this.flareBlock.SetVector(ShaderPropertyIDs.DrawPos, drawPos);
                this.DrawLayer(MapComponent_Ship.MatGravshipLensFlare, drawPos.SetToAltitude(AltitudeLayer.MetaOverlays).WithYOffset(0.03658537f), this.flareBlock);
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
            if (status is OrbitalTransferState.Working)
            { 
                Find.WorldCameraDriver.JumpTo(wo.DrawPos);
            } 
            this.EndUpdate();
        }
        public void BeginUpdate()
        {
            this.exhaustFleckSystem.parent = Find.CurrentMap.flecks;
            MapComponent_Ship.manualTicker.Push(Time.deltaTime);
            while (MapComponent_Ship.manualTicker.Pop())
            {
                this.exhaustFleckSystem.Tick();
            }
            this.exhaustFleckSystem.Update(Time.deltaTime);
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
        private Vector3 RotateAroundPivot(Vector3 position, Vector3 pivot, Quaternion rotation)
        {
            return rotation * (position - pivot) + pivot;
        }
        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                this.thrusterPlacements.RemoveAll(t => t == null || !t.Spawned);
            }
            Scribe_Values.Look(ref this.status, "workingStatus");
            Scribe_References.Look(ref this.wo, "wo");
            Scribe_Collections.Look(ref this.thrusterPlacements, "thrusterPlacements", LookMode.Reference);
        }

        public WorldObject_Transfer wo;
        public List<Thing> thrusterPlacements = new List<Thing>();
        private MaterialPropertyBlock thrusterFlameBlock = new MaterialPropertyBlock();
        private MaterialPropertyBlock flareBlock = new MaterialPropertyBlock();
        private Dictionary<Thing, EventQueue> exhaustTimers = new Dictionary<Thing, EventQueue>();
        private static readonly int ShaderPropertyColor2 = Shader.PropertyToID("_Color2");
        private FleckSystem exhaustFleckSystem;
        private static EventQueue manualTicker;
        private DrawBatch drawBatch = new DrawBatch();
        private static readonly Material MatGravshipLensFlare = MatLoader.LoadMat("Map/Gravship/GravshipLensFlare", -1);
    }
}