using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.Sound;

namespace DMSE
{
    public class CompProperties_TransferConsole : CompProperties
    {
        /// <summary>點火程序作業時間（殖民者操作）。</summary>
        public int operateTicks = 1250;

        /// <summary>點火程序完成後到分離的倒數（引擎暖機，效果見 <see cref="MapComponent_TransferBurn"/>）。</summary>
        public int countdownTicks = 600;

        public CompProperties_TransferConsole()
        {
            compClass = typeof(CompTransferConsole);
        }
    }

    public enum TransferConsoleState
    {
        Idle,
        Planned,
        AwaitingOperator,
        Countdown
    }

    /// <summary>
    /// 轉移飛行控制台（設計稿 §4）。必須位於某結構網路的船體範圍內；同一艘船有多台時，
    /// 第一台可用者為主控台，其餘為備援。完全不依賴 Building_GravEngine / CompPilotConsole。
    ///
    /// 狀態：Idle →（確認計畫）Planned →（啟動點火程序）AwaitingOperator →（殖民者完成作業）Countdown →（分離）。
    /// 起飛程序中每 250 tick 重做阻擋性整備檢查，失敗或結構網路在倒數中改變就自動中止。
    /// </summary>
    public class CompTransferConsole : ThingComp, IRenameable
    {
        private const int LaunchCheckInterval = 250;

        public TransferPlan plan;

        /// <summary>玩家為這艘船取的名稱；存在控制台上，隨船搬移，每次起飛都沿用。null = 尚未命名。</summary>
        private string shipName;

        private TransferConsoleState state = TransferConsoleState.Idle;

        private int countdownEndTick = -1;

        private bool showFootprint;

        /// <summary>倒數開始時本艘船的結構樁；倒數中鎖定其鋼樑操作，並用來偵測網路改變。</summary>
        private List<Thing> lockedPiles = new List<Thing>();

        [Unsaved(false)]
        private bool separationQueued;

        // 整備檢查快取（ITab 每幀繪製；完整檢查需掃描整個船體範圍）
        private const int IssuesCacheTicks = 120;

        [Unsaved(false)]
        private List<TransferIssue> cachedIssues;

        [Unsaved(false)]
        private int cachedIssuesTick = -1;

        public CompProperties_TransferConsole Props => (CompProperties_TransferConsole)props;

        public TransferConsoleState State => state;

        public bool ShowFootprint => showFootprint;

        public bool InLaunchSequence => state == TransferConsoleState.AwaitingOperator || state == TransferConsoleState.Countdown;

        public int CountdownTicksLeft => state == TransferConsoleState.Countdown ? System.Math.Max(0, countdownEndTick - Find.TickManager.TicksGame) : 0;

        /// <summary>倒數（引擎暖機）進度 0 → 1；不在倒數中為 0。</summary>
        public float CountdownProgress => state == TransferConsoleState.Countdown ? 1f - (float)CountdownTicksLeft / System.Math.Max(1, Props.countdownTicks) : 0f;

        /// <summary>目前計畫（或無計畫時）的完整整備檢查，快取 <see cref="IssuesCacheTicks"/> tick。</summary>
        public List<TransferIssue> Issues
        {
            get
            {
                int now = Find.TickManager.TicksGame;
                if (cachedIssues == null || now - cachedIssuesTick > IssuesCacheTicks || now < cachedIssuesTick)
                {
                    cachedIssues = TransferReadiness.Check(this, state == TransferConsoleState.Idle ? null : plan);
                    cachedIssuesTick = now;
                }
                return cachedIssues;
            }
        }

        /// <summary>已生成、未損壞、開啟且通電（與 CompBVRDevice.Active 同樣的判定組合）。</summary>
        public bool IsAvailable
        {
            get
            {
                if (!parent.Spawned)
                {
                    return false;
                }
                CompBreakdownable breakdownable = parent.GetComp<CompBreakdownable>();
                if (breakdownable != null && breakdownable.BrokenDown)
                {
                    return false;
                }
                CompFlickable flickable = parent.GetComp<CompFlickable>();
                if (flickable != null && !flickable.SwitchIsOn)
                {
                    return false;
                }
                CompPowerTrader power = parent.GetComp<CompPowerTrader>();
                return power == null || power.PowerOn;
            }
        }

