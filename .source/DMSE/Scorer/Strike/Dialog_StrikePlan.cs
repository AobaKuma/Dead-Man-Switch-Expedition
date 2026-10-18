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
    ///   - 規劃模式（草稿，暫停遊戲）：左側候選導彈依彈種分組，右側波次；點一組加一枚到選取中的波次，Shift 加整組。
    ///   - 執行模式（指揮台上的任務，不暫停）：逐波顯示狀態並發射；Shift 只發射現在辦得到的部分。
    /// </summary>
    public class Dialog_StrikePlan : Window
    {
        private const float RowHeight = 28f;
        private const float ButtonHeight = 34f;
        private static readonly Color WarnColor = new Color(1f, 0.85f, 0.35f);

        private readonly CompStrikeConsole console;
        private readonly StrikeMission mission;
        private List<StrikeCandidate> candidates = new List<StrikeCandidate>();
        private Dictionary<Thing, StrikeCandidate> byRound = new Dictionary<Thing, StrikeCandidate>();
        private int reservedElsewhere;
        private int selectedWave;
        private bool showUnavailable;
        private Vector2 leftScroll, rightScroll, execScroll;
        private float leftHeight, rightHeight, execHeight;

        private bool Planning => mission.state == StrikeMissionState.Planning;

        public override Vector2 InitialSize => Planning ? new Vector2(980f, 680f) : new Vector2(640f, 540f);

        public Dialog_StrikePlan(CompStrikeConsole console, StrikeMission mission)
        {
            this.console = console;
            this.mission = mission;
            bool planning = mission.state == StrikeMissionState.Planning;
            forcePause = planning;
            absorbInputAroundWindow = planning;
            doCloseX = true;
            closeOnClickedOutside = false;
            draggable = !planning;
            if (planning) { RefreshCandidates(); }
        }

        private void RefreshCandidates()
        {
            candidates = StrikeRoundUtility.Candidates(console.parent.Map, mission.CurrentTargetTile, -1, out reservedElsewhere);
            byRound = candidates.Where(c => c.round != null).ToDictionary(c => c.round);
            // 已不能用的導彈從草稿移除
            foreach (StrikeWave wave in mission.waves)
            {
                wave.slots.RemoveAll(s => !byRound.TryGetValue(s.round, out StrikeCandidate c) || !c.Assignable);
            }
        }

        private float Distance => MissileWorldUtility.RangeDistance(console.parent.Map.Tile, mission.CurrentTargetTile);

        private bool SiteWarning => mission.targetParent != null && !mission.targetParent.Destroyed
            && !mission.targetParent.HasMap && mission.targetParent.Faction != Faction.OfPlayer;

        public override void DoWindowContents(Rect inRect)
        {
            if (console.parent.DestroyedOrNull() || !console.parent.Spawned || (!Planning && console.mission != mission))
            {
                Close();
                return;
            }
            if (Planning) { DoPlanning(inRect); }
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
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), title);
            Text.Font = GameFont.Small;
            float y = inRect.y + 36f;
            return Line(inRect, y, "DMSE.Strike.Plan.Target".Translate(mission.TargetLabel, Distance.ToString("0.#")));
        }

        // ==================================================================
        //  規劃模式
        // ==================================================================

        private void DoPlanning(Rect inRect)
        {
            int cap = Mathf.Max(1, console.StrikeCapacity);
            float y = Header(inRect, "DMSE.Strike.Plan.Title".Translate());
            y = Line(inRect, y, "DMSE.Strike.Plan.Channels".Translate(console.StrikeCapacity, console.StrikeFree,
                BVRWorldTuningDef.Current.strikeGuidanceTicks.ToStringTicksToPeriod()));
            if (console.Props.requiresComputation)
            {
                float work = console.WorkFor(mission);
                float speed = StrikeComputation.FastestResearchSpeed(console.parent.Map);
                string time = speed > 0f ? Mathf.CeilToInt(work / speed).ToStringTicksToPeriod()
                    : "DMSE.Strike.Plan.NoResearcher".Translate().ToString();
                y = Line(inRect, y, "DMSE.Strike.Plan.ComputeWork".Translate(Mathf.RoundToInt(work), time));
            }
            if (SiteWarning) { y = Line(inRect, y, "DMSE.Strike.Plan.SiteWarning".Translate(), WarnColor); }

            Rect body = new Rect(inRect.x, y + 4f, inRect.width, inRect.yMax - ButtonHeight - 12f - y);
            DrawCandidates(body.LeftPart(0.48f), cap);
            DrawWaves(body.RightPart(0.5f), cap);
            DrawPlanningButtons(new Rect(inRect.x, inRect.yMax - ButtonHeight, inRect.width, ButtonHeight), cap);
        }

        private HashSet<Thing> Assigned() => new HashSet<Thing>(mission.AllSlots.Select(s => s.round));

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
                Widgets.DrawHighlightIfMouseover(row);
                DrawCandidateRow(row, list);
                if (Widgets.ButtonInvisible(row))
                {
                    AddToSelectedWave(list, Event.current.shift ? int.MaxValue : 1, cap);
                }
                cy += RowHeight;
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
            Rect labelRect = new Rect(row.x + 4f, row.y, w * 0.55f - 4f, row.height);
            Rect flightRect = new Rect(row.x + w * 0.55f, row.y, w * 0.15f, row.height);
            Rect marginRect = new Rect(row.x + w * 0.70f, row.y, w * 0.15f, row.height);
            Rect valueRect = new Rect(row.x + w * 0.85f, row.y, w * 0.15f - 4f, row.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = first.groupLabel + " ×" + list.Count;
            if (notReady) { GUI.color = WarnColor; }
            Widgets.Label(labelRect, label.Truncate(labelRect.width));
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

        private void AddToSelectedWave(List<StrikeCandidate> list, int count, int cap)
        {
            if (mission.waves.Count == 0) { mission.waves.Add(new StrikeWave()); }
            selectedWave = Mathf.Clamp(selectedWave, 0, mission.waves.Count - 1);
            StrikeWave wave = mission.waves[selectedWave];
            int room = Mathf.Min(cap - wave.slots.Count, console.Props.maxRoundsPerMission - mission.RoundCount);
            if (room <= 0)
            {
                Messages.Message(wave.slots.Count >= cap
                    ? "DMSE.Strike.WaveOverCap".Translate(selectedWave + 1, wave.slots.Count, cap)
                    : "DMSE.Strike.TooManyRounds".Translate(console.Props.maxRoundsPerMission), MessageTypeDefOf.RejectInput, false);
                return;
            }
            foreach (StrikeCandidate c in list.OrderBy(c => c.issue == StrikeRoundIssue.None ? 0 : 1).Take(Mathf.Min(count, room)))
            {
                wave.slots.Add(new StrikeSlot { launcher = c.launcher.parent, round = c.round });
            }
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }

        private void DrawWaves(Rect rect, int cap)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(6f);
            Rect view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(rightHeight, inner.height));
            Widgets.BeginScrollView(inner, ref rightScroll, view);
            float y = 0f;
            int remove = -1;
            for (int i = 0; i < mission.waves.Count; i++)
            {
                StrikeWave wave = mission.waves[i];
                Rect head = new Rect(0f, y, view.width, 30f);
                if (i == selectedWave) { Widgets.DrawHighlightSelected(head); }
                else { Widgets.DrawHighlightIfMouseover(head); }
                Rect deleteRect = new Rect(head.xMax - 26f, head.y + 3f, 24f, 24f);
                Rect titleRect = new Rect(head.x + 4f, head.y, head.width - 34f, head.height);
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = wave.slots.Count > cap ? ColorLibrary.RedReadable : Color.white;
                string title = "DMSE.Strike.Plan.Wave".Translate(i + 1, wave.slots.Count, cap);
                List<int> flights = wave.slots.Select(s => byRound.TryGetValue(s.round, out StrikeCandidate c) ? c.flightTicks : 0).ToList();
                if (flights.Count > 0)
                {
                    int min = flights.Min(), max = flights.Max();
                    title += "    " + "DMSE.Strike.Plan.Flight".Translate(min == max ? min.ToStringTicksToPeriod()
                        : min.ToStringTicksToPeriod() + "–" + max.ToStringTicksToPeriod());
                }
                Widgets.Label(titleRect, title.Truncate(titleRect.width));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (mission.waves.Count > 1 && Widgets.ButtonImage(deleteRect, TexButton.Delete)) { remove = i; }
                else if (Widgets.ButtonInvisible(titleRect)) { selectedWave = i; }
                y += 32f;

                foreach (IGrouping<string, StrikeSlot> group in wave.slots.GroupBy(s => byRound.TryGetValue(s.round, out StrikeCandidate c) ? c.groupLabel : s.round?.LabelCap.ToString() ?? "?").ToList())
                {
                    Rect row = new Rect(16f, y, view.width - 16f, 24f);
                    Rect minus = new Rect(row.xMax - 26f, row.y, 24f, 24f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(row.x, row.y, row.width - 30f, row.height), (group.Key + " ×" + group.Count()).Truncate(row.width - 30f));
                    Text.Anchor = TextAnchor.UpperLeft;
                    if (Widgets.ButtonText(minus, "−"))
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
                mission.waves.RemoveAt(remove);
                selectedWave = Mathf.Clamp(selectedWave, 0, mission.waves.Count - 1);
            }
            if (mission.waves.Count < console.Props.maxWavesPerMission)
            {
                if (Widgets.ButtonText(new Rect(0f, y, 160f, 28f), "DMSE.Strike.Plan.AddWave".Translate()))
                {
                    mission.waves.Add(new StrikeWave());
                    selectedWave = mission.waves.Count - 1;
                }
                y += 32f;
            }
            rightHeight = y;
            Widgets.EndScrollView();
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

        private void DrawPlanningButtons(Rect rect, int cap)
        {
            Rect auto = new Rect(rect.x, rect.y, 150f, ButtonHeight);
            if (Widgets.ButtonText(auto, "DMSE.Strike.Plan.AutoAssign".Translate())) { AutoAssign(cap); }
            TooltipHandler.TipRegion(auto, "DMSE.Strike.Plan.AutoAssignDesc".Translate());
            if (Widgets.ButtonText(new Rect(auto.xMax + 8f, rect.y, 100f, ButtonHeight), "DMSE.Strike.Plan.Clear".Translate()))
            {
                mission.waves.Clear();
                mission.waves.Add(new StrikeWave());
                selectedWave = 0;
            }

            Rect confirm = new Rect(rect.xMax - 160f, rect.y, 160f, ButtonHeight);
            Rect cancel = new Rect(confirm.x - 128f, rect.y, 120f, ButtonHeight);
            string invalid = console.ValidateDraft(mission);
            if (invalid != null)
            {
                Rect error = new Rect(auto.xMax + 116f, rect.y, cancel.x - auto.xMax - 124f, ButtonHeight);
                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(error, invalid.Truncate(error.width));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }
            if (Widgets.ButtonText(cancel, "CancelButton".Translate())) { Close(); }
            if (Widgets.ButtonText(confirm, "DMSE.Strike.Plan.Confirm".Translate(), active: invalid == null))
            {
                if (console.Commit(mission, out string reason)) { Close(); }
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
            float y = Header(inRect, "DMSE.Strike.Exec.Title".Translate(mission.id));
            y = Line(inRect, y, "DMSE.Strike.Exec.State".Translate(console.StateLabel));
            string channels = "DMSE.Strike.Inspect.Channels".Translate(console.StrikeFree, console.StrikeCapacity);
            int release = console.parent.Map.GetComponent<MapComponent_StrikePlanner>()?.TicksUntilNextRelease(console.parent.Faction) ?? -1;
            if (release > 0) { channels += "  " + "DMSE.Strike.Exec.NextRelease".Translate(release.ToStringTicksToPeriod()); }
            y = Line(inRect, y, channels);

            Rect outRect = new Rect(inRect.x, y + 4f, inRect.width, inRect.yMax - ButtonHeight - 12f - y);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(execHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref execScroll, view);
            float cy = 0f;
            for (int i = 0; i < mission.waves.Count; i++)
            {
                DrawExecutionWave(new Rect(0f, cy, view.width, 54f), i);
                cy += 58f;
            }
            execHeight = cy;
            Widgets.EndScrollView();

            Rect bottom = new Rect(inRect.x, inRect.yMax - ButtonHeight, inRect.width, ButtonHeight);
            if (Widgets.ButtonText(new Rect(bottom.x, bottom.y, 160f, ButtonHeight), "DMSE.Strike.Exec.CancelMission".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "DMSE.Strike.Console.CancelConfirm".Translate(mission.id), () => console.CancelMission(), destructive: true));
            }
            if (Widgets.ButtonText(new Rect(bottom.xMax - 120f, bottom.y, 120f, ButtonHeight), "CloseButton".Translate())) { Close(); }
        }

        private void DrawExecutionWave(Rect row, int index)
        {
            StrikeWave wave = mission.waves[index];
            if (index % 2 == 0) { Widgets.DrawLightHighlight(row); }
            Rect fireRect = new Rect(row.xMax - 124f, row.y + 10f, 120f, ButtonHeight);
            Rect text = new Rect(row.x + 6f, row.y + 2f, fireRect.x - row.x - 12f, row.height - 4f);

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
            Widgets.Label(text, "DMSE.Strike.Exec.Wave".Translate(index + 1, groups).Truncate(text.width) + "\n"
                + string.Join("; ", parts).Truncate(text.width));
            string skips = string.Join("\n", wave.slots.Where(s => s.state == StrikeSlotState.Skipped)
                .Select(s => "- " + s.skipReason).Distinct());
            if (!skips.NullOrEmpty()) { TooltipHandler.TipRegion(text, skips); }

            if (!wave.HasPending) { return; }
            string full = console.CannotFireReason(index, partial: false);
            string partial = console.CannotFireReason(index, partial: true);
            if (Widgets.ButtonText(fireRect, "DMSE.Strike.Exec.Fire".Translate(), active: full == null || partial == null))
            {
                console.TryFireFromUI(index, Event.current.shift);
            }
            TooltipHandler.TipRegion(fireRect, (full ?? string.Empty) + (full != null ? "\n\n" : string.Empty)
                + "DMSE.Strike.Exec.FireTip".Translate());
        }
    }
}
