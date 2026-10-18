using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>一枚已升空打擊導彈的導引期：期間占用發射方地圖的一條火控通道。</summary>
    public class StrikeGuidanceHold : IExposable
    {
        public Faction faction;
        public int releaseTick;

        public void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref releaseTick, "releaseTick");
        }
    }

    /// <summary>漣波發射佇列中的一枚：已占用導引通道，到 <see cref="fireTick"/> 時由指揮台發射。</summary>
    public class StrikeQueuedLaunch : IExposable
    {
        public ThingWithComps console;
        public int missionId;
        public int wave;
        public int slot;
        public int fireTick;
        /// <summary>同一波次同平台的後續各枚：略過冷卻檢查。</summary>
        public bool salvoContinuation;

        public void ExposeData()
        {
            Scribe_References.Look(ref console, "console");
            Scribe_Values.Look(ref missionId, "missionId");
            Scribe_Values.Look(ref wave, "wave");
            Scribe_Values.Look(ref slot, "slot");
            Scribe_Values.Look(ref fireTick, "fireTick");
            Scribe_Values.Look(ref salvoContinuation, "salvoContinuation");
        }
    }

    /// <summary>
    /// 波次打擊的地圖管理器（docs/BVR_波次打擊規劃.md §4-1）：
    ///   - 世界選靶發射平台與打擊指揮台的註冊表（各自在 PostSpawnSetup／PostDeSpawn 註冊）；
    ///   - 打擊保留索引：彈藥 Thing → 擁有者（打擊任務 id）。不存檔：指揮台在 PostSpawnSetup 依任務重建，
    ///     開發者模式的手動保留（<see cref="DebugOwner"/>）讀檔後即消失；
    ///   - 漣波發射佇列與導引期（存檔），兩者都計入火控通道帳本的 Strike 分項。
    /// </summary>
    public class MapComponent_StrikePlanner : MapComponent
    {
        /// <summary>開發者模式手動保留使用的擁有者；打擊任務 id 從 1 起算。</summary>
        public const int DebugOwner = 0;

        public readonly HashSet<CompMissileLauncher_WorldTargeting> launchers = new HashSet<CompMissileLauncher_WorldTargeting>();
        public readonly HashSet<CompStrikeConsole> consoles = new HashSet<CompStrikeConsole>();

        private readonly Dictionary<Thing, int> reservations = new Dictionary<Thing, int>();
        private int nextMissionId = 1;
        private List<StrikeGuidanceHold> holds = new List<StrikeGuidanceHold>();
        private List<StrikeQueuedLaunch> queue = new List<StrikeQueuedLaunch>();

        public MapComponent_StrikePlanner(Map map) : base(map) { }

        /// <summary>任務 id 由世界層配發（全域唯一，戰果帳與航跡標記都用它）；沒有世界時退回本地計數。</summary>
        public int NewMissionId() => WorldComponent_StrikeReports.Current?.NewMissionId() ?? nextMissionId++;

        // ---------------- 保留 ----------------

        /// <summary>彈藥的保留擁有者；未保留（或已銷毀）時回傳 -1。</summary>
        public int OwnerOf(Thing round)
        {
            if (round == null || !reservations.TryGetValue(round, out int owner)) { return -1; }
            if (round.Destroyed)
            {
                reservations.Remove(round);
                return -1;
            }
            return owner;
        }

        public bool IsReserved(Thing round) => OwnerOf(round) >= 0;

        /// <summary>保留彈藥；已被其他擁有者保留時失敗，同一擁有者重複保留視為成功。</summary>
        public bool TryReserve(Thing round, int owner)
        {
            if (round == null || round.Destroyed || owner < 0) { return false; }
            int current = OwnerOf(round);
            if (current >= 0 && current != owner) { return false; }
            reservations[round] = owner;
            return true;
        }

        public void Release(Thing round)
        {
            if (round != null) { reservations.Remove(round); }
        }

        public void ReleaseAll(int owner)
        {
            foreach (Thing round in reservations.Where(kv => kv.Value == owner).Select(kv => kv.Key).ToList())
            {
                reservations.Remove(round);
            }
        }

        /// <summary>某擁有者目前保留的彈藥（略過已銷毀者）。</summary>
        public List<Thing> ReservedBy(int owner)
        {
            return reservations.Where(kv => kv.Value == owner && !kv.Key.Destroyed).Select(kv => kv.Key).ToList();
        }

        // ---------------- 導引通道 ----------------

        /// <summary>某陣營排在佇列中（已訂通道）與仍在導引期內的打擊導彈數。</summary>
        public int GuidanceOccupied(Faction faction)
        {
            if (faction == null) { return 0; }
            int now = Find.TickManager.TicksGame;
            return holds.Count(h => h.faction == faction && h.releaseTick > now)
                + queue.Count(q => q.console != null && q.console.Faction == faction);
        }

        /// <summary>最早一條導引期結束的剩餘 tick；沒有導引中的導彈時回傳 -1。</summary>
        public int TicksUntilNextRelease(Faction faction)
        {
            int now = Find.TickManager.TicksGame;
            int best = -1;
            foreach (StrikeGuidanceHold h in holds)
            {
                if (h.faction != faction || h.releaseTick <= now) { continue; }
                int left = h.releaseTick - now;
                if (best < 0 || left < best) { best = left; }
            }
            return best;
        }

        /// <summary>某陣營仍在導引期內的釋放 tick（時間軸估算用）；排隊中的視為立即升空。</summary>
        public IEnumerable<int> GuidanceReleaseTicks(Faction faction)
        {
            int now = Find.TickManager.TicksGame;
            int guidance = BVRWorldTuningDef.Current.strikeGuidanceTicks;
            foreach (StrikeGuidanceHold h in holds) { if (h.faction == faction && h.releaseTick > now) { yield return h.releaseTick; } }
            foreach (StrikeQueuedLaunch q in queue) { if (q.console != null && q.console.Faction == faction) { yield return System.Math.Max(now, q.fireTick) + guidance; } }
        }

        public void AddGuidanceHold(Faction faction, int releaseTick)
        {
            holds.Add(new StrikeGuidanceHold { faction = faction, releaseTick = releaseTick });
        }

        // ---------------- 發射佇列 ----------------

        public void Enqueue(StrikeQueuedLaunch launch) => queue.Add(launch);

        public int QueuedCount(int missionId) => queue.Count(q => q.missionId == missionId);

        /// <summary>取消任務時移除其佇列（釋放已訂的通道）。</summary>
        public void DropQueued(int missionId) => queue.RemoveAll(q => q.missionId == missionId);

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            int now = Find.TickManager.TicksGame;
            if (holds.Count > 0) { holds.RemoveAll(h => h.releaseTick <= now || h.faction == null); }
            if (queue.Count > 0)
            {
                List<StrikeQueuedLaunch> due = queue.Where(q => q.fireTick <= now).OrderBy(q => q.fireTick).ToList();
                foreach (StrikeQueuedLaunch launch in due)
                {
                    queue.Remove(launch);
                    CompStrikeConsole console = launch.console?.GetComp<CompStrikeConsole>();
                    console?.ExecuteQueued(launch, this);
                }
            }
            if (now % 60 == 0)
            {
                foreach (CompStrikeConsole console in consoles.ToList()) { console.MissionTick(now); }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextMissionId, "nextMissionId", 1);
            Scribe_Collections.Look(ref holds, "guidanceHolds", LookMode.Deep);
            Scribe_Collections.Look(ref queue, "launchQueue", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                holds ??= new List<StrikeGuidanceHold>();
                queue ??= new List<StrikeQueuedLaunch>();
                holds.RemoveAll(h => h == null || h.faction == null);
                queue.RemoveAll(q => q == null || q.console == null);
            }
        }
    }
}
