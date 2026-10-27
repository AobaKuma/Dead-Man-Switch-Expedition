using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>一顆漂浮煙團的參數（docs/導彈發射特效_重製計畫.md §4.2）。單位：格、tick。</summary>
    public struct SmokePuffSpec
    {
        public FleckDef def;
        /// <summary>畫面位置（地圖格座標；高度已含在 z 裡）。</summary>
        public Vector3 pos;
        /// <summary>初速（格/tick，y = 0）。</summary>
        public Vector3 velocity;
        /// <summary>初始大小（格）。</summary>
        public float size;
        /// <summary>膨脹量：大小 = size × (1 + growth × √(age / life))。</summary>
        public float growth;
        public int lifeTicks;
        /// <summary>每 tick 速度衰減比例。</summary>
        public float drag;
        /// <summary>每 tick 往畫面上方（+z）加的速度：熱煙上升。</summary>
        public float buoyancy;
        /// <summary>風向量（<see cref="FxWind.Vector"/>）每 tick 加到速度上的比例。</summary>
        public float windFactor;
        /// <summary>剛發出時的顏色（被尾焰照亮）；<see cref="hotTicks"/> 內漸變到 <see cref="color"/>。</summary>
        public Color hotColor;
        public Color color;
        public int hotTicks;
        public float rotation;
        /// <summary>自轉（度/tick）。</summary>
        public float spin;
        /// <summary>出生時已過的 tick（同一 tick 沿線段發出的多顆，讓前面的看起來較舊）。</summary>
        public int ageOffset;
    }

    /// <summary>
    /// 漂浮煙團（docs/導彈發射特效_重製計畫.md §3、§4.2）：純視覺、不存檔，每 tick 依速度、阻力、浮力與風推進，
    /// 隨年齡膨脹、變色、淡出，壽命到或飄出地圖就移除。和 <see cref="InterceptDebrisFlecks"/> 一樣，每顆持有一個
    /// <see cref="FleckStatic"/> 只負責繪製（FleckDef 用 Mote shader＋renderInstanced 時自動走 GPU instancing）；
    /// 淡入淡出由這裡用 instanceColor.a 控制，FleckDef 本身給很長的 solidTime 即可。
    /// </summary>
    public class FxSmokeParticles
    {
        private struct Puff
        {
            public FleckStatic fleck;
            public SmokePuffSpec spec;
            public Vector3 pos;
            public Vector3 velocity;
            public int age;
        }

        /// <summary>淡入（tick）。</summary>
        public const int FadeInTicks = 3;
        /// <summary>壽命的這個比例之後開始淡出。</summary>
        public const float FadeOutStart = 0.4f;
        /// <summary>
        /// 繪製高度在 def 的高度之上依「出生先後」再往上墊的總範圍（以 <see cref="Altitudes.AltInc"/> 計）。
        /// 同材質超過 1023 顆時 DrawBatch 會拆成多次 instanced 繪製，Unity 依各次的包圍盒高度排序；墊高後舊的那批一定先畫。
        /// 小於煙柱與地面雲 def 高度的差（0.4），兩類不會交錯。
        /// </summary>
        public const float OrderAltitudeSpan = 0.3f;

        /// <summary>飄到地圖外框外超過這個距離（格）就移除：地圖外被原版邊緣遮罩蓋住。</summary>
        public const float OffMapCull = 4f;

        private readonly List<Puff> puffs = new List<Puff>();
        private readonly List<FleckDef> handledDefs = new List<FleckDef>();

        public int Count => puffs.Count;

        /// <summary>移除原因統計（單元測試用）。</summary>
        public int expiredCount, culledCount;

        /// <summary>
        /// 加一顆煙團。<see cref="FleckStatic.Setup"/> 會用 Rand 並讀遊戲 tick：呼叫端必須已 PushState（純視覺，不擾動遊戲亂數）。
        /// </summary>
        public void Spawn(SmokePuffSpec spec)
        {
            if (spec.def == null || spec.lifeTicks <= 0) { return; }
            handledDefs.AddUnique(spec.def);
            var fleck = new FleckStatic();
            fleck.Setup(new FleckCreationData
            {
                def = spec.def,
                spawnPosition = spec.pos,
                scale = spec.size,
                rotation = spec.rotation,
                instanceColor = spec.hotTicks > 0 ? spec.hotColor : spec.color,
                ageTicksOverride = -1
            });
            var puff = new Puff { fleck = fleck, spec = spec, pos = spec.pos, velocity = spec.velocity, age = Mathf.Max(0, spec.ageOffset) };
            Apply(ref puff, default);
            puffs.Add(puff);
        }

        /// <summary>
        /// 每遊戲 tick 推進一次。<paramref name="map"/> 有值時套用環境光（<see cref="FxLighting"/>：煙在光照層之後畫，原版光照照不到它）；
        /// 剛出噴口的熾熱期是被尾焰照亮的，漸進套用。
        /// </summary>
        public void Tick(IntVec3 mapSize, Vector3 wind, Map map = null)
        {
            FxLighting.Sampler light = FxLighting.For(map);
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                Puff p = puffs[i];
                Step(ref p.pos, ref p.velocity, p.spec, wind);
                p.age++;
                if (p.age >= p.spec.lifeTicks)
                {
                    expiredCount++;
                    puffs.RemoveAt(i);
                    continue;
                }
                if (InterceptGeometry.DistanceOutside(mapSize, new Vector3(p.pos.x, 0f, p.pos.z)) > OffMapCull)
                {
                    culledCount++;
                    puffs.RemoveAt(i);
                    continue;
                }
                Apply(ref p, light);
                puffs[i] = p;
            }
        }

        /// <summary>一 tick 的運動（純函式，單元測試用）：阻力、浮力（畫面 +z）、風，然後位移。</summary>
        public static void Step(ref Vector3 pos, ref Vector3 velocity, in SmokePuffSpec spec, Vector3 wind)
        {
            velocity *= 1f - Mathf.Clamp01(spec.drag);
            velocity += new Vector3(wind.x, 0f, wind.z) * spec.windFactor + new Vector3(0f, 0f, spec.buoyancy);
            pos += velocity;
        }

        /// <summary>清單第 <paramref name="index"/> 顆（共 <paramref name="count"/> 顆）的繪製高度（純函式）：def 高度再往上墊，越新越高。</summary>
        public static float OrderedAltitude(FleckDef def, int index, int count)
            => def.altitudeLayer.AltitudeFor(def.altitudeLayerIncOffset + OrderAltitudeSpan * index / Mathf.Max(1, count));

        /// <summary>年齡對應的大小（純函式）。</summary>
        public static float SizeAt(in SmokePuffSpec spec, int age)
            => spec.size * (1f + spec.growth * Mathf.Sqrt(Mathf.Clamp01((float)age / Mathf.Max(1, spec.lifeTicks))));

        /// <summary>年齡對應的透明度倍率（純函式）：<see cref="FadeInTicks"/> 淡入、壽命 <see cref="FadeOutStart"/> 後平滑淡出。</summary>
        public static float AlphaAt(in SmokePuffSpec spec, int age)
        {
            float fadeIn = Mathf.Clamp01((age + 1f) / FadeInTicks);
            float t = (float)age / Mathf.Max(1, spec.lifeTicks);
            float fadeOut = t <= FadeOutStart ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (t - FadeOutStart) / (1f - FadeOutStart));
            return fadeIn * fadeOut;
        }

        private static void Apply(ref Puff p, FxLighting.Sampler light)
        {
            float size = SizeAt(p.spec, p.age);
            p.fleck.linearScale = new Vector3(size, 1f, size);
            p.fleck.position.worldPosition = p.pos;
            p.fleck.exactRotation = p.spec.rotation + p.spec.spin * p.age;
            float cooled = p.spec.hotTicks > 0 ? Mathf.Clamp01((float)p.age / p.spec.hotTicks) : 1f;
            Color c = Color.Lerp(p.spec.hotColor, p.spec.color, cooled);
            c = FxLighting.Apply(c, light.At(p.pos), cooled);
            c.a *= AlphaAt(p.spec, p.age);
            p.fleck.instanceColor = c;
        }

        /// <summary>
        /// 繪製。<paramref name="view"/> 有值時只畫在視野矩形（外擴已由呼叫端處理）內的煙團：
        /// 視野外的照常推進但不送出繪製（docs/導彈發射特效_重製計畫.md §7）。
        /// 先後順序（docs/導彈發射特效_重製計畫.md §0.5）：清單依出生先後（移除不改順序），同一材質的 instanced 繪製依加入順序畫，
        /// 所以舊的在下、新的在上；高度再依清單位置墊一點（<see cref="OrderAltitudeSpan"/>），讓拆開的多批也照這個順序。
        /// 每種煙只用一張貼圖：同高度的不同材質之間的先後由 Unity 任意決定，會一塊塊亂蓋、逐幀跳動。
        /// </summary>
        public void Draw(DrawBatch batch, CellRect? view = null)
        {
            if (puffs.Count == 0) { return; }
            for (int i = 0; i < handledDefs.Count; i++)
            {
                handledDefs[i].graphicData?.ExplicitlyInitCachedGraphic();
                if (handledDefs[i].randomGraphics != null)
                {
                    foreach (GraphicData g in handledDefs[i].randomGraphics) { g.ExplicitlyInitCachedGraphic(); }
                }
            }
            for (int i = 0; i < puffs.Count; i++)
            {
                if (view.HasValue)
                {
                    Vector3 p = puffs[i].pos;
                    float r = puffs[i].fleck.linearScale.x * 0.5f;
                    CellRect v = view.Value;
                    if (p.x + r < v.minX || p.x - r > v.maxX + 1 || p.z + r < v.minZ || p.z - r > v.maxZ + 1) { continue; }
                }
                FleckStatic f = puffs[i].fleck;
                f.Draw(OrderedAltitude(f.def, i, puffs.Count), batch);
            }
        }
    }
}
