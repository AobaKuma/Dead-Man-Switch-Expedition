using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 來襲導彈警報節流：同一種警報（<paramref name="key"/>）第一次送信件（ThreatBig 會暫停遊戲），
    /// <see cref="CooldownTicks"/> 內再發生時改用左上角訊息（不暫停），避免每一發導彈都跳一次信件。
    /// 計時不存檔；讀檔後頂多多送一封信件。
    /// </summary>
    public static class MissileAlertThrottle
    {
        /// <summary>同一種警報兩封信件之間至少間隔的 tick（1 遊戲小時）。</summary>
        public const int CooldownTicks = GenDate.TicksPerHour;

        private static readonly Dictionary<string, int> lastLetterTick = new Dictionary<string, int>();

        public static void Notify(string key, TaggedString label, TaggedString text, LetterDef letterDef, LookTargets lookTargets)
        {
            int now = Find.TickManager.TicksGame;
            // 讀了較早的存檔時 now 會倒退，視同冷卻已過。
            if (lastLetterTick.TryGetValue(key, out int last) && now >= last && now - last < CooldownTicks)
            {
                Messages.Message(label + ": " + text, lookTargets, MessageTypeDefOf.ThreatBig);
                return;
            }
            lastLetterTick[key] = now;
            Find.LetterStack.ReceiveLetter(label, text, letterDef, lookTargets);
        }

        /// <summary>測試用：清除冷卻。</summary>
        public static void Reset() => lastLetterTick.Clear();
    }
}
