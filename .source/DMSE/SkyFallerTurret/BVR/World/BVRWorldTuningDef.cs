using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public class BVRWorldTuningDef : Def
    {
        public float ticksPerTileAtSpeed1 = 150f;
        public float interceptorSpeedMultiplier = 3f;
        public float stealthDetectFactor = 0.4f;
        public float antiStealthRangeBonusPerLevel = 0.25f;
        public float maxAntiStealthRangeMultiplier = 1.5f;
        public int minInterceptMarginTicks = 60;
        public float airburstRadiusTiles = 1f;
        public int scanIntervalTicks = 15;
        /// <summary>高度換算的參考高度（公尺）：等於此高度時偵測窗口係數為 1。</summary>
        public float altitudeRefMeters = 1000f;
        /// <summary>最低高度係數（極低空，例如掠海導彈）。</summary>
        public float minAltitudeFactor = 0.6f;
        /// <summary>最高高度係數（彈道／軌道目標）。</summary>
        public float maxAltitudeFactor = 1.3f;
        /// <summary>箔條每次觸發的斷鎖機率係數（實際機率 = 此值 × 部件 jamStrength）。</summary>
        public float chaffBreakChance = 0.5f;
        /// <summary>箔條成功斷鎖後，防空重新鎖定的延遲（tick）。</summary>
        public int chaffLockDelayTicks = 300;
        /// <summary>地圖層（末端）干擾對命中率的折扣係數。</summary>
        public float chaffMapHitPenalty = 0.3f;
        /// <summary>P4：空投是否另外建立世界層虛擬航跡（開啟後地圖內只保留末端 CIWS）。</summary>
        public bool podsUseWorldTracks = false;
        /// <summary>P4：區域防禦（節點可攔截飛越自己覆蓋範圍、目標是同陣營其他據點的航跡）。</summary>
        public bool areaDefenseEnabled = true;
        /// <summary>Strike guidance period: a launched strike missile holds a fire-control channel this long.</summary>
        public int strikeGuidanceTicks = 600;
        /// <summary>Wave strike: suggested arrival gap after the previous wave when a wave would arrive too early.</summary>
        public int strikeArrivalSpacingTicks = 60;
        /// <summary>Wave strike: firing solutions are recomputed once the target has moved this many tiles (0 = never).</summary>
        public float strikeRecomputeMoveTiles = 5f;
        public static BVRWorldTuningDef Current => DefDatabase<BVRWorldTuningDef>.GetNamedSilentFail("DMSE_BVRWorldTuning") ?? fallback;
        private static readonly BVRWorldTuningDef fallback = new BVRWorldTuningDef();
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (ticksPerTileAtSpeed1 <= 0 || interceptorSpeedMultiplier <= 0 || scanIntervalTicks < 1
                || minInterceptMarginTicks < 0 || airburstRadiusTiles < 0 || strikeGuidanceTicks < 0)
                yield return "BVR motion, interval and range values must be positive (margin/radius may be zero).";
        }
    }
}
