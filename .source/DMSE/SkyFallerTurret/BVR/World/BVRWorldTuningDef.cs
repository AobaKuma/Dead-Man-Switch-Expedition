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
        /// <summary>攔截高度的邊緣寬度（倍率）：離上下限不到這個倍率時命中率下降。</summary>
        public float altitudeEdgeRatio = 1.5f;
        // ---- 地圖層波次（原 BVRTuning 常數）----
        /// <summary>預測抵達時間相差在此 ticks 內的目標合併到同一個攔截窗口（同一波次）。</summary>
        public int waveMergeTicks = 600;
        /// <summary>波次合併時，每多一個目標整體攔截窗口縮短的 ticks。</summary>
        public int windowPenaltyPerExtraTarget = 60;
        /// <summary>搜索雷達功率對預警窗口的指數（大於 1 即超線性）。</summary>
        public float searchPowerWindowExponent = 2f;
        /// <summary>地圖層：反隱身不足時預警窗口的折扣係數（世界層是 <see cref="stealthDetectFactor"/>）。</summary>
        public float mapStealthWindowFactor = 0.4f;
        /// <summary>地圖層：反隱身每高出目標隱身 1 級，預警窗口的加成比例。</summary>
        public float mapAntiStealthWindowBonusPerLevel = 0.5f;
        /// <summary>盤旋結束後留給末端防禦接戰的俯衝時間（ticks）。</summary>
        public int loiterDiveTicks = 600;
        /// <summary>剛好在攔截高度上下限時的命中率係數。</summary>
        public float altitudeEdgeHitFactor = 0.6f;
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
        // ---- 功率段位（CompPowerThrottle）：調變都乘在結果上，滿段為 1。----
        public float throttleWindowFloor = 0.4f;
        public float throttleRangeFloor = 0.4f;
        public float throttleHitFloor = 0.6f;
        public float throttleLockPenalty = 0.5f;
        // ---- 干擾場對敵方雷達（EWEnvironment）----
        public float jamLevelScaling = 2f;
        public float jamDetectionPenalty = 0.6f;
        public float jamMinDetectionFactor = 0.15f;
        public float jamLockPenalty = 0.8f;
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
