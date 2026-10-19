using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 波次打擊視窗（docs/BVR_波次打擊規劃.md §10）。
    ///   - 規劃模式（草稿，暫停遊戲）：左側候選導彈依彈種分組（可展開指定單枚），右側波次與抵達預覽，底部時間軸。
    ///   - 調整模式（執行中的任務，暫停遊戲）：同規劃介面；已發射、略過的導彈固定不動，可增刪移動待發射的導彈。
    ///   - 執行模式（不暫停）：逐波狀態、預計抵達與順序警示，發射；Shift 只發射現在辦得到的部分。
    /// </summary>
    public class Dialog_StrikePlan : Window
    {
        private const float RowHeight = 28f;
        private const float ButtonHeight = 34f;
        private const float TimelineRow = 14f;
        private static readonly Color WarnColor = new Color(1f, 0.85f, 0.35f);
        private static readonly Color FixedColor = new Color(0.65f, 0.65f, 0.65f);
        private static readonly Color[] WaveColors =
        {
            new Color(0.45f, 0.75f, 1f), new Color(0.55f, 0.9f, 0.55f), new Color(1f, 0.7f, 0.4f),
            new Color(0.85f, 0.6f, 1f), new Color(1f, 0.55f, 0.6f),
        };

        private readonly CompStrikeConsole console;
        private readonly StrikeMission mission;
        private readonly List<StrikeWave> editWaves;
        private List<StrikeCandidate> candidates = new List<StrikeCandidate>();
        private Dictionary<Thing, StrikeCandidate> byRound = new Dictionary<Thing, StrikeCandidate>();
        private readonly HashSet<string> expanded = new HashSet<string>();
        private int reservedElsewhere;
        private int selectedWave;
        private bool showUnavailable;
        private Vector2 leftScroll, rightScroll, execScroll;
        private float leftHeight, rightHeight, execHeight;

        private bool Planning => mission.state == StrikeMissionState.Planning;
        private bool Editing => editWaves != null;
        private bool Composing => Planning || Editing;
        private List<StrikeWave> Waves => editWaves ?? mission.waves;
        private int CandidateOwner => Planning ? -1 : mission.id;

        public override Vector2 InitialSize => Composing ? new Vector2(1000f, 720f) : new Vector2(700f, 600f);

        public Dialog_StrikePlan(CompStrikeConsole console, StrikeMission mission, bool edit = false)
        {
            this.console = console;
            this.mission = mission;
            if (edit) { editWaves = console.AdjustmentCopy(); }
            bool composing = mission.state == StrikeMissionState.Planning || edit;
            forcePause = composing;
            absorbInputAroundWindow = composing;
            doCloseX = true;
            closeOnClickedOutside = false;
            draggable = !composing;
            if (composing) { RefreshCandidates(); }
        }

        private void RefreshCandidates()
        {
            candidates = StrikeRoundUtility.Candidates(console.parent.Map, mission.CurrentTargetTile, CandidateOwner, out reservedElsewhere);
            byRound = candidates.Where(c => c.round != null).ToDictionary(c => c.round);
            // 已不能用的待發射導彈從計畫移除（已發射、略過的固定不動）
            foreach (StrikeWave wave in Waves)
            {
                wave.slots.RemoveAll(s => s.state == StrikeSlotState.Pending
                    && (s.round == null || !byRound.TryGetValue(s.round, out StrikeCandidate c) || !c.Assignable));
            }
        }

        private float Distance => MissileWorldUtility.RangeDistance(console.parent.Map.Tile, mission.CurrentTargetTile);

        private bool SiteWarning => mission.targetParent != null && !mission.targetParent.Destroyed
            && !mission.targetParent.HasMap && mission.targetParent.Faction != Faction.OfPlayer;

        private static string Rel(int ticks, int now)
        {
            int d = ticks - now;
            return d <= 1 ? "DMSE.Strike.Plan.Now".Translate().ToString() : "+" + d.ToStringTicksToPeriod();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (console.parent.DestroyedOrNull() || !console.parent.Spawned || (!Planning && console.mission != mission))
            {
                Close();
                return;
            }
            if (Composing) { DoComposing(inRect); }
            else { DoExecution(inRect); }
        }

        private float Line(Rect inRect, float y, string text, Color? color = null)
        {
            float h = Text.CalcHeight(text, inRect.width);
            GUI.color = color ?? Color.white;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, h), text);
            GUI.color = Color.white;
            return y + h + 2f;
        }

        private float Header(Rect inRect, string title)
        {
            Text.Font = GameFont.Medium;
            float titleWidth = Mathf.Min(Text.CalcSize(title).x, inRect.width - 70f);
            Widgets.Label(new Rect(inRect.x, inRect.y, titleWidth, 34f), title.Truncate(titleWidth));
            Text.Font = GameFont.Small;
            // 任務隨時可重新命名（視窗內可擲隨機名稱）
            Rect renameRect = new Rect(inRect.x + titleWidth + 6f, inRect.y + 5f, 24f, 24f);
            if (Widgets.ButtonImage(renameRect, TexButton.Rename)) { Find.WindowStack.Add(new Dialog_RenameStrikeMission(mission)); }
            TooltipHandler.TipRegion(renameRect, "DMSE.Strike.RenameTip".Translate());
            float y = inRect.y + 36f;
            return Line(inRect, y, "DMSE.Strike.Plan.Target".Translate(mission.TargetLabel, Distance.ToString("0.#")));
        }

        private float TimelineHeight(int waves) => 22f + TimelineRow * Mathf.Max(1, waves);

        // ==================================================================
        //  規劃／調整
        // ==================================================================

        private void DoComposing(Rect inRect)
        {
            int cap = Mathf.Max(1, console.StrikeCapacity);
            float y = Header(inRect, Editing ? "DMSE.Strike.Adjust.Title".Translate(mission.Label) : "DMSE.Strike.Plan.Title".Translate(mission.Label));
            y = Line(inRect, y, "DMSE.Strike.Plan.Channels".Translate(console.StrikeCapacity, console.StrikeFree,
                BVRWorldTuningDef.Current.strikeGuidanceTicks.ToStringTicksToPeriod()));
            if (Planning && console.Props.requiresComputation)
            {
                float work = console.WorkFor(mission);
                float speed = StrikeComputation.FastestResearchSpeed(console.parent.Map);
                string time = speed > 0f ? Mathf.CeilToInt(work / speed).ToStringTicksToPeriod()
                    : "DMSE.Strike.Plan.NoResearcher".Translate().ToString();
                y = Line(inRect, y, "DMSE.Strike.Plan.ComputeWork".Translate(Mathf.RoundToInt(work), time));
            }
            if (SiteWarning) { y = Line(inRect, y, "DMSE.Strike.Plan.SiteWarning".Translate(), WarnColor); }

            List<StrikePlanWave> plan = console.PlanTimeline(mission, Waves);
            float timeline = TimelineHeight(Waves.Count);
            Rect body = new Rect(inRect.x, y + 4f, inRect.width, inRect.yMax - ButtonHeight - 16f - timeline - y);
            DrawCandidates(body.LeftPart(0.48f), cap);
            DrawWaves(body.RightPart(0.5f), cap, plan);
            DrawTimeline(new Rect(inRect.x, body.yMax + 4f, inRect.width, timeline), plan);
            DrawComposingButtons(new Rect(inRect.x, inRect.yMax - ButtonHeight, inRect.width, ButtonHeight), cap);
        }

        private HashSet<Thing> Assigned() => new HashSet<Thing>(Waves.SelectMany(w => w.slots).Where(s => s.round != null).Select(s => s.round));

        private void DrawCandidates(Rect rect, int cap)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(6f);
            HashSet<Thing> assigned = Assigned();
            List<StrikeCandidate> available = candidates.Where(c => c.Assignable && !assigned.Contains(c.round)).ToList();
            List<StrikeCandidate> unavailable = candidates.Where(c => !c.Assignable).ToList();

            float y = Line(inner, inner.y, "DMSE.Strike.Plan.Available".Translate(available.Count));
            Text.Font = GameFont.Tiny;
            y = Line(inner, y, "DMSE.Strike.Plan.AddHint".Translate(), Color.gray);
            Text.Font = GameFont.Small;

            Rect outRect = new Rect(inner.x, y, inner.width, inner.yMax - y);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(leftHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref leftScroll, view);
            float cy = 0f;
            foreach (IGrouping<string, StrikeCandidate> group in available.GroupBy(c => c.groupKey).OrderBy(g => g.Min(c => c.value)))
            {
                List<StrikeCandidate> list = group.ToList();
                Rect row = new Rect(0f, cy, view.width, RowHeight);
                Rect toggle = new Rect(row.x, row.y + 4f, 20f, 20f);
                Widgets.DrawHighlightIfMouseover(row);
                bool open = expanded.Contains(group.Key);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(toggle, open ? "▾" : "▸");
                Text.Anchor = TextAnchor.UpperLeft;
                DrawCandidateRow(new Rect(row.x + 20f, row.y, row.width - 20f, row.height), list);
                if (Widgets.ButtonInvisible(toggle))
                {
                    if (open) { expanded.Remove(group.Key); } else { expanded.Add(group.Key); }
                }
                else if (Widgets.ButtonInvisible(row))
                {
                    AddToSelectedWave(list, Event.current.shift ? int.MaxValue : 1, cap);
                }
                cy += RowHeight;
                if (!open) { continue; }
                foreach (StrikeCandidate c in list)
                {
                    Rect sub = new Rect(24f, cy, view.width - 24f, 24f);
                    Widgets.DrawHighlightIfMouseover(sub);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    GUI.color = c.issue != StrikeRoundIssue.None ? WarnColor : Color.white;
                    string label = c.launcher.parent.LabelShort + " · " + c.round.LabelShort
                        + (c.issue != StrikeRoundIssue.None ? " — " + c.issueText : string.Empty);
                    Widgets.Label(sub, label.Truncate(sub.width));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    if (Widgets.ButtonInvisible(sub)) { AddToSelectedWave(new List<StrikeCandidate> { c }, 1, cap); }
                    cy += 24f;
                }
            }

            if (unavailable.Count > 0)
            {
                cy += 6f;
                Rect head = new Rect(0f, cy, view.width, RowHeight);
                Widgets.DrawHighlightIfMouseover(head);
                string summary = string.Join(", ", unavailable.GroupBy(c => c.issueText).Select(g => g.Key + " ×" + g.Count()));
                GUI.color = Color.gray;
                Widgets.Label(head, ((showUnavailable ? "▾ " : "▸ ")
                    + "DMSE.Strike.Plan.Unavailable".Translate(unavailable.Count, summary)).Truncate(head.width));
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(head)) { showUnavailable = !showUnavailable; }
                cy += RowHeight;
                if (showUnavailable)
                {
                    foreach (IGrouping<string, StrikeCandidate> group in unavailable.GroupBy(c => c.groupKey + "|" + c.issueText))
                    {
                        StrikeCandidate first = group.First();
                        Rect row = new Rect(12f, cy, view.width - 12f, RowHeight);
                        GUI.color = Color.gray;
                        Widgets.Label(row, (first.groupLabel + " ×" + group.Count() + " — " + first.issueText).Truncate(row.width));
                        GUI.color = Color.white;
                        cy += RowHeight;
                    }
                }
            }
            if (reservedElsewhere > 0)
            {
                cy += 6f;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, cy, view.width, RowHeight), "DMSE.Strike.Plan.ReservedElsewhere".Translate(reservedElsewhere));
                GUI.color = Color.white;
                cy += RowHeight;
            }
            leftHeight = cy;
            Widgets.EndScrollView();
        }

        private void DrawCandidateRow(Rect row, List<StrikeCandidate> list)
        {
            StrikeCandidate first = list[0];
            bool notReady = list.Any(c => c.issue != StrikeRoundIssue.None);
            float w = row.width;
            Rect labelRect = new Rect(row.x + 2f, row.y, w * 0.55f - 2f, row.height);
            Rect flightRect = new Rect(row.x + w * 0.55f, row.y, w * 0.15f, row.height);
            Rect marginRect = new Rect(row.x + w * 0.70f, row.y, w * 0.15f, row.height);
            Rect valueRect = new Rect(row.x + w * 0.85f, row.y, w * 0.15f - 4f, row.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            if (notReady) { GUI.color = WarnColor; }
            Widgets.Label(labelRect, (first.groupLabel + " ×" + list.Count).Truncate(labelRect.width));
            GUI.color = Color.white;
            Widgets.Label(flightRect, first.flightTicks.ToStringTicksToPeriod());
            int range = first.config?.Range ?? 0;
            if (range > 0) { Widgets.Label(marginRect, "DMSE.Strike.Plan.Margin".Translate(Mathf.FloorToInt(range - first.distance))); }
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(valueRect, first.value.ToStringMoney());
            Text.Anchor = TextAnchor.UpperLeft;

            string tip = first.groupLabel + "\n" + "DMSE.Strike.Plan.Flight".Translate(first.flightTicks.ToStringTicksToPeriod());
            foreach (string issue in list.Where(c => c.issue != StrikeRoundIssue.None).Select(c => c.issueText).Distinct())
            {
                tip += "\n" + "DMSE.Strike.Plan.NotReadyTag".Translate() + " " + issue;
            }
            TooltipHandler.TipRegion(row, tip);
        }

        private static int PendingCount(StrikeWave wave) => wave.slots.Count(s => s.state == StrikeSlotState.Pending);

        private void AddToSelectedWave(List<StrikeCandidate> list, int count, int cap)
        {
            if (Waves.Count == 0) { Waves.Add(new StrikeWave()); }
            selectedWave = Mathf.Clamp(selectedWave, 0, Waves.Count - 1);
            StrikeWave wave = Waves[selectedWave];
            int total = Waves.Sum(w => w.slots.Count);
            int room = Mathf.Min(cap - PendingCount(wave), console.Props.maxRoundsPerMission - total);
            if (room <= 0)
            {
                Messages.Message(PendingCount(wave) >= cap
                    ? "DMSE.Strike.WaveOverCap".Translate(selectedWave + 1, PendingCount(wave), cap)
                    : "DMSE.Strike.TooManyRounds".Translate(console.Props.maxRoundsPerMission), MessageTypeDefOf.RejectInput, false);
                return;
            }
            foreach (StrikeCandidate c in list.OrderBy(c => c.issue == StrikeRoundIssue.None ? 0 : 1).Take(Mathf.Min(count, room)))
            {
                wave.slots.Add(new StrikeSlot { launcher = c.launcher.parent, round = c.round });
            }
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }

        private string SlotGroupLabel(StrikeSlot s)
        {
            if (s.round != null && byRound.TryGetValue(s.round, out StrikeCandidate c)) { return c.groupLabel; }
            return s.round?.LabelCap.ToString() ?? s.launcher?.LabelCap.ToString() ?? "?";
        }

        private static string StateTag(StrikeSlotState state)
        {
            switch (state)
            {
                case StrikeSlotState.Launched: return "DMSE.Strike.Slot.Launched".Translate();
                case StrikeSlotState.Queued: return "DMSE.Strike.Slot.Queued".Translate();
                case StrikeSlotState.Skipped: return "DMSE.Strike.Slot.Skipped".Translate();
                default: return string.Empty;
            }
        }

        /// <summary>波次的預計升空／抵達說明與順序警示（規劃、調整、執行三種模式共用）。</summary>
        private string ArrivalText(StrikePlanWave p, StrikeWave wave, int now, out bool warn)
        {
            warn = false;
            if (p == null) { return null; }
            if (p.overCapacity) { warn = true; return null; }
            if (p.arriveMin < 0) { return null; }
            string text = PendingCount(wave) > 0
                ? "DMSE.Strike.Plan.Arrival".Translate(Rel(p.fireTick, now), Rel(p.arriveMin, now), Rel(p.arriveMax, now))
                : "DMSE.Strike.Plan.ArrivalFlying".Translate(Rel(p.arriveMin, now), Rel(p.arriveMax, now));
            if (p.earlierThan >= 0)
            {
                warn = true;
                text += "  " + "DMSE.Strike.Plan.EarlyWarning".Translate(p.earlierThan + 1, p.suggestedDelay.ToStringTicksToPeriod());
            }
            return text;
        }

        private void DrawWaves(Rect rect, int cap, List<StrikePlanWave> plan)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(6f);
            Rect view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(rightHeight, inner.height));
            Widgets.BeginScrollView(inner, ref rightScroll, view);
            int now = Find.TickManager.TicksGame;
            float y = 0f;
            int remove = -1;
            for (int i = 0; i < Waves.Count; i++)
            {
                StrikeWave wave = Waves[i];
                StrikePlanWave p = i < plan.Count ? plan[i] : null;
                bool hasFixed = wave.slots.Any(s => s.state != StrikeSlotState.Pending);
                Rect head = new Rect(0f, y, view.width, 30f);
                if (i == selectedWave) { Widgets.DrawHighlightSelected(head); }
                else { Widgets.DrawHighlightIfMouseover(head); }
                Rect deleteRect = new Rect(head.xMax - 26f, head.y + 3f, 24f, 24f);
                // 已全部發射或略過的波次（調整模式）不需要定時發射
                float autoLeft = hasFixed && !wave.slots.Any(s => s.state == StrikeSlotState.Pending)
                    ? deleteRect.x : DrawAutoFireToggle(deleteRect.x - 6f, head.y + 3f, wave);
                Rect titleRect = new Rect(head.x + 4f, head.y, autoLeft - head.x - 8f, head.height);
                Text.Anchor = TextAnchor.MiddleLeft;
                int pending = PendingCount(wave);
                string title = "DMSE.Strike.Plan.WaveTitle".Translate(i + 1);
                float titleWidth = Mathf.Min(Text.CalcSize(title).x, titleRect.width);
                GUI.color = pending > cap ? ColorLibrary.RedReadable : WaveColors[i % WaveColors.Length];
                Widgets.Label(new Rect(titleRect.x, titleRect.y, titleWidth, titleRect.height), title.Truncate(titleWidth));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                // 已全部發射的波次（調整模式）不再占用通道，不畫方格
                if (pending > 0 || !hasFixed)
                {
                    DrawChannelCells(new Rect(titleRect.x + titleWidth + 8f, head.y + 7f, titleRect.width - titleWidth - 8f, 16f), pending, cap);
                    TooltipHandler.TipRegion(titleRect, "DMSE.Strike.Plan.WaveChannelsTip".Translate(pending, cap));
                }
                if (Waves.Count > 1 && !hasFixed && Widgets.ButtonImage(deleteRect, TexButton.Delete)) { remove = i; }
                else if (Widgets.ButtonInvisible(titleRect)) { selectedWave = i; }
                y += 30f;

                string arrival = ArrivalText(p, wave, now, out bool warn);
                if (!arrival.NullOrEmpty())
                {
                    Text.Font = GameFont.Tiny;
                    float h = Text.CalcHeight(arrival, view.width - 8f);
                    GUI.color = warn ? WarnColor : Color.gray;
                    Widgets.Label(new Rect(8f, y, view.width - 8f, h), arrival);
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                    y += h + 2f;
                }

                foreach (IGrouping<string, StrikeSlot> group in wave.slots
                    .GroupBy(s => (s.state == StrikeSlotState.Pending ? "" : StateTag(s.state) + "|") + SlotGroupLabel(s)).ToList())
                {
                    StrikeSlot first = group.First();
                    bool isFixed = first.state != StrikeSlotState.Pending;
                    Rect row = new Rect(16f, y, view.width - 16f, 24f);
                    Rect minus = new Rect(row.xMax - 26f, row.y, 24f, 24f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    GUI.color = isFixed ? FixedColor : Color.white;
                    string label = SlotGroupLabel(first) + " ×" + group.Count() + (isFixed ? "  (" + StateTag(first.state) + ")" : string.Empty);
                    Widgets.Label(new Rect(row.x, row.y, row.width - 30f, row.height), label.Truncate(row.width - 30f));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    if (!isFixed && Widgets.ButtonText(minus, "−"))
                    {
                        wave.slots.Remove(group.Last());
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                    }
                    y += 26f;
                }
                y += 6f;
            }
            if (remove >= 0)
            {
                Waves.RemoveAt(remove);
                selectedWave = Mathf.Clamp(selectedWave, 0, Waves.Count - 1);
            }
            if (Waves.Count < console.Props.maxWavesPerMission)
            {
                if (Widgets.ButtonText(new Rect(0f, y, 160f, 28f), "DMSE.Strike.Plan.AddWave".Translate()))
                {
                    Waves.Add(new StrikeWave());
                    selectedWave = Waves.Count - 1;
                }
                y += 32f;
            }
            rightHeight = y;
            Widgets.EndScrollView();
        }

        /// <summary>時間軸：每波一列，細線為升空到最早抵達，色塊為抵達區間；會早到的波次標黃。</summary>
        private void DrawTimeline(Rect rect, List<StrikePlanWave> plan)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(4f);
            int now = Find.TickManager.TicksGame;
            int end = now + 1;
            foreach (StrikePlanWave p in plan)
            {
                end = Mathf.Max(end, p.arriveMax, p.fireTick);
            }
            Text.Font = GameFont.Tiny;
            Rect labelCol = new Rect(inner.x, inner.y, 34f, inner.height);
            Rect track = new Rect(inner.x + 38f, inner.y, inner.width - 42f, inner.height - 12f);
            float X(int t) => track.x + track.width * Mathf.Clamp01((t - now) / (float)(end - now));
            for (int i = 0; i < plan.Count; i++)
            {
                StrikePlanWave p = plan[i];
                float y = track.y + i * TimelineRow;
                Widgets.Label(new Rect(labelCol.x, y, labelCol.width, TimelineRow), "W" + (i + 1));
                if (p.arriveMin < 0) { continue; }
                Color color = p.earlierThan >= 0 ? WarnColor : WaveColors[i % WaveColors.Length];
                if (p.fireTick >= 0)
                {
                    Widgets.DrawBoxSolid(new Rect(X(p.fireTick), y + TimelineRow / 2f - 0.5f, Mathf.Max(1f, X(p.arriveMin) - X(p.fireTick)), 1f), Color.gray);
                    Widgets.DrawBoxSolid(new Rect(X(p.fireTick) - 1f, y + 3f, 2f, TimelineRow - 6f), Color.gray);
                }
                Widgets.DrawBoxSolid(new Rect(X(p.arriveMin), y + 2f, Mathf.Max(3f, X(p.arriveMax) - X(p.arriveMin)), TimelineRow - 4f), color);
            }
            GUI.color = Color.gray;
            Widgets.Label(new Rect(track.x, track.yMax, 120f, 12f), "DMSE.Strike.Plan.Now".Translate());
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(track.xMax - 160f, track.yMax, 160f, 12f), "+" + (end - now).ToStringTicksToPeriod());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(rect, "DMSE.Strike.Plan.TimelineTip".Translate());
        }

        private void AutoAssign(int cap)
        {
            List<StrikeCandidate> pool = candidates.Where(c => c.Assignable)
                .OrderBy(c => c.value).ThenBy(c => c.issue == StrikeRoundIssue.None ? 0 : 1).ToList();
            List<List<int>> plan = StrikeRoundUtility.AutoAssign(Mathf.Min(pool.Count, console.Props.maxRoundsPerMission),
                cap, console.Props.maxWavesPerMission);
            mission.waves.Clear();
            foreach (List<int> indices in plan)
            {
                var wave = new StrikeWave();
                foreach (int i in indices) { wave.slots.Add(new StrikeSlot { launcher = pool[i].launcher.parent, round = pool[i].round }); }
                mission.waves.Add(wave);
            }
            if (mission.waves.Count == 0) { mission.waves.Add(new StrikeWave()); }
            selectedWave = 0;
        }

        private void DrawComposingButtons(Rect rect, int cap)
        {
            float x = rect.x;
            if (Planning)
            {
                Rect auto = new Rect(x, rect.y, 150f, ButtonHeight);
                if (Widgets.ButtonText(auto, "DMSE.Strike.Plan.AutoAssign".Translate())) { AutoAssign(cap); }
                TooltipHandler.TipRegion(auto, "DMSE.Strike.Plan.AutoAssignDesc".Translate());
                x = auto.xMax + 8f;
            }
            Rect clear = new Rect(x, rect.y, Editing ? 150f : 100f, ButtonHeight);
            if (Widgets.ButtonText(clear, Editing ? "DMSE.Strike.Adjust.ClearPending".Translate() : "DMSE.Strike.Plan.Clear".Translate()))
            {
                if (Editing)
                {
                    foreach (StrikeWave wave in Waves) { wave.slots.RemoveAll(s => s.state == StrikeSlotState.Pending); }
                }
                else
                {
                    mission.waves.Clear();
                    mission.waves.Add(new StrikeWave());
                    selectedWave = 0;
                }
            }

            Rect confirm = new Rect(rect.xMax - 160f, rect.y, 160f, ButtonHeight);
            Rect cancel = new Rect(confirm.x - 168f, rect.y, 160f, ButtonHeight);
            string invalid = Editing ? console.ValidateAdjustment(Waves) : console.ValidateDraft(mission);
            if (invalid != null)
            {
                Rect error = new Rect(clear.xMax + 8f, rect.y, cancel.x - clear.xMax - 16f, ButtonHeight);
                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(error, invalid.Truncate(error.width));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }
            if (Widgets.ButtonText(cancel, Editing ? "DMSE.Strike.Adjust.Cancel".Translate() : "CancelButton".Translate()))
            {
                Close();
                if (Editing) { Find.WindowStack.Add(new Dialog_StrikePlan(console, mission)); }
            }
            string confirmLabel = Editing ? "DMSE.Strike.Adjust.Apply".Translate() : "DMSE.Strike.Plan.Confirm".Translate();
            if (Widgets.ButtonText(confirm, confirmLabel, active: invalid == null))
            {
                bool ok;
                string reason;
                if (Editing) { ok = console.ApplyAdjustment(Waves, out reason); }
                else { ok = console.Commit(mission, out reason); }
                if (ok)
                {
                    Close();
                    if (Editing && console.mission == mission) { Find.WindowStack.Add(new Dialog_StrikePlan(console, mission)); }
                }
                else
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    RefreshCandidates();
                }
            }
        }

        // ==================================================================
        //  執行模式
        // ==================================================================

        private void DoExecution(Rect inRect)
        {
            float y = Header(inRect, "DMSE.Strike.Exec.Title".Translate(mission.Label));
            y = Line(inRect, y, "DMSE.Strike.Exec.State".Translate(console.StateLabel));
            string channels = "DMSE.Strike.Inspect.Channels".Translate(console.StrikeFree, console.StrikeCapacity);
            int release = console.parent.Map.GetComponent<MapComponent_StrikePlanner>()?.TicksUntilNextRelease(console.parent.Faction) ?? -1;
            if (release > 0) { channels += "  " + "DMSE.Strike.Exec.NextRelease".Translate(release.ToStringTicksToPeriod()); }
            y = Line(inRect, y, channels);

            List<StrikePlanWave> plan = console.PlanTimeline(mission, mission.waves);
            float timeline = TimelineHeight(mission.waves.Count);
            Rect outRect = new Rect(inRect.x, y + 4f, inRect.width, inRect.yMax - ButtonHeight - 16f - timeline - y);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(execHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref execScroll, view);
            float cy = 0f;
            for (int i = 0; i < mission.waves.Count; i++)
            {
                DrawExecutionWave(new Rect(0f, cy, view.width, 70f), i, i < plan.Count ? plan[i] : null);
                cy += 74f;
            }
            execHeight = cy;
            Widgets.EndScrollView();
            DrawTimeline(new Rect(inRect.x, outRect.yMax + 4f, inRect.width, timeline), plan);

            Rect bottom = new Rect(inRect.x, inRect.yMax - ButtonHeight, inRect.width, ButtonHeight);
            if (Widgets.ButtonText(new Rect(bottom.x, bottom.y, 150f, ButtonHeight), "DMSE.Strike.Exec.CancelMission".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "DMSE.Strike.Console.CancelConfirm".Translate(mission.Label), () => console.CancelMission(), destructive: true));
            }
            Rect adjust = new Rect(bottom.x + 158f, bottom.y, 150f, ButtonHeight);
            string cannotAdjust = console.CannotAdjustReason();
            if (Widgets.ButtonText(adjust, "DMSE.Strike.Adjust.Button".Translate(), active: cannotAdjust == null))
            {
                Close();
                Find.WindowStack.Add(new Dialog_StrikePlan(console, mission, edit: true));
            }
            if (cannotAdjust != null) { TooltipHandler.TipRegion(adjust, cannotAdjust); }
            if (Widgets.ButtonText(new Rect(bottom.xMax - 120f, bottom.y, 120f, ButtonHeight), "CloseButton".Translate())) { Close(); }
        }

        private static readonly Color ChannelEmptyColor = new Color(0.3f, 0.3f, 0.3f, 1f);

        /// <summary>
        /// 仿原版機械師頻寬 Gizmo 的方格：黃色＝本波占用的導引通道，灰色＝空閒，紅色＝超出容量。
        /// 方格依可用寬度縮小（太小就只顯示數字），後面附上「占用/容量」小字。
        /// </summary>
        private static void DrawChannelCells(Rect rect, int used, int capacity)
        {
            int cells = Mathf.Max(used, capacity);
            if (cells <= 0 || rect.width <= 0f) { return; }
            string count = used + "/" + capacity;
            Text.Font = GameFont.Tiny;
            float countWidth = Text.CalcSize(count).x + 4f;
            float cell = Mathf.Min(rect.height, (rect.width - countWidth - 6f) / cells);
            float x = rect.x;
            if (cell >= 6f)
            {
                float pad = cell >= 10f ? 2f : 1f;
                for (int k = 0; k < cells; k++)
                {
                    Rect r = new Rect(x + k * cell, rect.y + (rect.height - cell) / 2f, cell, cell).ContractedBy(pad);
                    Widgets.DrawRectFast(r, k >= used ? ChannelEmptyColor : k < capacity ? ColorLibrary.Yellow : ColorLibrary.Red);
                }
                x += cells * cell + 6f;
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = used > capacity ? ColorLibrary.RedReadable : Color.gray;
            Widgets.Label(new Rect(x, rect.y - 4f, Mathf.Min(countWidth, rect.xMax - x), rect.height + 8f), count);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        private static float AutoFireToggleWidth
        {
            get
            {
                Text.Font = GameFont.Tiny;
                float w = Text.CalcSize("DMSE.Strike.Plan.AutoFire".Translate()).x;
                Text.Font = GameFont.Small;
                return w + 30f;
            }
        }

        /// <summary>
        /// 定時發射勾選框，右緣對齊 <paramref name="right"/>，回傳左緣。前面加短標籤：
        /// 未勾選的紅叉緊鄰刪除鈕時容易被看成「移除」。標籤也可點擊切換。
        /// </summary>
        private static float DrawAutoFireToggle(float right, float y, StrikeWave wave)
        {
            string label = "DMSE.Strike.Plan.AutoFire".Translate();
            Text.Font = GameFont.Tiny;
            float labelWidth = Text.CalcSize(label).x + 4f;
            Rect box = new Rect(right - 24f, y, 24f, 24f);
            Rect labelRect = new Rect(box.x - labelWidth - 2f, y, labelWidth, 24f);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = wave.autoFire ? Color.white : Color.gray;
            Widgets.Label(labelRect, label);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            if (Widgets.ButtonInvisible(labelRect)) { wave.autoFire = !wave.autoFire; }
            Widgets.Checkbox(box.position, ref wave.autoFire, 24f);
            TooltipHandler.TipRegion(new Rect(labelRect.x, y, box.xMax - labelRect.x, 24f), "DMSE.Strike.Plan.AutoFireTip".Translate());
            return labelRect.x;
        }

        private void DrawExecutionWave(Rect row, int index, StrikePlanWave p)
        {
            StrikeWave wave = mission.waves[index];
            if (index % 2 == 0) { Widgets.DrawLightHighlight(row); }
            Rect fireRect = new Rect(row.xMax - 124f, row.y + 16f, 120f, ButtonHeight);
            Rect text = new Rect(row.x + 6f, row.y + 2f, fireRect.x - row.x - 12f - AutoFireToggleWidth, row.height - 4f);

            string groups = string.Join(", ", wave.slots.GroupBy(s => s.round?.LabelShort ?? s.launcher?.LabelShort ?? "?")
                .Select(g => g.Key + " ×" + g.Count()));
            var parts = new List<string>();
            int pending = wave.Count(StrikeSlotState.Pending);
            int queued = wave.Count(StrikeSlotState.Queued);
            int launched = wave.Count(StrikeSlotState.Launched);
            int skipped = wave.Count(StrikeSlotState.Skipped);
            if (pending > 0) { parts.Add("DMSE.Strike.Exec.WavePending".Translate(pending)); }
            if (queued > 0) { parts.Add("DMSE.Strike.Exec.WaveQueued".Translate(queued)); }
            if (launched > 0) { parts.Add("DMSE.Strike.Exec.WaveLaunched".Translate(launched, wave.slots.Count, wave.slots.Count(s => s.InFlight))); }
            if (skipped > 0) { parts.Add("DMSE.Strike.Exec.WaveSkipped".Translate(skipped)); }
            GUI.color = WaveColors[index % WaveColors.Length];
            Widgets.Label(new Rect(text.x, text.y, text.width, 22f), "DMSE.Strike.Exec.Wave".Translate(index + 1, groups).ToString().Truncate(text.width));
            GUI.color = Color.white;
            Widgets.Label(new Rect(text.x, text.y + 22f, text.width, 22f), string.Join("; ", parts).Truncate(text.width));
            string arrival = ArrivalText(p, wave, Find.TickManager.TicksGame, out bool warn);
            if (!arrival.NullOrEmpty())
            {
                Text.Font = GameFont.Tiny;
                GUI.color = warn ? WarnColor : Color.gray;
                Widgets.Label(new Rect(text.x, text.y + 44f, text.width, 22f), arrival.Truncate(text.width));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
            string skips = string.Join("\n", wave.slots.Where(s => s.state == StrikeSlotState.Skipped)
                .Select(s => "- " + s.skipReason).Distinct());
            if (!skips.NullOrEmpty()) { TooltipHandler.TipRegion(text, skips); }

            if (!wave.HasPending) { return; }
            DrawAutoFireToggle(fireRect.x - 6f, fireRect.y + 5f, wave);
            string full = console.CannotFireReason(index, partial: false);
            string partialReason = console.CannotFireReason(index, partial: true);
            if (Widgets.ButtonText(fireRect, "DMSE.Strike.Exec.Fire".Translate(), active: full == null || partialReason == null))
            {
                console.TryFireFromUI(index, Event.current.shift);
            }
            TooltipHandler.TipRegion(fireRect, (full ?? string.Empty) + (full != null ? "\n\n" : string.Empty)
                + "DMSE.Strike.Exec.FireTip".Translate());
        }
    }
}