        public PileNetwork Network => parent.Spawned ? MapComponent_PileNetwork.For(parent.Map)?.NetworkContaining(parent) : null;

        public TransferShipSurvey Survey => Network?.Survey;

        public bool IsPrimary
        {
            get
            {
                TransferShipSurvey survey = Survey;
                return survey != null && survey.primaryConsole == this;
            }
        }

        // ── 船名 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 船名：控制台保存的名稱；尚未命名但已在船體地圖上（例如舊存檔）時沿用該船體的名稱。
        /// 兩者皆無時為 null，起飛時自動命名（<see cref="TakeShipNameForLaunch"/>）。
        /// </summary>
        public string ShipName
        {
            get
            {
                if (!shipName.NullOrEmpty())
                {
                    return shipName;
                }
                return CurrentShipObject?.Name;
            }
        }

        /// <summary>本控制台所在的船體世界物件；只有本控制台屬於該船的起飛網路時才算。</summary>
        private WorldObject_TransferShip CurrentShipObject
        {
            get
            {
                if (!parent.Spawned)
                {
                    return null;
                }
                WorldObject_TransferShip ship = parent.Map.Parent as WorldObject_TransferShip;
                if (ship == null)
                {
                    return null;
                }
                TransferShipSurvey survey = ship.FlightSurvey();
                return survey != null && survey.consoles.Contains(this) ? ship : null;
            }
        }

        /// <summary>為這艘船命名：同船所有控制台一起改名；已在船體地圖上時，世界地圖上的船名同步更新。</summary>
        public void SetShipName(string name)
        {
            name = name?.Trim();
            if (name.NullOrEmpty())
            {
                return;
            }
            WorldObject_TransferShip ship = CurrentShipObject;
            ApplyShipName(name);
            if (ship != null)
            {
                ship.Name = name;
            }
        }

        /// <summary>
        /// 起飛時決定船名並存回同船所有控制台：本控制台的名稱優先，其次同船其他控制台，最後自動命名。
        /// 必須在分離前（控制台仍在原地圖上）呼叫。
        /// </summary>
        public string TakeShipNameForLaunch()
        {
            string name = ShipName;
            List<CompTransferConsole> consoles = Survey?.consoles;
            if (name.NullOrEmpty() && consoles != null)
            {
                name = consoles.Select(c => c.ShipName).FirstOrDefault(n => !n.NullOrEmpty());
            }
            if (name.NullOrEmpty())
            {
                name = WorldObject_TransferShip.NextDefaultName();
            }
            ApplyShipName(name);
            return name;
        }

        private void ApplyShipName(string name)
        {
            shipName = name;
            List<CompTransferConsole> consoles = Survey?.consoles;
            if (consoles == null)
            {
                return;
            }
            for (int i = 0; i < consoles.Count; i++)
            {
                consoles[i].shipName = name;
            }
        }

        // ── IRenameable（原版 Dialog_Rename 與選取面板的筆形按鈕） ─────────────

        public string RenamableLabel
        {
            get => ShipName ?? WorldObject_TransferShip.NextDefaultName();
            set => SetShipName(value);
        }

        public string BaseLabel => parent.def.label;

        public string InspectLabel => parent.LabelCap;

        // ── 計畫與起飛程序 ───────────────────────────────────────────────────

        public void SetPlan(TransferPlan newPlan)
        {
            plan = newPlan;
            state = TransferConsoleState.Planned;
            countdownEndTick = -1;
            lockedPiles.Clear();
            cachedIssues = null;
        }

        public void AbortPlan()
        {
            plan = null;
            state = TransferConsoleState.Idle;
            countdownEndTick = -1;
            lockedPiles.Clear();
            separationQueued = false;
            cachedIssues = null;
        }

