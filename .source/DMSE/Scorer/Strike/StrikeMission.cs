using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>打擊任務狀態（docs/BVR_波次打擊規劃.md §3）。</summary>
    public enum StrikeMissionState
    {
        /// <summary>規劃視窗中的草稿，尚未保留導彈。</summary>
        Planning,
        /// <summary>已保留導彈，彈道計算中（自動版為機器計算倒數）。</summary>
        AwaitingComputation,
        /// <summary>彈道解完成，可以發射。</summary>
        Ready,
        /// <summary>至少一波已發射，仍有待發射的導彈。</summary>
        Executing,
        Completed,
        Cancelled,
    }

    public enum StrikeSlotState
    {
        /// <summary>保留中，等待所屬波次發射。</summary>
        Pending,
        /// <summary>已排入漣波發射佇列，已占用一條導引通道。</summary>
        Queued,
        Launched,
        /// <summary>發射前重驗失敗（彈藥不在、超出射程、改裝過…），原因見 <see cref="StrikeSlot.skipReason"/>。</summary>
        Skipped,
    }

    /// <summary>波次中的一枚導彈：平台建築 + 彈藥（Rail 為容器中的導彈；彈道井／Scorer 為平台本身）。</summary>
    public class StrikeSlot : IExposable
    {
        public ThingWithComps launcher;
        public Thing round;
        /// <summary>保留時的發射設定指紋（<see cref="StrikeRoundUtility.ConfigHash"/>），發射前比對以發現改裝。</summary>
        public int configHash;
        /// <summary>此枚的彈道解已算好。</summary>
        public bool solved;
        public StrikeSlotState state = StrikeSlotState.Pending;
        public string skipReason;
        public int launchTick = -1;
        /// <summary>升空後的世界航跡（追蹤用；航跡結束後存檔時清空）。</summary>
        public ScorerProjectile_WorldObject track;

        public CompMissileLauncher_WorldTargeting LauncherComp => launcher?.GetComp<CompMissileLauncher_WorldTargeting>();

        /// <summary>尚未結束（保留中或已排入佇列）：彈藥應維持保留。</summary>
        public bool Open => state == StrikeSlotState.Pending || state == StrikeSlotState.Queued;

        public bool InFlight => track != null && !track.Destroyed;

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // 已銷毀的物件不在存檔裡，不寫入懸空參照
                if (round != null && round.Destroyed) { round = null; }
                if (launcher != null && launcher.Destroyed) { launcher = null; }
                if (track != null && track.Destroyed) { track = null; }
            }
            Scribe_References.Look(ref launcher, "launcher");
            Scribe_References.Look(ref round, "round");
            Scribe_Values.Look(ref configHash, "configHash");
            Scribe_Values.Look(ref solved, "solved");
            Scribe_Values.Look(ref state, "state", StrikeSlotState.Pending);
            Scribe_Values.Look(ref skipReason, "skipReason");
            Scribe_Values.Look(ref launchTick, "launchTick", -1);
            Scribe_References.Look(ref track, "track");
        }
    }

    public class StrikeWave : IExposable
    {
        public List<StrikeSlot> slots = new List<StrikeSlot>();
        /// <summary>首次發射的 tick；-1 = 尚未發射。</summary>
        public int launchedTick = -1;
        /// <summary>定時發射（P4）：輪到本波時自動發射，但要等到它會在前面各波之後抵達。</summary>
        public bool autoFire;

        public int Count(StrikeSlotState state) => slots.Count(s => s.state == state);
        public bool Open => slots.Any(s => s.Open);
        public bool HasPending => slots.Any(s => s.state == StrikeSlotState.Pending);

        public void ExposeData()
        {
            Scribe_Collections.Look(ref slots, "slots", LookMode.Deep);
            Scribe_Values.Look(ref launchedTick, "launchedTick", -1);
            Scribe_Values.Look(ref autoFire, "autoFire");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && slots == null) { slots = new List<StrikeSlot>(); }
        }
    }

    /// <summary>
    /// 打擊任務：一個目標、若干波次。任務存在指揮台（<see cref="CompStrikeConsole"/>）上，
    /// 保留索引不存檔，讀檔後由任務重建（<see cref="MapComponent_StrikePlanner"/>）。
    /// </summary>
    public class StrikeMission : IExposable
    {
        public int id;
        public PlanetTile targetTile = PlanetTile.Invalid;
        /// <summary>選靶處的據點；null 表示打擊固定 tile（抵達時可能落空）。</summary>
        public MapParent targetParent;
        /// <summary>選靶時有據點：之後據點消失即視為目標丟失（存檔時已銷毀的參照會被清空，所以另記一個旗標）。</summary>
        public bool targetedSite;
        public StrikeMissionState state = StrikeMissionState.Planning;
        public List<StrikeWave> waves = new List<StrikeWave>();
        /// <summary>自動版機器計算完成的 tick。</summary>
        public int computeDoneTick = -1;
        /// <summary>目前彈道解所對應的目標位置；目標移動太遠時需重算（P4）。</summary>
        public PlanetTile solutionTile = PlanetTile.Invalid;
        /// <summary>人工版：已投入的彈道計算工作量（研究速度 × tick），只供顯示。</summary>
        public float computeWorkDone;
        /// <summary>人工版：尚未完成的基礎工作量、已累積但還不夠解出下一枚的工作量、每枚所需工作量（皆已乘上跨圖層倍率）。</summary>
        public float computeBaseLeft;
        public float computeCredit;
        public float computePerRound;

        /// <summary>尚未發射且尚未解出的導彈（依波次順序）。</summary>
        public IEnumerable<StrikeSlot> UnsolvedOpen => AllSlots.Where(s => s.Open && !s.solved);

        /// <summary>人工版總工作量 = 已投入 + 剩餘；加入或移除導彈時自動跟著變。</summary>
        public float ComputeWorkTotal => computeWorkDone
            + StrikeComputation.Remaining(computeBaseLeft, computeCredit, computePerRound, UnsolvedOpen.Count());
        public int createdTick;

        public IEnumerable<StrikeSlot> AllSlots => waves.SelectMany(w => w.slots);

        public int RoundCount => waves.Sum(w => w.slots.Count);

        /// <summary>發射時的目標：據點會移動（轉移船、陣地轉移），以其當下 tile 為準。</summary>
        public GlobalTargetInfo Target => targetParent != null && !targetParent.Destroyed
            ? new GlobalTargetInfo(targetParent) : new GlobalTargetInfo(targetTile);

        public PlanetTile CurrentTargetTile => targetParent != null && !targetParent.Destroyed ? targetParent.Tile : targetTile;

        public string TargetLabel => targetParent != null && !targetParent.Destroyed ? targetParent.LabelCap : targetTile.ToString();

        /// <summary>目標據點已不存在（選靶時有據點，之後被摧毀或撤離）。</summary>
        public bool TargetLost => targetedSite && (targetParent == null || targetParent.Destroyed);

        public bool IsActive => state == StrikeMissionState.AwaitingComputation || state == StrikeMissionState.Ready
            || state == StrikeMissionState.Executing;

        /// <summary>下一個仍有待發射導彈的波次；沒有時回傳 -1。</summary>
        public int NextWaveIndex
        {
            get
            {
                for (int i = 0; i < waves.Count; i++)
                {
                    if (waves[i].HasPending) { return i; }
                }
                return -1;
            }
        }

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && targetParent != null && targetParent.Destroyed) { targetParent = null; }
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref targetTile, "targetTile", PlanetTile.Invalid);
            Scribe_References.Look(ref targetParent, "targetParent");
            Scribe_Values.Look(ref targetedSite, "targetedSite");
            Scribe_Values.Look(ref state, "state", StrikeMissionState.Planning);
            Scribe_Collections.Look(ref waves, "waves", LookMode.Deep);
            Scribe_Values.Look(ref computeDoneTick, "computeDoneTick", -1);
            Scribe_Values.Look(ref solutionTile, "solutionTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref computeWorkDone, "computeWorkDone");
            Scribe_Values.Look(ref computeBaseLeft, "computeBaseLeft");
            Scribe_Values.Look(ref computeCredit, "computeCredit");
            Scribe_Values.Look(ref computePerRound, "computePerRound");
            Scribe_Values.Look(ref createdTick, "createdTick");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && waves == null) { waves = new List<StrikeWave>(); }
        }
    }
}
