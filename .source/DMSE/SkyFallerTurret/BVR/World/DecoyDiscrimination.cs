using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 誘餌識別（docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §7.3）：火控鎖定完成時擲一次，每個陣營對每個誘餌只擲一次。
    /// 識破的陣營不再對它鎖定或發射；失敗則之後一直把它當真目標。CIWS 不經鎖定，所以不識別。
    /// </summary>
    public static class DecoyDiscrimination
    {
        /// <summary>同一陣營的識破訊息最短間隔，齊射時不洗版。</summary>
        private const int MessageIntervalTicks = 300;
        private static int lastMessageTick = -MessageIntervalTicks;

        /// <summary>
        /// 修正鏈之前的識別率：雷達的 <see cref="CompProperties_FireControlRadar.decoyDiscrimination"/>
        /// × <see cref="BVRWorldTuningDef.decoyFidelityFactor"/>^擬真等級 × (1 + 每級功率加成 × (功率 − 1))。
        /// </summary>
        public static float BaseChance(CompProperties_FireControlRadar radar, int fidelity)
        {
            if (radar == null || radar.decoyDiscrimination <= 0f) { return 0f; }
            BVRWorldTuningDef tuning = BVRWorldTuningDef.Current;
            float chance = radar.decoyDiscrimination
                * Mathf.Pow(Mathf.Clamp(tuning.decoyFidelityFactor, 0f, 1f), Mathf.Max(0, fidelity))
                * (1f + tuning.decoyPowerBonusPerLevel * Mathf.Max(0, radar.powerLevel - 1));
            return Mathf.Clamp01(chance);
        }

        public static bool IsDecoy(object track, out int fidelity)
        {
            if (track is IDecoyTrack decoy && decoy.IsDecoy)
            {
                fidelity = decoy.DecoyFidelity;
                return true;
            }
            fidelity = 0;
            return false;
        }

        /// <summary>地圖層載荷（落地彈）是不是誘餌。</summary>
        public static bool IsDecoy(Thing payload, out int fidelity)
        {
            DecoyProps props = (payload as IIncomingMissile)?.Config?.body?.decoy;
            fidelity = props?.fidelity ?? 0;
            return props != null;
        }

        /// <summary>識破後給防守方玩家的訊息（節流）。攻擊方不會被告知。</summary>
        public static void NotifyIdentified(Faction defender, string label, GlobalTargetInfo target)
        {
            if (defender == null || !defender.IsPlayer) { return; }
            int now = Find.TickManager.TicksGame;
            if (now - lastMessageTick >= 0 && now - lastMessageTick < MessageIntervalTicks) { return; }
            lastMessageTick = now;
            Messages.Message("DMSE.BVR.World.DecoyIdentified".Translate(label), target, MessageTypeDefOf.PositiveEvent, historical: false);
        }
    }
}