        /// <summary>中止起飛程序並發信件說明原因；計畫保留為 Planned，排除問題後可重新啟動。</summary>
        public void AbortLaunch(string reason)
        {
            if (!InLaunchSequence)
            {
                return;
            }
            state = plan != null ? TransferConsoleState.Planned : TransferConsoleState.Idle;
            countdownEndTick = -1;
            lockedPiles.Clear();
            separationQueued = false;
            cachedIssues = null;
            Find.LetterStack.ReceiveLetter("DMSE.Transfer.Letter.AbortedLabel".Translate(), "DMSE.Transfer.Letter.AbortedText".Translate(reason), LetterDefOf.NegativeEvent, parent);
        }

        /// <summary>Planned → AwaitingOperator：等殖民者到控制台執行點火程序（設計稿 §4-2 步驟 3）。</summary>
        public void BeginLaunchSequence()
        {
            string reason = LaunchBlockedReason();
            if (reason != null)
            {
                Messages.Message(reason, parent, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            state = TransferConsoleState.AwaitingOperator;
            cachedIssues = null;
            Messages.Message("DMSE.Transfer.Console.AwaitingOperatorMsg".Translate(), parent, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>點火程序作業完成：AwaitingOperator → Countdown，鎖定本船結構樁。</summary>
        public void BeginCountdown()
        {
            if (state != TransferConsoleState.AwaitingOperator)
            {
                return;
            }
            PileNetwork net = Network;
            if (net == null)
            {
                AbortLaunch("DMSE.Transfer.Issue.ConsoleNotOnShip".Translate());
                return;
            }
            state = TransferConsoleState.Countdown;
            countdownEndTick = Find.TickManager.TicksGame + Props.countdownTicks;
            lockedPiles.Clear();
            for (int i = 0; i < net.piles.Count; i++)
            {
                lockedPiles.Add(net.piles[i].parent);
            }
            cachedIssues = null;

            // 引擎暖機開始：核融合爐（沒有則控制台）的啟動音，同舊版暖機；之後的暖機效果由 MapComponent_TransferBurn 依倒數進度繪製
            Thing soundSource = parent;
            List<CompTransferReactor> reactors = net.Survey.reactors;
            for (int i = 0; i < reactors.Count; i++)
            {
                if (reactors[i].IsAvailable)
                {
                    soundSource = reactors[i].parent;
                    break;
                }
            }
            SoundDefOf.ShipReactor_Startup.PlayOneShot(SoundInfo.InMap(new TargetInfo(soundSource.Position, soundSource.Map)));
        }

        /// <summary>啟動起飛程序前的阻擋性檢查；可以啟動時回傳 null。</summary>
        public string LaunchBlockedReason()
        {
            if (plan == null || state != TransferConsoleState.Planned)
            {
                return "DMSE.Transfer.Abort.NoPlan".Translate();
            }
            List<TransferIssue> issues = TransferReadiness.Check(this, plan, includeWarnings: false);
            TransferIssue error = issues.FirstOrDefault(i => i.IsError);
            return error?.text;
        }

        public bool IsLockingPile(Thing pile)
        {
            return state == TransferConsoleState.Countdown && lockedPiles.Contains(pile);
        }

        /// <summary>由 <see cref="MapComponent_PileNetwork"/> 每 tick 呼叫（只在起飛程序中）。</summary>
        public void LaunchTick()
        {
            if (!InLaunchSequence || separationQueued)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now % LaunchCheckInterval == 0)
            {
                string reason = LaunchSequenceProblem();
                if (reason != null)
                {
                    AbortLaunch(reason);
                    return;
                }
            }
            if (state == TransferConsoleState.Countdown && now >= countdownEndTick)
            {
                // 分離（扣燃料）前最後一次檢查，例如倒數最後幾 tick 才生效的防空封鎖。
                string finalReason = LaunchSequenceProblem();
                if (finalReason != null)
                {
                    AbortLaunch(finalReason);
                    return;
                }
                separationQueued = true;
                ShipSeparationUtility.Launch(this);
            }
        }

        private string LaunchSequenceProblem()
        {
            List<TransferIssue> issues = TransferReadiness.Check(this, plan, includeWarnings: false);
            TransferIssue error = issues.FirstOrDefault(i => i.IsError);
            if (error != null)
            {
                return error.text;
            }
            if (state == TransferConsoleState.Countdown)
            {
                // 倒數中鋼樑操作已鎖定，仍改變只可能是樁被摧毀/搬走（設計稿 §3-8）
                PileNetwork net = Network;
                if (net == null || net.piles.Count != lockedPiles.Count)
                {
                    return "DMSE.Transfer.Abort.NetworkChanged".Translate();
                }
                for (int i = 0; i < lockedPiles.Count; i++)
                {
                    Thing p = lockedPiles[i];
                    if (p == null || !p.Spawned || !net.Contains(p.TryGetComp<CompStructuralPile>()))
                    {
                        return "DMSE.Transfer.Abort.NetworkChanged".Translate();
                    }
                }
            }
            return null;
        }

        // ── 生命週期 ─────────────────────────────────────────────────────────

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_PileNetwork.For(parent.Map)?.RegisterConsole(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_PileNetwork.For(map)?.DeregisterConsole(this);
            // 隨船搬移時保留計畫與狀態；其他情況（拆除、摧毀）計畫失效
            if (mode != DestroyMode.WillReplace && !parent.BeingTransportedOnGravship)
            {
                AbortPlan();
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                lockedPiles.RemoveAll(t => t == null || t.Destroyed);
            }
            Scribe_Values.Look(ref shipName, "shipName");
            Scribe_Deep.Look(ref plan, "transferPlan");
            Scribe_Values.Look(ref state, "transferState", TransferConsoleState.Idle);
            Scribe_Values.Look(ref countdownEndTick, "countdownEndTick", -1);
            Scribe_Values.Look(ref showFootprint, "showFootprint");
            Scribe_Collections.Look(ref lockedPiles, "lockedPiles", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (lockedPiles == null)
                {
                    lockedPiles = new List<Thing>();
                }
                lockedPiles.RemoveAll(t => t == null);
                if (plan == null && state != TransferConsoleState.Idle)
                {
                    state = TransferConsoleState.Idle;
                }
            }
        }

        // ── 顯示 ─────────────────────────────────────────────────────────────

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            PileNetwork net = Network;
            if (net != null)
            {
                PileDrawUtility.DrawShipOverlay(net);
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned)
            {
                return null;
            }
            StringBuilder sb = new StringBuilder();
            TransferShipSurvey survey = Survey;
            if (survey == null)
            {
                sb.Append("DMSE.Transfer.Console.NotOnShip".Translate().Colorize(ColorLibrary.RedReadable));
            }
            else
            {
                sb.Append(IsPrimary ? "DMSE.Transfer.Console.Primary".Translate() : "DMSE.Transfer.Console.Backup".Translate());
                sb.AppendLine();
                string name = ShipName;
                sb.Append("DMSE.Transfer.Console.ShipName".Translate(name.NullOrEmpty() ? "DMSE.Transfer.Console.Unnamed".Translate().ToString() : name));
                sb.AppendLine();
                sb.Append("DMSE.Transfer.Console.ShipSummary".Translate(survey.network.piles.Count, survey.operationalEngines, survey.TWR.ToString("0.##"), survey.totalFuel.ToString("0")));
            }
            if (plan != null && state != TransferConsoleState.Idle)
            {
                sb.AppendLine();
                if (plan.isImpact)
                {
                    sb.Append("DMSE.Transfer.Console.PlanSummaryImpact".Translate(plan.destination.ToString(), plan.distance).Colorize(ColorLibrary.RedReadable));
                }
                else
                {
                    sb.Append("DMSE.Transfer.Console.PlanSummary".Translate(plan.destination.ToString(), plan.distance));
                }
            }
            WorldObject_TransferShip flying = parent.Map.Parent as WorldObject_TransferShip;
            if (flying != null && flying.InFlight)
            {
                sb.AppendLine();
                sb.Append(flying.FlightStatusText());
            }
            if (state == TransferConsoleState.AwaitingOperator)
            {
                sb.AppendLine();
                sb.Append("DMSE.Transfer.Console.StateAwaitingOperator".Translate());
            }
            else if (state == TransferConsoleState.Countdown)
            {
                sb.AppendLine();
                sb.Append("DMSE.Transfer.Console.StateCountdown".Translate(CountdownTicksLeft.ToStringTicksToPeriod()));
            }
            return sb.ToString();
        }

        // ── Gizmo ────────────────────────────────────────────────────────────

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }
            if (parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }

            if (state == TransferConsoleState.Planned)
            {
                Command_Action launch = new Command_Action
                {
                    defaultLabel = "DMSE.Transfer.Console.Launch".Translate(),
                    defaultDesc = "DMSE.Transfer.Console.LaunchDesc".Translate(),
                    icon = PileResources.LaunchTransferIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = BeginLaunchSequence
                };
                string blocked = LaunchBlockedReason();
                if (blocked != null)
                {
                    launch.Disable(blocked);
                }
                yield return launch;
            }

            Command_Action planCmd = new Command_Action
            {
                defaultLabel = "DMSE.Transfer.Console.Plan".Translate(),
                defaultDesc = "DMSE.Transfer.Console.PlanDesc".Translate(),
                icon = PileResources.PlanTransferIcon,
                alsoClickIfOtherInGroupClicked = false,
                action = delegate { TransferPlanningUtility.StartPlanning(this); }
            };
            string reason = TransferPlanningUtility.CannotPlanReason(this);
            if (reason != null)
            {
                planCmd.Disable(reason);
            }
            yield return planCmd;

            // 撞擊飛行（設計稿 §9）：與舊版模式選單相同，撞擊用引擎未達門檻時不顯示
            if (TransferPlanningUtility.ImpactAvailable(this))
            {
                Command_Action impactCmd = new Command_Action
                {
                    defaultLabel = "DMSE.Transfer.Console.PlanImpact".Translate(),
                    defaultDesc = "DMSE.Transfer.Console.PlanImpactDesc".Translate(TransferFlightMath.Tuning.impactMinEngines, TransferFlightMath.Tuning.planetkillerMinEngines),
                    icon = PileResources.PlanImpactIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate { TransferPlanningUtility.StartPlanningImpact(this); }
                };
                if (reason != null)
                {
                    impactCmd.Disable(reason);
                }
                yield return impactCmd;
            }

            if (state != TransferConsoleState.Idle)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DMSE.Transfer.Console.Abort".Translate(),
                    defaultDesc = "DMSE.Transfer.Console.AbortDesc".Translate(),
                    icon = PileResources.AbortTransferIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = AbortPlan
                };
            }

