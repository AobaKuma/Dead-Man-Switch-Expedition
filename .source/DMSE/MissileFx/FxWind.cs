using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 特效用的地圖風（docs/導彈發射特效_重製計畫.md §0.1-2）。原版 <see cref="WindManager"/> 只有風速（0.04–2 × 天氣倍率），
    /// 沒有風向；這裡補一個每張地圖固定種子、隨遊戲時間緩慢擺動（±45°，週期 1 天）的風向。純函式，不需存檔。
    /// 只影響特效的飄移，不影響遊戲。
    /// </summary>
    public static class FxWind
    {
        /// <summary>風向擺動週期（tick）：1 天。</summary>
        private const float SwingPeriodTicks = 60000f;

        /// <summary>目前的風向（0 = 北、順時針，風「吹往」的方向）。</summary>
        public static float Heading(Map map)
        {
            int seed = Gen.HashCombineInt(map.uniqueID, 0x77a1d);
            float baseHeading = (seed & 0x7fffffff) % 360;
            float t = Find.TickManager.TicksAbs / SwingPeriodTicks;
            return baseHeading + 45f * Mathf.Sin(t * Mathf.PI * 2f + (seed & 0xff));
        }

        /// <summary>水平風向量（地圖格座標，y = 0）：方向 × 原版風速。</summary>
        public static Vector3 Vector(Map map)
        {
            if (map == null) { return Vector3.zero; }
            return MissileMapEntryUtility.HeadingVector(Heading(map)) * map.windManager.WindSpeed;
        }
    }
}
