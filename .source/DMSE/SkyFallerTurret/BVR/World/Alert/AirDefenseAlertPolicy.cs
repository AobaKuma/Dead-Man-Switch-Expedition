using RimWorld;
using UnityEngine;

namespace DMSE
{
    /// <summary>警戒階段；數字與玩家看到的編號一致。</summary>
    public enum AirDefenseStage
    {
        NoDetection = 1,
        EarlyWarning = 2,
        Passive = 3,
        Active = 4,
        Blockade = 5,
    }

    /// <summary>升降級判定的輸入（純資料，方便單元測試）。</summary>
    public struct AirDefenseStageInputs
    {
        public int current;
        public int baseline;
        /// <summary>有效壓力 T = max(L, 0.5 × R)。</summary>
        public float threat;
        public bool hasRadar;
        public bool directThreat;
        /// <summary>最近 24 遊戲小時內指向本地／支援區的獨立攻擊波數。</summary>
        public int recentWaves;
        /// <summary>目前階段已維持到最短持續時間。</summary>
        public bool holdElapsed;
    }

    /// <summary>
    /// 純規則（§3、§5.3）：友敵識別、可觀測、可接戰與升降級。不讀寫狀態、不消耗彈藥。
    /// </summary>
    public static class AirDefenseAlertPolicy
    {
        private static AirDefenseAlertTuningDef Tuning => AirDefenseAlertTuningDef.Current;

        /// <summary>同陣營或盟友：一律免受本系統攔截及封鎖。</summary>
        public static bool IsFriendly(Faction defender, Faction other)
        {
            if (defender == null || other == null) { return false; }
            if (defender == other) { return true; }
            return defender.RelationWith(other, allowNull: true)?.kind == FactionRelationKind.Ally;
        }

        /// <summary>非盟友：中立與敵對都算，不能寫成僅 <c>HostileTo</c>。無陣營（未知）不算。</summary>
        public static bool IsNonAlly(Faction defender, Faction other) => other != null && defender != null && !IsFriendly(defender, other);

        /// <summary>NPC 會觀測非盟友與未知來源的導彈；同陣營／盟友的導彈不產生情報。</summary>
        public static bool MayObserve(Faction defender, Faction attacker) => defender != null && (attacker == null || IsNonAlly(defender, attacker));

        /// <summary>探測半徑係數：第一階段沒有本地探測，第二階段只開小範圍，雷達暖機中為 0。</summary>
        public static float DetectionFactor(int stage, bool radarWarm)
        {
            if (stage <= (int)AirDefenseStage.NoDetection || !radarWarm) { return 0f; }
            return stage == (int)AirDefenseStage.EarlyWarning ? Tuning.observeRangeFactor : 1f;
        }

        /// <summary>
        /// 導彈交戰規則（§3）：第三階段只攔截確認指向防守陣營基地的來襲導彈；第四、五階段攔截非盟友導彈（含中立過境）。
        /// 未知來源只在確認攻擊己方時接戰。友方導彈指向己方座標不算攻擊。
        /// </summary>
        public static bool MayEngage(int stage, Faction defender, Faction attacker, Faction targetFaction)
        {
            if (defender == null) { return false; }
            if (attacker != null && IsFriendly(defender, attacker)) { return false; }
            if (stage < (int)AirDefenseStage.Passive) { return false; }
            bool targetsOwn = targetFaction == defender;
            if (stage == (int)AirDefenseStage.Passive || attacker == null) { return targetsOwn; }
            return true;
        }

        /// <summary>舊規則（關閉警戒系統或玩家節點）：只處理敵對陣營。</summary>
        public static bool LegacyMayEngage(Faction defender, Faction attacker) => defender != null && defender.HostileTo(attacker);

        /// <summary>
        /// 下一個命令階段（§5.3）。升級優先於降級；降級每次一階、不低於基準、需維持最短時間。
        /// 回傳 5 代表「應下達封鎖命令」，公告延遲由呼叫端處理。沒有雷達的據點停在基準。
        /// </summary>
        public static int NextStage(AirDefenseStageInputs i)
        {
            AirDefenseAlertTuningDef t = Tuning;
            int cur = Mathf.Clamp(i.current, 1, 5);
            int baseline = Mathf.Clamp(i.baseline, 1, 4);
            if (!i.hasRadar) { return baseline; }

            int up = cur;
            if (up < 2 && i.threat >= 1f) { up = 2; }
            if (i.threat >= t.stage3Up || i.directThreat) { up = Mathf.Max(up, 3); }
            if (i.threat >= t.stage4Up) { up = Mathf.Max(up, 4); }
            if (i.threat >= t.stage5Up && i.recentWaves >= t.stage5MinWaves) { up = 5; }
            if (up > cur) { return up; }
            if (!i.holdElapsed) { return Mathf.Max(cur, baseline); }

            int down = cur;
            switch (cur)
            {
                case 5:
                    if (BlockadeShouldLift(i.threat, i.recentWaves)) { down = 4; }
                    break;
                case 4:
                    if (i.threat < t.stage4Down) { down = 3; }
                    break;
                case 3:
                    if (i.threat < t.stage3Down && !i.directThreat) { down = 2; }
                    break;
                case 2:
                    if (i.threat < 1f) { down = 1; }
                    break;
            }
            return Mathf.Max(down, baseline);
        }

        /// <summary>封鎖（含公告期）解除條件：壓力低於下門檻，或本地攻擊紀錄已過期。</summary>
        public static bool BlockadeShouldLift(float threat, int recentWaves) => threat < Tuning.stage5Down || recentWaves <= 0;

        /// <summary>同航跡升格時只補差額（§5.2）：回傳應加的量並更新已入帳權重。</summary>
        public static float TopUp(ref float applied, float weight)
        {
            if (weight <= applied) { return 0f; }
            float delta = weight - applied;
            applied = weight;
            return delta;
        }

        /// <summary>壓力依經過時間線性衰減。</summary>
        public static float Decayed(float value, int elapsedTicks)
            => Mathf.Max(0f, value - Tuning.decayPerHour * Mathf.Max(0, elapsedTicks) / 2500f);

        public static float EffectiveThreat(float local, float regional) => Mathf.Max(local, 0.5f * regional);
    }
}
