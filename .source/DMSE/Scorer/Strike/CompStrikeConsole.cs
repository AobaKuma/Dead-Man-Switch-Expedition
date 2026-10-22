using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_StrikeConsole : CompProperties
    {
        /// <summary>人工版：確認後需由會研究的殖民者在此完成彈道計算（§8-2）。</summary>
        public bool requiresComputation = false;

        /// <summary>人工版的基礎計算工作量（研究速度 1 的 tick 數）。</summary>
        public float computeWorkBase = 1500f;

        /// <summary>人工版每枚導彈的計算工作量。</summary>
        public float computeWorkPerRound = 400f;

        /// <summary>目標在另一圖層（地表 ↔ 軌道）時的工作量倍率。</summary>
        public float crossLayerWorkFactor = 1.5f;

        /// <summary>自動版的機器計算時間（tick）；指揮台不可運作時暫停。0 = 確認後立即就緒。</summary>
        public int autoComputeTicks = 300;

        public int maxWavesPerMission = 5;

        public int maxRoundsPerMission = 20;

        public CompProperties_StrikeConsole()
        {
            compClass = typeof(CompStrikeConsole);
        }
    }

    [StaticConstructorOnStartup]
    public static class StrikeTextures
    {
        public static readonly Texture2D Cancel = ContentFinder<Texture2D>.Get("UI/Designators/Cancel");
    }

    /// <summary>
    /// 打擊指揮台（docs/BVR_波次打擊規劃.md §3、§7、§8）：世界選靶 → 規劃視窗分配波次 → 確認後保留導彈並計算彈道 →
    /// 逐波發射。每枚發射前重驗，漣波發射由 <see cref="MapComponent_StrikePlanner"/> 的佇列執行，
    /// 排隊與升空後的導引期都占用火控通道（<see cref="BVRChannelLedger.Strike"/>）。
    /// 可運作條件沿用 <see cref="CompBVRDevice"/>（通電、開關、未損壞、未暈眩、需操作時有人操作）。
    /// </summary>
    public class CompStrikeConsole : CompBVRDevice
    {
        public StrikeMission mission;

        public CompProperties_StrikeConsole Props => (CompProperties_StrikeConsole)props;

        private MapComponent_StrikePlanner Planner => parent.MapHeld?.GetComponent<MapComponent_StrikePlanner>();

        private Faction Side => parent.Faction;

        public int StrikeCapacity => Manager?.Channels.StrikeCapacity(Side) ?? 0;

        public int StrikeFree => Manager?.Channels.StrikeFree(Side) ?? 0;

        // ==================================================================
        //  規劃
        // ==================================================================

        public string CannotPlanReason()
        {
            if (!Active) { return "DMSE.BVR.DeviceInactive".Translate(); }
            if (StrikeCapacity <= 0) { return "DMSE.Strike.NoFireControl".Translate(); }
            MapComponent_StrikePlanner planner = Planner;
            if (planner == null || !planner.launchers.Any(l => l.parent.Faction == Side && l.StrikeRounds.Any()))
            {
                return "DMSE.Strike.NoStrikeRounds".Translate();
            }
            return null;
        }

        private static MapParent TargetParentAt(GlobalTargetInfo t)
        {
            return t.WorldObject as MapParent ?? Find.WorldObjects.MapParentAt(t.Tile);
        }

        /// <summary>選靶驗證，同發射平台：排除水域與已有非 MapParent 世界物件的 tile。</summary>
        private static bool CanTargetTile(GlobalTargetInfo t)
        {
            if (!t.IsValid || !t.Tile.Valid || t.Tile.Tile.PrimaryBiome.isWaterBiome) { return false; }
            return !(Find.World.worldObjects.WorldObjectAt<WorldObject>(t.Tile) is WorldObject wo) || wo is MapParent;
        }

        private void CountReach(PlanetTile target, out int reachable, out int loaded)
        {
            List<StrikeCandidate> list = StrikeRoundUtility.Candidates(parent.Map, target, -1, out int elsewhere);
            reachable = list.Count(c => c.Assignable);
            loaded = list.Count + elsewhere;
        }

        public void BeginTargeting()
        {
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(parent), CameraJumper.MovementMode.Pan);
            Find.WorldTargeter.BeginTargeting(t =>
            {
                if (!CanTargetTile(t)) { return false; }
                CountReach(t.Tile, out int reachable, out _);
                if (reachable == 0)
                {
                    Messages.Message("DMSE.Strike.NoRoundReaches".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                OpenPlanner(t);
                return true;
            },
            canTargetTiles: true,
            mouseAttachment: CompLaunchable.TargeterMouseAttachment,
            closeWorldTabWhenFinished: true,
            onUpdate: null,
            extraLabelGetter: t =>
            {
                if (!t.IsValid || parent.Map == null) { return string.Empty; }
                CountReach(t.Tile, out int reachable, out int loaded);
                string label = "DMSE.Strike.TargetLabel.Distance".Translate(
                    Mathf.RoundToInt(MissileWorldUtility.RangeDistance(parent.Map.Tile, t.Tile)))
                    + "\n" + "DMSE.Strike.TargetLabel.Reach".Translate(reachable, loaded);
                if (TargetParentAt(t) == null) { label += "\n" + "DMSE.MissileLauncher.NoTargetHere".Translate(); }
                return label;
            },
            canSelectTarget: null,
            originForClosest: null,
            showCancelButton: true);
        }

        private void OpenPlanner(GlobalTargetInfo t)
        {
            MapParent site = TargetParentAt(t);
            var draft = new StrikeMission
            {
                targetTile = t.Tile,
                targetParent = site,
                targetedSite = site != null,
                state = StrikeMissionState.Planning,
                createdTick = Find.TickManager.TicksGame,
                name = StrikeMissionNamer.RandomName(),
            };
            draft.waves.Add(new StrikeWave());
            CameraJumper.TryJump(parent);
            Find.WindowStack.Add(new Dialog_StrikePlan(this, draft));
        }

        /// <summary>草稿能否確認；可以時回傳 null。</summary>
        public string ValidateDraft(StrikeMission draft)
        {
            int cap = Mathf.Max(1, StrikeCapacity);
            int rounds = draft.RoundCount;
            if (rounds == 0) { return "DMSE.Strike.NoRoundsAssigned".Translate(); }
            if (rounds > Props.maxRoundsPerMission) { return "DMSE.Strike.TooManyRounds".Translate(Props.maxRoundsPerMission); }
            if (draft.waves.Count(w => w.slots.Count > 0) > Props.maxWavesPerMission)
            {
                return "DMSE.Strike.TooManyWaves".Translate(Props.maxWavesPerMission);
            }
            for (int i = 0; i < draft.waves.Count; i++)
            {
                if (StrikeRoundUtility.PendingChannels(draft.waves[i]) > cap)
                {
                    return "DMSE.Strike.WaveOverCap".Translate(i + 1, StrikeRoundUtility.PendingChannels(draft.waves[i]), cap);
                }
            }
            return null;
        }

        /// <summary>確認計畫：保留全部導彈、記下設定指紋，進入彈道計算（§8-3／§8-4）。</summary>
        public bool Commit(StrikeMission draft, out string reason)
        {
            reason = ValidateDraft(draft);
            MapComponent_StrikePlanner planner = Planner;
            if (reason == null && (planner == null || mission != null)) { reason = "DMSE.BVR.DeviceInactive".Translate(); }
            if (reason != null) { return false; }

            draft.waves.RemoveAll(w => w.slots.Count == 0);
            draft.id = planner.NewMissionId();
            PlanetTile from = parent.Map.Tile;
            foreach (StrikeSlot slot in draft.AllSlots)
            {
                CompMissileLauncher_WorldTargeting launcher = slot.LauncherComp;
                MissileConfig config = launcher?.BuildFiringConfig(slot.round);
                if (launcher == null || !launcher.OwnsRound(slot.round)
                    || !MissileWorldUtility.CanEngage(config, from, draft.CurrentTargetTile, 0f, out _))
                {
                    reason = "DMSE.Strike.RoundUnavailable".Translate();
                }
                else if (!planner.TryReserve(slot.round, draft.id))
                {
                    reason = "DMSE.Strike.RoundTaken".Translate();
                }
                if (reason != null)
                {
                    planner.ReleaseAll(draft.id);
                    return false;
                }
                slot.configHash = StrikeRoundUtility.ConfigHash(config);
                slot.state = StrikeSlotState.Pending;
                slot.solved = false;
            }

            int now = Find.TickManager.TicksGame;
            mission = draft;
            mission.state = StrikeMissionState.AwaitingComputation;
            mission.solutionTile = mission.CurrentTargetTile;
            if (Props.requiresComputation)
            {
                float factor = CrossLayer(mission) ? Props.crossLayerWorkFactor : 1f;
                mission.computeDoneTick = -1;
                mission.computeWorkDone = 0f;
                mission.computeBaseLeft = Props.computeWorkBase * factor;
                mission.computeCredit = 0f;
                mission.computePerRound = Props.computeWorkPerRound * factor;
                Messages.Message("DMSE.Strike.Msg.ConfirmedManual".Translate(mission.Label, mission.RoundCount, mission.waves.Count,
                    parent.LabelShort), parent, MessageTypeDefOf.TaskCompletion, false);
                ApplyComputationProgress(0f);
            }
            else
            {
                mission.computeDoneTick = now + Mathf.Max(0, Props.autoComputeTicks);
                Messages.Message("DMSE.Strike.Msg.Confirmed".Translate(mission.Label, mission.RoundCount, mission.waves.Count),
                    parent, MessageTypeDefOf.TaskCompletion, false);
                if (Props.autoComputeTicks <= 0) { FinishComputation(); }
            }
            return true;
        }

        /// <summary>自動版到時、或開發者模式：全部彈道解完成。</summary>
        private void FinishComputation()
        {
            foreach (StrikeSlot slot in mission.AllSlots) { slot.solved = true; }
            mission.computeBaseLeft = 0f;
            mission.computeCredit = 0f;
            if (mission.state == StrikeMissionState.AwaitingComputation) { mission.state = StrikeMissionState.Ready; }
            Messages.Message("DMSE.Strike.Msg.Ready".Translate(mission.Label), parent, MessageTypeDefOf.PositiveEvent, false);
        }

        // ---------------- 人工彈道計算（§8-2） ----------------

        private bool CrossLayer(StrikeMission m) => !MissileWorldUtility.SameLayer(parent.Map.Tile, m.CurrentTargetTile);

        /// <summary>某計畫的人工計算總工作量。</summary>
        public float WorkFor(StrikeMission m)
        {
            return StrikeComputation.TotalWork(Props.computeWorkBase, Props.computeWorkPerRound,
                CrossLayer(m) ? Props.crossLayerWorkFactor : 1f, m.RoundCount);
        }

        /// <summary>人工版且仍有未解的待發射導彈：WorkGiver 會派研究者來計算。</summary>
        public bool NeedsManualComputation => Props.requiresComputation && mission != null && mission.IsActive
            && mission.AllSlots.Any(s => s.Open && !s.solved);

        public float ComputeProgress
        {
            get
            {
                if (mission == null) { return 1f; }
                float total = mission.ComputeWorkTotal;
                return total <= 0f ? 1f : Mathf.Clamp01(mission.computeWorkDone / total);
            }
        }

        /// <summary>研究者每 tick 呼叫：累積工作量（研究速度 × tick），依波次順序逐枚解出。</summary>
        public void AddComputationWork(float amount)
        {
            if (!NeedsManualComputation || amount <= 0f) { return; }
            mission.computeWorkDone += amount;
            ApplyComputationProgress(amount);
        }

        /// <summary>投入工作量並依波次順序解出導彈；amount = 0 時只把已累積的工作量用在新加入的導彈上。</summary>
        private void ApplyComputationProgress(float amount)
        {
            List<StrikeSlot> unsolved = mission.UnsolvedOpen.ToList();
            if (unsolved.Count == 0) { return; }
            int solved = StrikeComputation.Advance(ref mission.computeBaseLeft, ref mission.computeCredit, amount,
                mission.computePerRound, unsolved.Count);
            if (solved == 0) { return; }
            HashSet<StrikeWave> pendingBefore = new HashSet<StrikeWave>(mission.waves.Where(w => w.slots.Any(s => s.Open && !s.solved)));
            for (int i = 0; i < solved; i++) { unsolved[i].solved = true; }
            bool allSolved = !mission.UnsolvedOpen.Any();
            for (int w = 0; w < mission.waves.Count; w++)
            {
                StrikeWave wave = mission.waves[w];
                if (!allSolved && pendingBefore.Contains(wave) && !wave.slots.Any(s => s.Open && !s.solved))
                {
                    Messages.Message("DMSE.Strike.Msg.WaveSolved".Translate(mission.Label, w + 1), parent, MessageTypeDefOf.PositiveEvent, false);
                }
            }
            if (allSolved)
            {
                if (mission.state == StrikeMissionState.AwaitingComputation) { mission.state = StrikeMissionState.Ready; }
                Messages.Message("DMSE.Strike.Msg.Ready".Translate(mission.Label), parent, MessageTypeDefOf.PositiveEvent, false);
            }
        }

        private string ComputingReason()
        {
            return Props.requiresComputation
                ? "DMSE.Strike.ComputingManual".Translate(ComputeProgress.ToStringPercent())
                : "DMSE.Strike.Computing".Translate(ComputeTicksLeft.ToStringTicksToPeriod());
        }

        /// <summary>每 60 ticks 由地圖管理器呼叫：計算進度、目標丟失。</summary>
        public void MissionTick(int now)
        {
            if (mission == null) { return; }
            if (mission.TargetLost)
            {
                CancelMission("DMSE.Strike.Msg.TargetLost");
                return;
            }
            CheckTargetMoved(now);
            // 自動版：確認或調整波次後有未解的導彈就倒數（斷電時暫停）
            if (!Props.requiresComputation && mission.IsActive && mission.UnsolvedOpen.Any())
            {
                if (!Active) { mission.computeDoneTick += 60; }
                else if (now >= mission.computeDoneTick) { FinishComputation(); }
            }
            TryAutoFire();
        }

        public int ComputeTicksLeft => mission == null ? 0 : Mathf.Max(0, mission.computeDoneTick - Find.TickManager.TicksGame);

        /// <summary>
        /// 目標移動超過 <see cref="BVRWorldTuningDef.strikeRecomputeMoveTiles"/> 格：尚未發射的導彈彈道解作廢，
        /// 自動版重新倒數、人工版需研究者再算（P4）。
        /// </summary>
        private void CheckTargetMoved(int now)
        {
            if (!mission.IsActive) { return; }
            PlanetTile current = mission.CurrentTargetTile;
            if (!mission.solutionTile.Valid) { mission.solutionTile = current; return; }
            float threshold = BVRWorldTuningDef.Current.strikeRecomputeMoveTiles;
            if (threshold <= 0f || !current.Valid || current == mission.solutionTile) { return; }
            float moved = MissileWorldUtility.RangeDistance(mission.solutionTile, current);
            if (moved < threshold) { return; }
            List<StrikeSlot> stale = mission.AllSlots.Where(s => s.state == StrikeSlotState.Pending && s.solved).ToList();
            mission.solutionTile = current;
            if (stale.Count == 0) { return; }
            foreach (StrikeSlot slot in stale) { slot.solved = false; }
            if (!Props.requiresComputation) { mission.computeDoneTick = now + Mathf.Max(0, Props.autoComputeTicks); }
            if (mission.state == StrikeMissionState.Ready) { mission.state = StrikeMissionState.AwaitingComputation; }
            Messages.Message("DMSE.Strike.Msg.TargetMoved".Translate(mission.Label, Mathf.RoundToInt(moved), stale.Count), parent,
                MessageTypeDefOf.CautionInput, false);
        }

        /// <summary>
        /// 定時發射（P4，§14-12）：下一波勾選了定時發射、現在就能整波發射，且依時間軸它會在前面各波最晚抵達之後
        /// （再加 <see cref="BVRWorldTuningDef.strikeArrivalSpacingTicks"/>）抵達時，自動發射。
        /// </summary>
        private void TryAutoFire()
        {
            if (mission == null || !mission.IsActive) { return; }
            int next = mission.NextWaveIndex;
            if (next < 0 || !mission.waves[next].autoFire || CannotFireReason(next, partial: false) != null) { return; }
            List<StrikePlanWave> plan = PlanTimeline(mission, mission.waves);
            int latest = -1;
            for (int i = 0; i < next; i++) { latest = Mathf.Max(latest, plan[i].arriveMax); }
            StrikePlanWave p = plan[next];
            if (latest >= 0 && p.arriveMin >= 0 && p.arriveMin < latest + BVRWorldTuningDef.Current.strikeArrivalSpacingTicks) { return; }
            Messages.Message("DMSE.Strike.Msg.AutoFire".Translate(mission.Label, next + 1), parent, MessageTypeDefOf.NeutralEvent, false);
            FireWave(next, partial: false);
        }

        // ==================================================================
        //  時間軸（§2-3、§10-2、§10-3）
        // ==================================================================

        /// <summary>
        /// 估算各波最快的發射時刻與抵達區間：待發射的依通道與平台冷卻排程，已發射且仍在飛的取其實際 ETA。
        /// 用於規劃視窗的抵達預覽與執行視窗的「現在發射」提示。
        /// </summary>
        public List<StrikePlanWave> PlanTimeline(StrikeMission m, List<StrikeWave> waves)
        {
            int now = Find.TickManager.TicksGame;
            PlanetTile from = parent.Map.Tile;
            PlanetTile to = m.CurrentTargetTile;
            var input = new List<List<StrikePlanSlot>>();
            foreach (StrikeWave wave in waves)
            {
                var list = new List<StrikePlanSlot>();
                foreach (StrikeSlot slot in wave.slots)
                {
                    if (slot.state == StrikeSlotState.Skipped) { continue; }
                    CompMissileLauncher_WorldTargeting launcher = slot.LauncherComp;
                    if (slot.state == StrikeSlotState.Launched)
                    {
                        // 離場 skyfaller 還在地圖上時航跡尚未開始移動：抵達要從它離開地圖的時刻起算
                        int departs = slot.track != null && !slot.track.Spawned
                            ? Mathf.Max(now, Mathf.Max(slot.launchTick, slot.track.launchSpawnTick) + (launcher?.DepartureTicksFor(slot.track.config, slot.track.destinationTile) ?? 0)) : now;
                        list.Add(new StrikePlanSlot { launched = true, arrivalTick = slot.InFlight ? departs + slot.track.TicksToArrival : -1 });
                        continue;
                    }
                    // 升空後離場 skyfaller 飛出地圖才出現世界航跡，所以飛行時間要加上離場時間
                    MissileConfig slotConfig = launcher?.BuildFiringConfig(slot.round);
                    int flight = launcher != null
                        ? launcher.DepartureTicksFor(slotConfig, to) + StrikeRoundUtility.FlightTicks(slotConfig, from, to) : 0;
                    if (slot.state == StrikeSlotState.Queued)
                    {
                        list.Add(new StrikePlanSlot { launched = true, arrivalTick = now + 1 + flight });
                        continue;
                    }
                    list.Add(new StrikePlanSlot
                    {
                        launcher = slot.launcher,
                        flightTicks = flight,
                        channelCost = slotConfig?.StrikeChannelCost ?? 1,
                        salvoInterval = launcher?.SalvoIntervalTicks ?? 0,
                        launchCooldown = launcher?.CooldownTicksAfterLaunch ?? 0,
                        readyTick = now + (launcher?.CooldownTicksRemaining(now) ?? 0),
                    });
                }
                input.Add(list);
            }
            BVRWorldTuningDef tuning = BVRWorldTuningDef.Current;
            IEnumerable<int> holds = Planner?.GuidanceReleaseTicks(Side) ?? Enumerable.Empty<int>();
            return StrikeTimeline.Simulate(input, Mathf.Max(1, StrikeCapacity), tuning.strikeGuidanceTicks,
                tuning.strikeArrivalSpacingTicks, now, holds);
        }

        // ==================================================================
        //  執行中調整波次（§10-3）
        // ==================================================================

        /// <summary>能否開始調整波次；發射佇列有東西時不行（佇列以波次／槽位索引指向導彈）。</summary>
        public string CannotAdjustReason()
        {
            if (mission == null || !mission.IsActive) { return "DMSE.Strike.NotReady".Translate(); }
            if ((Planner?.QueuedCount(mission.id) ?? 0) > 0) { return "DMSE.Strike.Adjust.Launching".Translate(); }
            return null;
        }

        /// <summary>調整用的波次副本：槽位物件沿用（已發射、略過的固定不動），波次清單是新的。</summary>
        public List<StrikeWave> AdjustmentCopy()
        {
            return mission.waves.Select(w => new StrikeWave { launchedTick = w.launchedTick, autoFire = w.autoFire, slots = new List<StrikeSlot>(w.slots) }).ToList();
        }

        public string ValidateAdjustment(List<StrikeWave> waves)
        {
            if (mission == null) { return "DMSE.Strike.NotReady".Translate(); }
            int cap = Mathf.Max(1, StrikeCapacity);
            List<StrikeSlot> all = waves.SelectMany(w => w.slots).ToList();
            if (mission.AllSlots.Any(s => !s.Open && !all.Contains(s))) { return "DMSE.Strike.Adjust.FixedRemoved".Translate(); }
            if (all.Count > Props.maxRoundsPerMission) { return "DMSE.Strike.TooManyRounds".Translate(Props.maxRoundsPerMission); }
            if (waves.Count(w => w.slots.Count > 0) > Props.maxWavesPerMission) { return "DMSE.Strike.TooManyWaves".Translate(Props.maxWavesPerMission); }
            if (all.Where(s => s.round != null).GroupBy(s => s.round).Any(g => g.Count() > 1)) { return "DMSE.Strike.RoundTaken".Translate(); }
            for (int i = 0; i < waves.Count; i++)
            {
                int pending = StrikeRoundUtility.PendingChannels(waves[i]);
                if (pending > cap) { return "DMSE.Strike.WaveOverCap".Translate(i + 1, pending, cap); }
            }
            return null;
        }

        /// <summary>
        /// 套用調整：保留新加入的導彈、釋放被移除的；在波次間移動的導彈沿用原本的彈道解，新加入的需要計算
        /// （人工版只增加剩餘工作量，自動版重新倒數）。
        /// </summary>
        public bool ApplyAdjustment(List<StrikeWave> waves, out string reason)
        {
            reason = CannotAdjustReason() ?? ValidateAdjustment(waves);
            MapComponent_StrikePlanner planner = Planner;
            if (reason == null && planner == null) { reason = "DMSE.BVR.DeviceInactive".Translate(); }
            if (reason != null) { return false; }

            int now = Find.TickManager.TicksGame;
            PlanetTile from = parent.Map.Tile;
            var previous = new Dictionary<Thing, StrikeSlot>();
            foreach (StrikeSlot slot in mission.AllSlots.Where(s => s.state == StrikeSlotState.Pending && s.round != null)) { previous[slot.round] = slot; }
            var kept = new HashSet<Thing>();
            var added = new List<Thing>();
            foreach (StrikeSlot slot in waves.SelectMany(w => w.slots))
            {
                if (slot.state != StrikeSlotState.Pending) { continue; }
                if (slot.round != null && previous.TryGetValue(slot.round, out StrikeSlot old))
                {
                    if (!ReferenceEquals(old, slot))
                    {
                        slot.solved = old.solved;
                        slot.configHash = old.configHash;
                    }
                    kept.Add(slot.round);
                    continue;
                }
                CompMissileLauncher_WorldTargeting launcher = slot.LauncherComp;
                MissileConfig config = launcher?.BuildFiringConfig(slot.round);
                if (launcher == null || !launcher.OwnsRound(slot.round)
                    || !MissileWorldUtility.CanEngage(config, from, mission.CurrentTargetTile, 0f, out _))
                {
                    reason = "DMSE.Strike.RoundUnavailable".Translate();
                }
                else if (!planner.TryReserve(slot.round, mission.id))
                {
                    reason = "DMSE.Strike.RoundTaken".Translate();
                }
                if (reason != null)
                {
                    foreach (Thing round in added) { planner.Release(round); }
                    return false;
                }
                slot.configHash = StrikeRoundUtility.ConfigHash(config);
                slot.solved = false;
                added.Add(slot.round);
            }
            foreach (KeyValuePair<Thing, StrikeSlot> kv in previous)
            {
                if (!kept.Contains(kv.Key)) { planner.Release(kv.Key); }
            }

            mission.waves = waves.Where(w => w.slots.Count > 0).ToList();
            bool unsolved = mission.UnsolvedOpen.Any();
            if (Props.requiresComputation) { ApplyComputationProgress(0f); }
            else if (unsolved) { mission.computeDoneTick = now + Mathf.Max(0, Props.autoComputeTicks); }
            if (!mission.AllSlots.Any(s => !s.Open))
            {
                mission.state = mission.UnsolvedOpen.Any() ? StrikeMissionState.AwaitingComputation : StrikeMissionState.Ready;
            }
            Messages.Message("DMSE.Strike.Msg.Adjusted".Translate(mission.Label, mission.RoundCount, mission.waves.Count, added.Count),
                parent, MessageTypeDefOf.TaskCompletion, false);
            TryFinish();
            return true;
        }

        // ==================================================================
        //  發射
        // ==================================================================

        /// <summary>發射前的永久性問題（彈藥不在、改裝過、超出射程）：這枚會被略過。沒有時回傳 null。</summary>
        private string PermanentProblem(StrikeSlot slot, CompMissileLauncher_WorldTargeting launcher)
        {
            if (launcher == null || slot.round == null || !launcher.OwnsRound(slot.round))
            {
                return "DMSE.Strike.Skip.RoundGone".Translate();
            }
            MissileConfig config = launcher.BuildFiringConfig(slot.round);
            if (StrikeRoundUtility.ConfigHash(config) != slot.configHash) { return "DMSE.Strike.Skip.Refitted".Translate(); }
            return MissileWorldUtility.CanEngage(config, parent.Map.Tile, mission.CurrentTargetTile, 0f, out string reason)
                ? null : reason;
        }

        private struct WaveCheck
        {
            public int pending;      // 待發射且沒有永久性問題
            public int skip;         // 發射時會被略過
            public int ready;        // 平台現在就能發射（含同平台漣波續射）
            public int channels;
            public int minReadyCost;
            public int blockedPlatforms;
        }

        private WaveCheck CheckWave(StrikeWave wave)
        {
            var check = new WaveCheck();
            int now = Find.TickManager.TicksGame;
            foreach (IGrouping<ThingWithComps, StrikeSlot> group in wave.slots.Where(s => s.state == StrikeSlotState.Pending)
                .GroupBy(s => s.launcher))
            {
                CompMissileLauncher_WorldTargeting launcher = group.Key?.GetComp<CompMissileLauncher_WorldTargeting>();
                bool platformReady = launcher != null && launcher.Active && !launcher.OnCooldown(now);
                bool counted = false;
                foreach (StrikeSlot slot in group)
                {
                    if (PermanentProblem(slot, launcher) != null) { check.skip++; continue; }
                    check.pending++;
                    int cost = StrikeRoundUtility.ChannelCost(slot);
                    check.channels += cost;
                    if (platformReady)
                    {
                        check.ready++;
                        check.minReadyCost = check.minReadyCost == 0 ? cost : Mathf.Min(check.minReadyCost, cost);
                    }
                    else if (!counted) { check.blockedPlatforms++; counted = true; }
                }
            }
            return check;
        }

        /// <summary>第 <paramref name="index"/> 波能否發射；可以時回傳 null。<paramref name="partial"/> = 只發射現在辦得到的部分。</summary>
        public string CannotFireReason(int index, bool partial)
        {
            if (mission == null || index < 0 || index >= mission.waves.Count) { return "DMSE.Strike.NotReady".Translate(); }
            if (!mission.IsActive) { return "DMSE.Strike.NotReady".Translate(); }
            if (!Active) { return "DMSE.BVR.DeviceInactive".Translate(); }
            StrikeWave wave = mission.waves[index];
            if (!wave.HasPending) { return "DMSE.Strike.WaveDone".Translate(); }
            // 人工版逐枚解出：本波的待發射導彈都解好了就能發射，不必等後面的波次
            if (wave.slots.Any(s => s.state == StrikeSlotState.Pending && !s.solved)) { return ComputingReason(); }
            WaveCheck check = CheckWave(wave);
            if (check.pending == 0) { return null; }   // 全部會被略過：發射即結算
            int free = StrikeFree;
            if (partial)
            {
                if (check.ready > 0 && free >= check.minReadyCost) { return null; }
            }
            else if (check.ready == check.pending && free >= check.channels)
            {
                return null;
            }
            if (check.ready < check.pending && (partial ? check.ready == 0 : true))
            {
                return "DMSE.Strike.PlatformsNotReady".Translate(check.blockedPlatforms);
            }
            int release = Planner?.TicksUntilNextRelease(Side) ?? -1;
            return release > 0
                ? "DMSE.Strike.NotEnoughChannelsRelease".Translate(free, partial && check.ready > 0 ? check.minReadyCost : check.channels, release.ToStringTicksToPeriod())
                : "DMSE.Strike.NotEnoughChannels".Translate(free, partial && check.ready > 0 ? check.minReadyCost : check.channels);
        }

        /// <summary>
        /// 發射第 <paramref name="index"/> 波（§7）：永久性問題的導彈略過；其餘依平台分組排入漣波佇列，
        /// 同平台各枚相隔 <see cref="CompMissileLauncher_WorldTargeting.SalvoIntervalTicks"/>，每枚依發射配置占用導引通道。
        /// 部分發射時，平台未就緒或通道不足的導彈留在本波等待補射。回傳排入佇列的枚數。
        /// </summary>
        public int FireWave(int index, bool partial)
        {
            if (CannotFireReason(index, partial) != null) { return 0; }
            MapComponent_StrikePlanner planner = Planner;
            StrikeWave wave = mission.waves[index];
            int now = Find.TickManager.TicksGame;
            int start = now + 1;   // 第一枚在下一個 tick 發射，之後每枚間隔固定
            int free = StrikeFree;
            int queued = 0, skipped = 0;
            foreach (IGrouping<ThingWithComps, StrikeSlot> group in wave.slots.Where(s => s.state == StrikeSlotState.Pending)
                .GroupBy(s => s.launcher).ToList())
            {
                CompMissileLauncher_WorldTargeting launcher = group.Key?.GetComp<CompMissileLauncher_WorldTargeting>();
                int salvo = 0;
                foreach (StrikeSlot slot in group)
                {
                    string problem = PermanentProblem(slot, launcher);
                    if (problem != null)
                    {
                        Skip(slot, problem, planner);
                        skipped++;
                        continue;
                    }
                    int cost = StrikeRoundUtility.ChannelCost(slot);
                    if (!launcher.Active || (salvo == 0 && launcher.OnCooldown(now)) || free < cost) { continue; }
                    planner.Enqueue(new StrikeQueuedLaunch
                    {
                        console = parent,
                        missionId = mission.id,
                        wave = index,
                        slot = wave.slots.IndexOf(slot),
                        fireTick = start + salvo * launcher.SalvoIntervalTicks,
                        channelCost = cost,
                        salvoContinuation = salvo > 0,
                    });
                    slot.state = StrikeSlotState.Queued;
                    salvo++;
                    free -= cost;
                    queued++;
                }
            }
            if (queued > 0)
            {
                if (wave.launchedTick < 0) { wave.launchedTick = now; }
                mission.state = StrikeMissionState.Executing;
            }
            Messages.Message("DMSE.Strike.Msg.WaveFired".Translate(mission.Label, index + 1, queued, skipped), parent,
                MessageTypeDefOf.NeutralEvent, false);
            TryFinish();
            return queued;
        }

        /// <summary>Gizmo／視窗的發射按鈕：不能發射時以拒絕訊息說明原因。</summary>
        public void TryFireFromUI(int index, bool partial)
        {
            string reason = CannotFireReason(index, partial);
            if (reason != null)
            {
                Messages.Message(reason, parent, MessageTypeDefOf.RejectInput, false);
                return;
            }
            FireWave(index, partial);
        }

        private void Skip(StrikeSlot slot, string reason, MapComponent_StrikePlanner planner)
        {
            slot.state = StrikeSlotState.Skipped;
            slot.skipReason = reason;
            planner?.Release(slot.round);
        }

        /// <summary>佇列到時由地圖管理器呼叫：發射這一枚，成功則掛上任務標記並開始導引期。</summary>
        public void ExecuteQueued(StrikeQueuedLaunch launch, MapComponent_StrikePlanner planner)
        {
            if (mission == null || mission.id != launch.missionId || launch.wave < 0 || launch.wave >= mission.waves.Count) { return; }
            List<StrikeSlot> slots = mission.waves[launch.wave].slots;
            if (launch.slot < 0 || launch.slot >= slots.Count) { return; }
            StrikeSlot slot = slots[launch.slot];
            if (slot.state != StrikeSlotState.Queued) { return; }

            int now = Find.TickManager.TicksGame;
            CompMissileLauncher_WorldTargeting launcher = slot.LauncherComp;
            if (launcher != null && launcher.OwnsRound(slot.round)
                && (!launcher.Active || (!launch.salvoContinuation && launcher.OnCooldown(now))))
            {
                // 暫時性問題（排隊期間斷電、被單發搶先進入冷卻）：留在本波等待補射
                slot.state = StrikeSlotState.Pending;
                Messages.Message("DMSE.Strike.Msg.Deferred".Translate(mission.Label, slot.round.LabelShort, launch.wave + 1),
                    parent, MessageTypeDefOf.RejectInput, false);
                return;
            }
            string reason = null;
            ScorerProjectile_WorldObject track = launcher != null
                ? launcher.TryLaunchQueuedRound(mission.Target, slot.round, launch.salvoContinuation, out reason)
                : null;
            if (track != null)
            {
                planner.Release(slot.round);
                slot.state = StrikeSlotState.Launched;
                slot.track = track;
                slot.launchTick = now;
                track.strikeMissionId = mission.id;
                track.strikeWave = launch.wave;
                planner.AddGuidanceHold(Side, now + BVRWorldTuningDef.Current.strikeGuidanceTicks, launch.channelCost);
                WorldComponent_StrikeReports.Current?.RecordLaunch(mission.id, launch.wave, mission.TargetLabel,
                    parent.Map?.Parent?.LabelCap ?? parent.LabelShort, mission.Label);
            }
            else
            {
                Skip(slot, reason ?? "DMSE.Strike.Skip.RoundGone".Translate(), planner);
                Messages.Message("DMSE.Strike.Msg.Skipped".Translate(mission.Label, slot.round?.LabelShort ?? "?", slot.skipReason),
                    parent, MessageTypeDefOf.RejectInput, false);
            }
            TryFinish();
        }

        /// <summary>沒有任何待發射或排隊中的導彈：任務結束，釋放保留，指揮台可規劃下一個任務。</summary>
        private void TryFinish()
        {
            if (mission == null || mission.AllSlots.Any(s => s.Open)) { return; }
            int launched = mission.AllSlots.Count(s => s.state == StrikeSlotState.Launched);
            int skipped = mission.AllSlots.Count(s => s.state == StrikeSlotState.Skipped);
            mission.state = StrikeMissionState.Completed;
            Planner?.ReleaseAll(mission.id);
            WorldComponent_StrikeReports.Current?.Close(mission.id);
            Messages.Message("DMSE.Strike.Msg.Completed".Translate(mission.Label, launched, skipped), parent,
                MessageTypeDefOf.TaskCompletion, false);
            mission = null;
        }

        /// <summary>取消任務：移除佇列（釋放已訂通道）、釋放保留。已升空的導彈照常飛行。</summary>
        public void CancelMission(string messageKey = "DMSE.Strike.Msg.Cancelled")
        {
            if (mission == null) { return; }
            MapComponent_StrikePlanner planner = Planner;
            planner?.DropQueued(mission.id);
            planner?.ReleaseAll(mission.id);
            WorldComponent_StrikeReports.Current?.Close(mission.id);
            mission.state = StrikeMissionState.Cancelled;
            if (!messageKey.NullOrEmpty())
            {
                Messages.Message(messageKey.Translate(mission.Label), parent, MessageTypeDefOf.NeutralEvent, false);
            }
            mission = null;
        }

        // ==================================================================
        //  生命週期與顯示
        // ==================================================================

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_StrikePlanner planner = Planner;
            if (planner == null) { return; }
            planner.consoles.Add(this);
            // 保留索引不存檔：讀檔後依任務重建
            if (mission != null)
            {
                foreach (StrikeSlot slot in mission.AllSlots.Where(s => s.Open && s.round != null))
                {
                    planner.TryReserve(slot.round, mission.id);
                }
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_StrikePlanner planner = map?.GetComponent<MapComponent_StrikePlanner>();
            planner?.consoles.Remove(this);
            if (mission != null && mode != DestroyMode.WillReplace && !parent.BeingTransportedOnGravship)
            {
                planner?.DropQueued(mission.id);
                planner?.ReleaseAll(mission.id);
                WorldComponent_StrikeReports.Current?.Close(mission.id);
                Messages.Message("DMSE.Strike.Msg.ConsoleLost".Translate(mission.Label), MessageTypeDefOf.NegativeEvent, false);
                mission = null;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref mission, "strikeMission");
        }

        public string StateLabel
        {
            get
            {
                if (mission == null) { return string.Empty; }
                switch (mission.state)
                {
                    case StrikeMissionState.AwaitingComputation:
                        return Props.requiresComputation
                            ? "DMSE.Strike.State.ComputingManual".Translate(ComputeProgress.ToStringPercent())
                            : "DMSE.Strike.State.Computing".Translate(ComputeTicksLeft.ToStringTicksToPeriod());
                    case StrikeMissionState.Ready: return "DMSE.Strike.State.Ready".Translate();
                    case StrikeMissionState.Executing:
                        return NeedsManualComputation
                            ? "DMSE.Strike.State.ExecutingComputing".Translate(ComputeProgress.ToStringPercent())
                            : "DMSE.Strike.State.Executing".Translate();
                    default: return mission.state.ToString();
                }
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned) { return null; }
            var sb = new StringBuilder();
            sb.Append("DMSE.Strike.Inspect.Channels".Translate(StrikeFree, StrikeCapacity));
            if (mission != null)
            {
                sb.AppendLine();
                sb.Append("DMSE.Strike.Inspect.Mission".Translate(mission.Label, mission.TargetLabel, StateLabel,
                    mission.AllSlots.Count(s => s.state == StrikeSlotState.Launched), mission.RoundCount));
            }
            return sb.ToString();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) { yield return g; }
            if (parent.Faction != Faction.OfPlayer) { yield break; }

            if (mission == null)
            {
                var plan = new Command_Action
                {
                    defaultLabel = "DMSE.Strike.Console.Plan".Translate(),
                    defaultDesc = "DMSE.Strike.Console.PlanDesc".Translate(),
                    icon = CompLaunchable.LaunchCommandTex,
                    action = BeginTargeting,
                };
                string reason = CannotPlanReason();
                if (reason != null) { plan.Disable(reason); }
                yield return plan;
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "DMSE.Strike.Console.Open".Translate(),
                defaultDesc = "DMSE.Strike.Console.OpenDesc".Translate(),
                icon = CompLaunchable.LaunchCommandTex,
                action = () => Find.WindowStack.Add(new Dialog_StrikePlan(this, mission)),
            };

            int next = mission.NextWaveIndex;
            if (next >= 0)
            {
                var fire = new Command_Action
                {
                    defaultLabel = "DMSE.Strike.Console.FireWave".Translate(next + 1),
                    defaultDesc = "DMSE.Strike.Console.FireWaveDesc".Translate(),
                    icon = TexCommand.Attack,
                    action = () => TryFireFromUI(next, Event.current != null && Event.current.shift),
                };
                string blocked = CannotFireReason(next, partial: false);
                if (blocked != null && CannotFireReason(next, partial: true) != null) { fire.Disable(blocked); }
                yield return fire;
            }

            yield return new Command_Action
            {
                defaultLabel = "DMSE.Strike.Console.Cancel".Translate(),
                defaultDesc = "DMSE.Strike.Console.CancelDesc".Translate(),
                icon = StrikeTextures.Cancel,
                action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "DMSE.Strike.Console.CancelConfirm".Translate(mission.Label), () => CancelMission(), destructive: true)),
            };

            if (DebugSettings.ShowDevGizmos && (NeedsManualComputation || mission.state == StrikeMissionState.AwaitingComputation))
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Finish firing solutions",
                    action = FinishComputation,
                };
            }
        }
    }
}
