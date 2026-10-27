using UnityEngine;

namespace DMSE
{
    /// <summary>
    /// 發射特效的密度與每張地圖的預算（docs/導彈發射特效_重製計畫.md §7，L6）。密度沿用模組設定「導彈特效密度」
    /// （<see cref="InterceptFxBudget.Density"/>，存檔鍵不變）。
    /// 煙柱是最大宗：每枚每 tick 最多 <see cref="MaxPuffsPerTick"/> 顆，全圖超過上限的 70% 後新煙柱壽命減半、間距加倍，
    /// 到上限時每枚每 tick 只發 1 顆。
    /// </summary>
    public static class LaunchFxBudget
    {
        public static int Density => InterceptFxBudget.Density;

        /// <summary>每枚導彈每 tick 最多發幾顆煙團：低 3、中 6、高 10。</summary>
        public static int MaxPuffsPerTick => Density == 0 ? 3 : Density == 2 ? 10 : 6;

        /// <summary>煙團壽命倍率：低 0.5、中 1、高 1.5（8 秒 → 4／8／12 秒）。</summary>
        public static float LifeFactor => Density == 0 ? 0.5f : Density == 2 ? 1.5f : 1f;

        /// <summary>全圖發射煙團上限：低 1500、中 3000、高 5000。</summary>
        public static int SmokeCap => Density == 0 ? 1500 : Density == 2 ? 5000 : 3000;

        /// <summary>依全圖煙團數決定本 tick 的壓力：0 = 正常、1 = 過七成（壽命減半、間距加倍）、2 = 到上限（每枚每 tick 1 顆）。</summary>
        public static int Pressure(int liveSmoke)
        {
            int cap = SmokeCap;
            if (liveSmoke >= cap) { return 2; }
            return liveSmoke >= cap * 0.7f ? 1 : 0;
        }

        /// <summary>一次性爆發（地面雲、煙環）的顆數：依密度縮放、到上限時減到 1/4。</summary>
        public static int BurstCount(int wanted, int liveSmoke)
        {
            int n = InterceptFxBudget.Scale(wanted, 1);
            return Pressure(liveSmoke) == 2 ? Mathf.Max(1, n / 4) : n;
        }
    }
}
