using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移飛行的平衡常數集中處（設計稿 §5-6）。由 <see cref="DMSE_DefOf.DMSE_TransferFlightTuning"/> 取用。
    /// </summary>
    public class TransferFlightTuningDef : Def
    {
        /// <summary>燃料基礎項 B：每單位質量權重的固定燃耗。</summary>
        public float fuelBasePerWeight = 60f;

        /// <summary>燃料距離項 P：每單位質量權重、每格距離的燃耗。</summary>
        public float fuelPerTilePerWeight = 12f;

        /// <summary>起飛所需最低推重比。</summary>
        public float minTWR = 0.5f;

        /// <summary>推重比為 <see cref="twrRef"/> 時的單次點火時數。</summary>
        public float burnHoursRef = 3f;

        public float twrRef = 1f;

        public float minBurnHours = 1f;

        public float maxBurnHours = 12f;

        /// <summary>滑行階段每格距離的時數。</summary>
        public float coastHoursPerTile = 2f;

        /// <summary>點火期間尾焰傷害的檢查間隔。</summary>
        public int exhaustDamageIntervalTicks = 60;

        /// <summary>船體範圍內的天然岩是否隨船帶走。</summary>
        public bool carryNaturalRock = true;

        /// <summary>結構網路 footprint 快取的有效期（tick）；超過後被查詢時重算。</summary>
        public int footprintCacheTicks = 250;

        /// <summary>撞擊飛行（設計稿 §9）所需的運作中撞擊用引擎（熱核推進器）數。</summary>
        public int impactMinEngines = 3;

        /// <summary>撞擊用引擎達此數量時為行星殺手結局（結束後回主選單）。</summary>
        public int planetkillerMinEngines = 4;

        /// <summary>撞擊前畫面淡出為白色的秒數（實際時間）。</summary>
        public float impactFadeSeconds = 5f;

        // ── 轉移後安全期（<see cref="TransferGraceUtility"/>） ──

        /// <summary>安全期門檻格數：船體不超過此格數時安全期為 <see cref="graceMaxDays"/>，超過則與格數成反比縮短。</summary>
        public int graceThresholdCells = 2000;

        /// <summary>難度係數 1 時的安全期上限（天）。</summary>
        public float graceMaxDays = 5f;

        /// <summary>難度係數：依說書人難度的 threatScale 換算，乘在安全期上（自訂難度同樣適用）。</summary>
        public SimpleCurve graceFactorByThreatScale = new SimpleCurve
        {
            new CurvePoint(0.1f, 1.5f),
            new CurvePoint(1f, 1f),
            new CurvePoint(2.2f, 0.5f)
        };
    }
}