            yield return new Command_Action
            {
                defaultLabel = "DMSE.Transfer.Console.Rename".Translate(),
                defaultDesc = "DMSE.Transfer.Console.RenameDesc".Translate(),
                icon = TexButton.Rename,
                alsoClickIfOtherInGroupClicked = false,
                action = delegate { Find.WindowStack.Add(new Dialog_RenameTransferShip(this)); }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "DMSE.Transfer.Console.ShowFootprint".Translate(),
                defaultDesc = "DMSE.Transfer.Console.ShowFootprintDesc".Translate(),
                icon = PileResources.ShowFootprintIcon,
                isActive = () => showFootprint,
                toggleAction = delegate { showFootprint = !showFootprint; }
            };

            if (DebugSettings.ShowDevGizmos)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Fill ship fuel",
                    action = DebugFillFuel
                };
                if (state == TransferConsoleState.AwaitingOperator)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Finish launch procedure",
                        action = BeginCountdown
                    };
                }
                if (state == TransferConsoleState.Countdown)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: End countdown",
                        action = delegate { countdownEndTick = Find.TickManager.TicksGame; }
                    };
                }
                if (Network != null && !InLaunchSequence)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Separate now (no flight)",
                        defaultDesc = "Detach this ship into its own map parked next to the current tile. Skips readiness checks and fuel.",
                        action = delegate { ShipSeparationUtility.DebugSeparateNow(this); }
                    };
                }
            }
        }

        private void DebugFillFuel()
        {
            TransferShipSurvey survey = Survey;
            if (survey == null)
            {
                return;
            }
            for (int i = 0; i < survey.fuelSources.Count; i++)
            {
                survey.fuelSources[i].DebugFill();
            }
            survey.network.InvalidateSurvey();
        }
    }
}
