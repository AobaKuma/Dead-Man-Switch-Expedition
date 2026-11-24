using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 一個分導群組：同一枚匯流排放出的彈頭與誘餌。負責共同落點與戰果彙整
    /// （docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §6）。
    /// </summary>
    public class MirvGroup : IExposable
    {
        public int id = -1;
        public Faction attacker;
        public int missionId = -1;
        public int wave = -1;
        public int expectedWarheads;
        public int expectedDecoys;
        public int createdTick;
        public int lastEventTick;

        // ---- 共同落點 ----
        public Map aimMap;
        public IntVec3 aimCell = IntVec3.Invalid;
        public bool aimLoiter;
        /// <summary>電視導引：第一枚抵達者正在等玩家選點，其他成員要等。</summary>
        public bool aimPending;

        // ---- 彈頭戰果（世界層 → 地圖層）----
        public int intercepted;
        public int missed;
        public int arrived;
        public int hit;
        public int terminalKilled;
        public int shieldBlocked;

        // ---- 誘餌（世界層）----
        /// <summary>在世界層被攔截彈打下的誘餌數（防空白費的攔截彈）。</summary>
        public int decoysShotDown;
        /// <summary>被防空識破的誘餌數（每枚只算一次，不論被幾個陣營識破）。</summary>
        public int decoysIdentified;

        public bool HasAim(Map map) => map != null && aimMap == map && aimCell.IsValid;

        public void SetAim(Map map, IntVec3 cell, bool loiter)
        {
            aimMap = map;
            aimCell = cell;
            aimLoiter = loiter;
            aimPending = false;
        }

        /// <summary>有最終結局的彈頭數（攔截、落空，或抵達後已有地圖層結果）。</summary>
        public int Resolved => intercepted + missed + hit + terminalKilled + shieldBlocked;
        /// <summary>世界層已有結局的彈頭數（抵達也算）。</summary>
        public int WorldResolved => intercepted + missed + arrived;
        public bool Complete => Resolved >= expectedWarheads;

        public void Record(StrikeOutcome outcome)
        {
            switch (outcome)
            {
                case StrikeOutcome.Intercepted: intercepted++; break;
                case StrikeOutcome.Missed: missed++; break;
                case StrikeOutcome.Arrived: arrived++; break;
                case StrikeOutcome.Hit: hit++; break;
                case StrikeOutcome.TerminalKilled: terminalKilled++; break;
                case StrikeOutcome.ShieldBlocked: shieldBlocked++; break;
                default: return;
            }
            lastEventTick = Find.TickManager.TicksGame;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id", -1);
            Scribe_References.Look(ref attacker, "attacker");
            Scribe_Values.Look(ref missionId, "missionId", -1);
            Scribe_Values.Look(ref wave, "wave", -1);
            Scribe_Values.Look(ref expectedWarheads, "expectedWarheads", 0);
            Scribe_Values.Look(ref expectedDecoys, "expectedDecoys", 0);
            Scribe_Values.Look(ref createdTick, "createdTick", 0);
            Scribe_Values.Look(ref lastEventTick, "lastEventTick", 0);
            Scribe_References.Look(ref aimMap, "aimMap");
            Scribe_Values.Look(ref aimCell, "aimCell", IntVec3.Invalid);
            Scribe_Values.Look(ref aimLoiter, "aimLoiter", false);
            Scribe_Values.Look(ref aimPending, "aimPending", false);
            Scribe_Values.Look(ref intercepted, "intercepted", 0);
            Scribe_Values.Look(ref missed, "missed", 0);
            Scribe_Values.Look(ref arrived, "arrived", 0);
            Scribe_Values.Look(ref hit, "hit", 0);
            Scribe_Values.Look(ref terminalKilled, "terminalKilled", 0);
            Scribe_Values.Look(ref shieldBlocked, "shieldBlocked", 0);
            Scribe_Values.Look(ref decoysShotDown, "decoysShotDown", 0);
            Scribe_Values.Look(ref decoysIdentified, "decoysIdentified", 0);
        }
    }

    /// <summary>
    /// 分導群組登錄（存檔）。群組在所有彈頭都有結局後把整發導彈的戰果交給打擊任務一次，然後刪除；
    /// 抵達後遲遲沒有地圖層結果的（地圖被移除）在最後一次事件後 <see cref="WorldComponent_StrikeReports.StaleTicks"/> 結算，
    /// 超過 <see cref="MaxAgeTicks"/> 的群組無條件刪除。
    /// </summary>
    public class WorldComponent_MirvGroups : WorldComponent
    {
        public const int MaxAgeTicks = GenDate.TicksPerDay * 2;
        private const int CheckIntervalTicks = 250;

        private List<MirvGroup> groups = new List<MirvGroup>();

        public static WorldComponent_MirvGroups Current => Find.World?.GetComponent<WorldComponent_MirvGroups>();

        public WorldComponent_MirvGroups(World world) : base(world) { }

        public IReadOnlyList<MirvGroup> Groups => groups;

        public static MirvGroup Get(int id)
        {
            if (id < 0) { return null; }
            List<MirvGroup> list = Current?.groups;
            if (list == null) { return null; }
            for (int i = 0; i < list.Count; i++) { if (list[i].id == id) { return list[i]; } }
            return null;
        }

        /// <summary>匯流排開始釋放時建立群組（同一 id 已存在就沿用）。</summary>
        public MirvGroup Create(MissileWorldObject bus, int expectedWarheads, int expectedDecoys)
        {
            MirvGroup group = Get(bus.mirvGroupId);
            if (group != null) { return group; }
            int now = Find.TickManager.TicksGame;
            group = new MirvGroup
            {
                id = bus.mirvGroupId,
                attacker = bus.Faction,
                missionId = bus.MissionId,
                wave = bus.MissionWave,
                expectedWarheads = expectedWarheads,
                expectedDecoys = expectedDecoys,
                createdTick = now,
                lastEventTick = now,
            };
            groups.Add(group);
            return group;
        }

        /// <summary>世界層結局（攔截、干擾落空、抵達或落空）；只有群組裡的彈頭會記錄。</summary>
        public static void RecordWorldOutcome(MissileWorldObject track, StrikeOutcome outcome)
        {
            if (track == null) { return; }
            MirvGroup group = Get(track.mirvGroupId);
            if (group == null) { return; }
            if (track.mirvRole == MirvRole.Warhead) { group.Record(outcome); }
            else if (track.mirvRole == MirvRole.Decoy && outcome == StrikeOutcome.Intercepted)
            {
                group.decoysShotDown++;
                group.lastEventTick = Find.TickManager.TicksGame;
            }
        }

        /// <summary>誘餌第一次被任何陣營識破時記一次。</summary>
        public static void RecordDecoyIdentified(int groupId)
        {
            MirvGroup group = Get(groupId);
            if (group == null) { return; }
            group.decoysIdentified++;
            group.lastEventTick = Find.TickManager.TicksGame;
        }

        /// <summary>匯流排在放完前被毀：還沒放出的彈頭一起記成 <paramref name="outcome"/>。</summary>
        public static void RecordUnreleased(int groupId, int warheads, StrikeOutcome outcome)
        {
            MirvGroup group = Get(groupId);
            if (group == null) { return; }
            for (int i = 0; i < warheads; i++) { group.Record(outcome); }
        }

        /// <summary>
        /// 地圖層結局（命中、末端擊落、護盾擋下）。回傳 true 表示群組已接手，呼叫端不要再直接回報打擊任務。
        /// </summary>
        public static bool RecordMapOutcome(int groupId, StrikeOutcome outcome)
        {
            MirvGroup group = Get(groupId);
            if (group == null) { return false; }
            group.Record(outcome);
            return true;
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            if (groups.Count == 0) { return; }
            int now = Find.TickManager.TicksGame;
            if (now % CheckIntervalTicks != 0) { return; }
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                MirvGroup group = groups[i];
                bool stale = group.WorldResolved >= group.expectedWarheads
                    && now - group.lastEventTick >= WorldComponent_StrikeReports.StaleTicks;
                bool expired = now - group.createdTick >= MaxAgeTicks;
                if (!group.Complete && !stale && !expired) { continue; }
                if (AnyMemberAlive(group) && !group.Complete && !expired) { continue; }
                groups.RemoveAt(i);
                Finish(group);
            }
        }

        /// <summary>還有成員在世界層飛（或在等電視選點）：還不能用「逾時」結算。</summary>
        private static bool AnyMemberAlive(MirvGroup group)
        {
            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is MissileWorldObject m && !m.Destroyed && m.mirvGroupId == group.id) { return true; }
            }
            return false;
        }

        /// <summary>
        /// 把整發導彈的結局交給打擊任務：任一彈頭命中 = 抵達＋命中；有抵達但沒有命中 = 抵達＋（護盾擋下或末端擊落）；
        /// 沒有抵達 = 攔截（攔截數不少於落空數時）或落空。抵達後沒有結果的只記抵達，由任務的「結果不明」處理。
        /// </summary>
        private static void Finish(MirvGroup group)
        {
            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE MIRV] 群組 #{group.id} 結算：彈頭 {group.expectedWarheads}，攔截 {group.intercepted}、落空 {group.missed}、"
                    + $"抵達 {group.arrived}（命中 {group.hit}、末端擊落 {group.terminalKilled}、護盾 {group.shieldBlocked}）");
            }
            if (group.missionId < 0) { return; }
            int id = group.missionId, wave = group.wave;
            if (group.arrived > 0)
            {
                WorldComponent_StrikeReports.Report(id, wave, StrikeOutcome.Arrived);
                if (group.hit > 0) { WorldComponent_StrikeReports.Report(id, wave, StrikeOutcome.Hit); }
                else if (group.shieldBlocked > 0) { WorldComponent_StrikeReports.Report(id, wave, StrikeOutcome.ShieldBlocked); }
                else if (group.terminalKilled > 0) { WorldComponent_StrikeReports.Report(id, wave, StrikeOutcome.TerminalKilled); }
            }
            else
            {
                WorldComponent_StrikeReports.Report(id, wave,
                    group.intercepted >= group.missed && group.intercepted > 0 ? StrikeOutcome.Intercepted : StrikeOutcome.Missed);
            }
            WorldComponent_StrikeReports.Current?.AddNote(id, "DMSE.Strike.Report.Mirv".Translate(
                wave + 1, group.expectedWarheads, group.hit, group.intercepted, group.terminalKilled + group.shieldBlocked,
                group.missed).ToString());
            if (group.expectedDecoys > 0)
            {
                WorldComponent_StrikeReports.Current?.AddNote(id, "DMSE.Strike.Report.MirvDecoys".Translate(
                    group.expectedDecoys, group.decoysShotDown, group.decoysIdentified).ToString());
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref groups, "groups", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                groups ??= new List<MirvGroup>();
                groups.RemoveAll(g => g == null);
            }
        }
    }
}
