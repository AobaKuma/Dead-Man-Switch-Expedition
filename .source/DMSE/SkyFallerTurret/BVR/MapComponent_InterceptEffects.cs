using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 地圖特效類：在導彈/空投攔截成功時，於目標上方高空（地圖外）生成一閃光效與音效，
    /// 表現「在地圖外被攔截」。光效以 Graphics.DrawMesh 直接繪製，故可畫在地圖邊界之外。
    /// </summary>
    public class MapComponent_InterceptEffects : MapComponent
    {
        private struct Flash
        {
            public Vector3 pos;
            public float start;     // Time.realtimeSinceStartup
            public float duration;  // 秒
            public float maxScale;
            public Color color;
        }

        private readonly List<Flash> flashes = new List<Flash>();

        private static Material glowMat;
        private static MaterialPropertyBlock mpb;
        private static SoundDef defaultSound;
        private static bool resolvedSound;

        private int lastWeatherFlashTick = -9999;
        private const int WeatherFlashMinInterval = 12;

        public MapComponent_InterceptEffects(Map map) : base(map) { }

        /// <summary>外部呼叫入口：在 cell 上方生成攔截光效與音效。terminal 表示末端攔截（較低較近）。</summary>
        public static void Trigger(Map map, IntVec3 cell, bool terminal, SoundDef sound = null)
        {
            if (map == null) { return; }
            MapComponent_InterceptEffects comp = map.GetComponent<MapComponent_InterceptEffects>();
            if (comp != null) { comp.AddInterception(cell, terminal, sound); }
        }

        public void AddInterception(IntVec3 cell, bool terminal, SoundDef sound = null)
        {
            flashes.Add(new Flash
            {
                pos = ComputeSkyPos(cell, terminal),
                start = Time.realtimeSinceStartup,
                duration = terminal ? 0.6f : 0.85f,
                maxScale = terminal ? 3.5f : 6f,
                color = terminal ? new Color(1f, 0.8f, 0.35f, 1f) : new Color(0.85f, 0.8f, 0.5f, 1f)
            });

            SoundDef s = sound ?? DefaultSound;
            if (s != null)
            {
                s.PlayOneShot(SoundInfo.InMap(new TargetInfo(cell, map)));
            }

            // 攔截空爆閃光：用暖色爆炸閃光（與導彈爆炸同款），節流避免齊射過度閃爍。
            int nowTick = Find.TickManager.TicksGame;
            if (nowTick - lastWeatherFlashTick >= WeatherFlashMinInterval)
            {
                lastWeatherFlashTick = nowTick;
                Vector2 shadow = new Vector2(Rand.Range(-4f, 4f), Rand.Range(-4f, 0f));
                map.weatherManager.eventHandler.AddEvent(new WeatherEvent_MissileFlash(map, terminal ? 0.45f : 0.65f, shadow));
            }
        }

        /// <summary>目標正上方、偏離地圖（北方）的天空位置。</summary>
        private Vector3 ComputeSkyPos(IntVec3 cell, bool terminal)
        {
            float x = cell.x + 0.5f;
            float z = cell.z + (terminal ? 8f : 16f); // 往北（畫面上方）偏移到天空
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            return new Vector3(x, y, z);
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            // 清理（涵蓋非當前地圖，避免殘留）。
            if (flashes.Count == 0) { return; }
            float now = Time.realtimeSinceStartup;
            for (int i = flashes.Count - 1; i >= 0; i--)
            {
                if (now - flashes[i].start >= flashes[i].duration) { flashes.RemoveAt(i); }
            }
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (flashes.Count == 0) { return; }

            EnsureResources();
            float now = Time.realtimeSinceStartup;

            for (int i = flashes.Count - 1; i >= 0; i--)
            {
                Flash f = flashes[i];
                float t = (now - f.start) / f.duration;
                if (t >= 1f)
                {
                    flashes.RemoveAt(i);
                    continue;
                }

                float scale = f.maxScale * (0.55f + 0.45f * t);
                Color c = f.color;
                c.a = Mathf.Clamp01(1f - t);

                mpb.SetColor(ShaderPropertyIDs.Color, c);
                Matrix4x4 matrix = Matrix4x4.TRS(f.pos, Quaternion.identity, new Vector3(scale, 1f, scale));
                Graphics.DrawMesh(MeshPool.plane10, matrix, glowMat, 0, null, 0, mpb);
            }
        }

        private static void EnsureResources()
        {
            if (glowMat == null)
            {
                glowMat = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.MoteGlow,Color.white);
            }
            if (mpb == null)
            {
                mpb = new MaterialPropertyBlock();
            }
        }

        private static SoundDef DefaultSound
        {
            get
            {
                if (!resolvedSound)
                {
                    resolvedSound = true;
                    defaultSound = DefDatabase<SoundDef>.GetNamedSilentFail("DMSE_Missile_Shot");
                }
                return defaultSound;
            }
        }
    }
}
