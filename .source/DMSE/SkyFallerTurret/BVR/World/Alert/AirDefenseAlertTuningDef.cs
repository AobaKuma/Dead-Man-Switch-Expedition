using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// NPC 防空警戒五階段的可調參數（docs/NPC防空警戒五階段_執行計畫.md §3–§6）。
    /// 數值為首輪測試起點，不是已驗證的平衡值。
    /// </summary>
    public class AirDefenseAlertTuningDef : Def
    {
        /// <summary>關閉時 NPC 節點回到舊規則：全距離偵測、只攔截敵對陣營的導彈、不封鎖。</summary>
        public bool enabled = true;

        // ── 偵測與情報 ──
        /// <summary>第二階段觀測半徑 = 正常有效探測半徑 × 此值。</summary>
        public float observeRangeFactor = 0.4f;
        /// <summary>同陣營警報的支援半徑（世界格，同圖層）。</summary>
        public float supportRadiusTiles = 20f;
        /// <summary>航跡位置與目標情報的有效期限。</summary>
        public int intelExpiryTicks = 600;
        /// <summary>雷達由關閉（第一階段）轉開機所需時間。</summary>
        public int radarWarmupTicks = 600;

        // ── 事件權重（本地 L／區域 R） ──
        public float transitLocal = 4f, transitRegional = 2f;
        /// <summary>單次齊射的過境事件上限。</summary>
        public float transitSalvoCapLocal = 12f, transitSalvoCapRegional = 6f;
        public float targetedLocal = 20f, targetedRegional = 10f;
        public float hitLocal = 25f, hitRegional = 12f;
        public float repeatWaveLocal = 15f, repeatWaveRegional = 8f;
        /// <summary>「再次出現獨立攻擊波」與第五階段所看的攻擊紀錄窗口（24 遊戲小時）。</summary>
        public int attackWindowTicks = 60000;
        /// <summary>齊射分組：同來源、同目標區域、相隔不超過此值的航跡視為同一波。</summary>
        public int salvoGroupTicks = 600;

        // ── 門檻 ──
        public float stage3Up = 20f, stage3Down = 10f;
        public float stage4Up = 50f, stage4Down = 35f;
        public float stage5Up = 80f, stage5Down = 60f;
        public int stage5MinWaves = 2;
        /// <summary>壓力每遊戲小時衰減。</summary>
        public float decayPerHour = 2f;
        /// <summary>每階段至少維持多久才能降級。</summary>
        public int minStageHoldTicks = 15000;
        /// <summary>第五階段達標後的公告期（期間玩家可撤離）。</summary>
        public int blockadeNoticeTicks = 5000;
        /// <summary>同陣營同區域的封鎖信件合併冷卻。</summary>
        public int noticeCooldownTicks = 30000;

        // ── 據點基準 ──
        public int baselineNoRadar = 1;
        public int baselineRadar = 2;
        public int baselineMilitary = 3;

        // ── 封鎖 ──
        /// <summary>世界攔截（封鎖）半徑；與探測半徑分開。<see cref="CompProperties_WorldAirDefense.worldInterceptRange"/> 可覆寫。</summary>
        public float interceptRadiusTiles = 6f;

        public static AirDefenseAlertTuningDef Current
            => DefDatabase<AirDefenseAlertTuningDef>.GetNamedSilentFail("DMSE_AirDefenseAlertTuning") ?? fallback;
        private static readonly AirDefenseAlertTuningDef fallback = new AirDefenseAlertTuningDef();

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (observeRangeFactor < 0f || supportRadiusTiles < 0f || intelExpiryTicks < 1 || radarWarmupTicks < 0)
                yield return "Air-defense alert detection values must be non-negative (intel expiry at least 1).";
            if (!(stage3Down < stage3Up && stage4Down < stage4Up && stage5Down < stage5Up && stage3Up <= stage4Up && stage4Up <= stage5Up))
                yield return "Air-defense alert thresholds need down < up per stage and rising up thresholds.";
            if (baselineNoRadar < 1 || baselineMilitary > 4 || baselineRadar < baselineNoRadar || baselineMilitary < baselineRadar)
                yield return "Air-defense alert baselines must satisfy 1 <= noRadar <= radar <= military <= 4.";
        }
    }
}
