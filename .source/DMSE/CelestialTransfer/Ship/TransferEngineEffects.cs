using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移引擎的火焰、鏡頭光暈與排氣 fleck 繪製（公式對照原版 GravshipRenderer）。
    /// <see cref="MapComponent_TransferBurn"/>（點火、暖機）與 <see cref="TransferTakeoffCutscene"/>（離場動畫，船體隨 offset 移動）共用。
    /// 每個使用者持有自己的實例（fleck 系統與排氣計時器各自獨立）。
    /// </summary>
    [StaticConstructorOnStartup]
    public class TransferEngineEffects
    {
        private static readonly Material MatLensFlare = MatLoader.LoadMat("Map/Gravship/GravshipLensFlare");

        private static readonly int ShaderPropertyColor2 = Shader.PropertyToID("_Color2");

        private readonly MaterialPropertyBlock flameBlock = new MaterialPropertyBlock();

        private readonly MaterialPropertyBlock flareBlock = new MaterialPropertyBlock();

        private readonly Dictionary<Thing, EventQueue> exhaustTimers = new Dictionary<Thing, EventQueue>();

        private readonly EventQueue manualTicker = new EventQueue(1f / 60f);

        private readonly DrawBatch drawBatch = new DrawBatch();

        private FleckSystem exhaustFleckSystem;

        /// <summary>推進方向：運作中引擎的推力加權朝向（火焰朝後，船往引擎朝向前進）；沒有運作中引擎時為 null。</summary>
        public static Vector3? ThrustDirection(TransferShipSurvey survey)
        {
            if (survey == null)
            {
                return null;
            }
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < survey.engines.Count; i++)
            {
                TransferShipSurvey.EngineEntry e = survey.engines[i];
                if (e.Operational)
                {
                    sum += e.engine.parent.Rotation.AsIntVec3.ToVector3() * e.engine.Props.thrust;
                }
            }
            sum.y = 0f;
            if (sum.sqrMagnitude < 0.0001f)
            {
                return null;
            }
            return sum.normalized;
        }

        /// <summary>每幀繪製前呼叫：推進排氣 fleck 的模擬（實際時間，暫停中也會動）。</summary>
        public void Begin(Map fleckMap)
        {
            if (exhaustFleckSystem == null)
            {
                exhaustFleckSystem = new FleckSystemThrown(fleckMap.flecks);
            }
            exhaustFleckSystem.parent = fleckMap.flecks;
            manualTicker.Push(Time.deltaTime);
            while (manualTicker.Pop())
            {
                exhaustFleckSystem.Tick();
            }
            exhaustFleckSystem.Update(Time.deltaTime);
        }

        public void End()
        {
            exhaustFleckSystem.ForceDraw(drawBatch);
            drawBatch.Flush(true);
        }

        /// <param name="flameScale">火焰長度與光暈強度（1 = 全推力；0 = 不畫火焰）。</param>
        /// <param name="exhaustRate">排氣 fleck 的發射比例。</param>
        /// <param name="offset">整艘船的位移（離場動畫用；地圖上的點火為 0）。</param>
        public void DrawEngines(TransferShipSurvey survey, float flameScale, float exhaustRate, Vector3 offset)
        {
            Color color = Color.white * Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise1D(Time.realtimeSinceStartup * 3f));
            color.a = 1f;
            MatLensFlare.SetColor(ShaderPropertyColor2, color * Mathf.Clamp01(flameScale));

            for (int i = 0; i < survey.engines.Count; i++)
            {
                TransferShipSurvey.EngineEntry e = survey.engines[i];
                if (!e.Operational)
                {
                    continue;
                }
                Thing thing = e.engine.parent;
                CompProperties_TransferEngine props = e.engine.Props;
                Rot4 rot = thing.Rotation;
                float fullLength = e.engine.FlameLength;
                float length = fullLength * flameScale;
                Vector3 drawPos = (e.engine.FlameCenter(length) + offset).SetToAltitude(AltitudeLayer.Skyfaller).WithYOffset(0.07317074f);

                if (length > 0.01f)
                {
                    Material mat = MaterialPool.MatFrom(new MaterialRequest(props.FlameShaderType.Shader) { renderQueue = 3201 });
                    flameBlock.Clear();
                    flameBlock.SetColor(ShaderPropertyColor2, color);
                    for (int j = 0; j < props.flameShaderParameters.Count; j++)
                    {
                        props.flameShaderParameters[j].Apply(flameBlock);
                    }
                    GenDraw.DrawQuad(mat, drawPos, rot.AsQuat, length, flameBlock);

                    flareBlock.SetVector(ShaderPropertyIDs.DrawPos, Find.Camera.WorldToViewportPoint(drawPos));
                    DrawScreenLayer(MatLensFlare, Find.Camera.transform.position.SetToAltitude(AltitudeLayer.MetaOverlays).WithYOffset(0.03658537f), flareBlock);
                }

                CompProperties_GravshipThruster.ExhaustSettings ex = props.exhaustSettings;
                if (ex == null || !ex.enabled || ex.ExhaustFleckDef == null || ex.emissionsPerSecond <= 0f || exhaustRate <= 0f)
                {
                    continue;
                }
                exhaustFleckSystem.handledDefs.AddUnique(ex.ExhaustFleckDef);
                EventQueue queue;
                if (!exhaustTimers.TryGetValue(thing, out queue))
                {
                    queue = new EventQueue(1f / ex.emissionsPerSecond);
                    exhaustTimers[thing] = queue;
                }
                queue.Push(Time.deltaTime);
                // 排氣從全推力時的火焰中心噴出；暖機時只發射一部分
                Vector3 smokePos = e.engine.FlameCenter(fullLength * Mathf.Max(flameScale, 0.2f)) + offset;
                while (queue.Pop())
                {
                    if (exhaustRate >= 1f || Random.value < exhaustRate)
                    {
                        EmitSmoke(ex, smokePos, rot.AsQuat);
                    }
                }
            }
        }

        private void EmitSmoke(CompProperties_GravshipThruster.ExhaustSettings settings, Vector3 position, Quaternion thrusterRotation)
        {
            Quaternion q = settings.inheritThrusterRotation ? thrusterRotation : Quaternion.identity;
            exhaustFleckSystem.CreateFleck(new FleckCreationData
            {
                def = settings.ExhaustFleckDef,
                spawnPosition = position + q * settings.spawnOffset + Random.insideUnitSphere.WithY(0f).normalized * settings.spawnRadiusRange.RandomInRange,
                scale = settings.scaleRange.RandomInRange,
                velocity = q * Quaternion.Euler(0f, settings.velocityRotationRange.RandomInRange, 0f) * (settings.velocity * settings.velocityMultiplierRange.RandomInRange),
                rotationRate = settings.rotationOverTimeRange.RandomInRange,
                ageTicksOverride = -1
            });
        }

        private static void DrawScreenLayer(Material mat, Vector3 position, MaterialPropertyBlock props)
        {
            float size = Find.Camera.orthographicSize * 2f;
            Vector3 scale = new Vector3(size * Find.Camera.aspect, 1f, size);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(position, Quaternion.identity, scale), mat, 0, null, 0, props);
        }
    }
}
