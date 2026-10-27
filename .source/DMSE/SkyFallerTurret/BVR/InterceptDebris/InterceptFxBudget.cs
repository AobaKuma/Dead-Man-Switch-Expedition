using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 攔截特效的密度設定與每張地圖的預算（docs/攔截殘骸特效_實作與技術美術計畫.md §8）。
    /// 密度（模組設定）縮放所有數量；預算在大規模齊射時再往下降：煙跡每 tick 有總量上限、細碎片減半或取消、
    /// 主殘骸不超過上限（到上限時不丟，屍體空投落點改用不生成的模擬殘骸預測）。
    /// P6 實機（驗收 I6）：高密度 40 發齊射，沒有煙跡上限時幀時間由 7.7 ms 升到 17.7 ms。
    /// </summary>
    public static class InterceptFxBudget
    {
        /// <summary>同時在空中的主殘骸上限（嚴格：任何一次攔截都不會讓總數超過它）。</summary>
        public const int MaxLiveChunks = 40;
        /// <summary>同時在空中的細碎片上限；超過一半時減半，到上限時不再丟。</summary>
        public const int MaxLiveMinor = 400;

        /// <summary>模組設定的密度等級（0 低、1 中、2 高）；測試環境沒有設定時為中。</summary>
        public static int Density => Mathf.Clamp(DMSEMod.Settings?.interceptFxDensity ?? 1, 0, 2);

        /// <summary>數量倍率：低 0.4、中 1、高 1.5。</summary>
        public static float DensityFactor => Density == 0 ? 0.4f : Density == 2 ? 1.5f : 1f;

        /// <summary>依密度縮放數量；<paramref name="min"/> 為下限。</summary>
        public static int Scale(int count, int min = 0) => Mathf.Max(min, Mathf.RoundToInt(count * DensityFactor));

        /// <summary>細碎片數：依密度縮放，再依目前在空中的數量減半或取消。</summary>
        public static int MinorCount(int wanted, int live)
        {
            if (live >= MaxLiveMinor) { return 0; }
            int n = Scale(wanted);
            return live >= MaxLiveMinor / 2 ? n / 2 : n;
        }

        /// <summary>主殘骸塊數：依密度縮放（至少 1），但不讓在空中的總數超過上限（到上限時為 0）。</summary>
        public static int MajorCount(int wanted, int live) => Mathf.Clamp(Scale(wanted, 1), 0, Mathf.Max(0, MaxLiveChunks - live));

        /// <summary>是否還能丟斷裂彈體：丟了之後總數仍不超過上限。</summary>
        public static bool AllowHulk(int liveAfterChunks) => liveAfterChunks < MaxLiveChunks;

        /// <summary>每張地圖每 tick 最多發出的煙跡團數：低 3、中 6、高 9（每團約活 170 tick，總量約 500–1500）。</summary>
        public static int TrailPuffsPerTick => Density == 0 ? 3 : Density == 2 ? 9 : 6;

        /// <summary>
        /// 煙跡間隔（tick）：基準值（一般 2、斷裂彈體 1），低密度加倍；在空中的主殘骸越多間隔越長（15 塊起 +1、30 塊起 +2）。
        /// </summary>
        public static int TrailInterval(int baseInterval, int liveChunks)
        {
            int interval = baseInterval * (Density == 0 ? 2 : 1);
            if (liveChunks >= 30) { interval += 2; }
            else if (liveChunks >= 15) { interval += 1; }
            return interval;
        }
    }
}
