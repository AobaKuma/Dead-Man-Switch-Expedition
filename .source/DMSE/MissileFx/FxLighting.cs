using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 特效的環境光（煙與碎片要跟著日夜與燈光變暗變亮）。
    ///
    /// 為什麼不靠原版：原版的環境光是 <c>SectionLayer_LightingOverlay</c>（render queue 3100）把整張地圖乘上一層
    /// 「天空色＋燈光」；我們的特效為了蓋過戰爭迷霧（3175）畫在 3176 之後，也就在光照層之後，所以不會被它變暗。
    /// 兩者無法同時靠繪製順序滿足，所以由這裡自己算光，乘進特效顏色。
    ///
    /// 光的來源與原版光照層相同：天空色 <see cref="SkyManager.CurSky"/>.colors.sky（白天約白、夜晚偏暗藍；
    /// 生態系停用天空光照時為白），加上 <see cref="GlowGrid.VisualGlowAt(IntVec3)"/> 的燈光（換算比例同原版
    /// <see cref="GlowGrid.GroundGlowAt"/> 的 3.6；過亮格為白），逐色取大者。地圖外取最近的圖內格。
    /// 只用於「被照亮」的東西（煙、碎片、殘骸）；自己發光的（尾焰、光暈、閃光、火花、火球）不套用。
    /// </summary>
    public static class FxLighting
    {
        /// <summary>原版 GroundGlowAt 把燈光 0–255 換算成亮度的倍率。</summary>
        private const float GlowScale = 3.6f / 255f;

        /// <summary>最暗不低於這個值：全黑會讓煙與碎片在夜裡完全消失（原版的夜晚也不是全黑）。</summary>
        public const float MinLight = 0.12f;

        /// <summary>
        /// 取樣器：天空色每次建立時讀一次（每 tick／每幀建立一個，給大量粒子共用），燈光逐格查。
        /// <c>default</c>（沒有地圖）時一律回傳白。
        /// </summary>
        public readonly struct Sampler
        {
            private readonly Map map;
            private readonly Color sky;

            public Sampler(Map map)
            {
                this.map = map;
                sky = map == null || (map.Biome != null && map.Biome.disableSkyLighting) ? Color.white : map.skyManager.CurSky.colors.sky;
            }

            public Color At(Vector3 pos) => map == null ? Color.white : LightAt(map, sky, pos);
        }

        public static Sampler For(Map map) => new Sampler(map);

        /// <summary><paramref name="pos"/>（畫面位置，地圖格座標）的環境光；沒有地圖時為白。</summary>
        public static Color LightAt(Map map, Vector3 pos) => For(map).At(pos);

        private static Color LightAt(Map map, Color sky, Vector3 pos)
        {
            IntVec3 cell = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(pos.x), 0, map.Size.x - 1), 0, Mathf.Clamp(Mathf.FloorToInt(pos.z), 0, map.Size.z - 1));
            Color32 glow = map.glowGrid.VisualGlowAt(cell);
            Color local = glow.a == 1
                ? Color.white
                : new Color(Mathf.Min(1f, glow.r * GlowScale), Mathf.Min(1f, glow.g * GlowScale), Mathf.Min(1f, glow.b * GlowScale));
            return new Color(
                Mathf.Max(MinLight, Mathf.Max(sky.r, local.r)),
                Mathf.Max(MinLight, Mathf.Max(sky.g, local.g)),
                Mathf.Max(MinLight, Mathf.Max(sky.b, local.b)),
                1f);
        }

        /// <summary>
        /// 把環境光乘進顏色（透明度不變）。<paramref name="amount"/>：0 = 不受光（自己發光，例如剛炸開的熾熱碎片）、1 = 完全受光。
        /// </summary>
        public static Color Apply(Color color, Color light, float amount = 1f)
        {
            amount = Mathf.Clamp01(amount);
            return new Color(
                color.r * Mathf.Lerp(1f, light.r, amount),
                color.g * Mathf.Lerp(1f, light.g, amount),
                color.b * Mathf.Lerp(1f, light.b, amount),
                color.a);
        }
    }
}
