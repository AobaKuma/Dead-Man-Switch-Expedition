using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 導彈組裝台的裝配窗口（docs/導彈組裝台與裝配方案_實作計畫.md §7）。
    ///
    /// Refactoring UI 排序：
    ///   主要＝這枚導彈（設計名稱＋四張數值卡）；主要動作＝下單（已下單時換成取消訂單）。
    ///   次要＝左欄部件選擇、右下需求區（材料、工作量、重複次數、存放方式）。
    ///   三級＝詳細數值（捲動）、規則字串、方案存讀、名稱的隨機／候選／鎖定。
    /// 原版慣例（v6）：標題列＋右上「載入／保存」（仿異種人編輯器）；左下「關閉」、右下主按鈕；
    /// 名稱列的隨機／「...」／鎖頭與其音效、提示照抄 GeneCreationDialogBase；
    /// 需求區照帳單設定（Dialog_BillConfig）：「需要」「工作量」、IntEntry 的重複次數、存放方式下拉；
    /// 彈體與部件旁有原版資訊卡按鈕。
    /// 窗口持有編輯副本 <see cref="working"/>；下單才凍結進組裝台，關窗時配置中的草稿寫回組裝台。
    /// </summary>
    public class Dialog_MissileAssemblyBench : Window
    {
        private const float LeftWidth = 400f;
        private const float HeaderHeight = 35f;
        private const float BodyButtonHeight = 40f;
        private const float NameRowHeight = 32f;
        private const float IconButtonSize = 24f;
        private const float PrimaryButtonWidth = 150f;
        private const float MaterialRowHeight = 24f;
        private const float SectionLabelHeight = 18f;

        private readonly CompMissileAssemblyBench comp;

        private MissileConfig working;
        private string designName;
        private bool nameLocked;
        private string ruleString;
        private int count = 1;
        private string countBuffer;
        private BenchOutputMode outputMode;

        private Vector2 leftScroll;
        private float leftHeight;
        private Vector2 detailScroll;
        private float detailHeight;

        // 開窗時估算：能做 Crafting 的殖民者中最快的工作速度（工時預估用）。
        private readonly float bestPawnSpeed;
        // 換彈體時重算：地圖上是否有發射平台接受此物品。
        private MissileBodyDef launcherCheckedFor;
        private bool anyLauncherAccepts;

        public override Vector2 InitialSize
            => new Vector2(Mathf.Min(1000f, UI.screenWidth), Mathf.Min(720f, UI.screenHeight));

        public Dialog_MissileAssemblyBench(CompMissileAssemblyBench comp)
        {
            this.comp = comp;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            draggable = true;
            forcePause = true;
            onlyOneOfTypeAllowed = true;

            working = comp.draft?.Clone();
            designName = comp.designName;
            nameLocked = comp.nameLocked;
            ruleString = comp.ruleString;
            count = comp.OrderActive ? comp.remaining : 1;
            outputMode = comp.outputMode;
            bestPawnSpeed = BestPawnSpeed(comp.parent.Map);
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!comp.OrderActive) { comp.SetDraft(working, designName, nameLocked, ruleString); }
            comp.SetOutputMode(outputMode);
        }

        // ================================================================== 主體

        public override void DoWindowContents(Rect inRect)
        {
            bool active = comp.OrderActive;
            if (active)
            {
                // 訂單進行中：顯示的就是凍結的訂單。
                working = comp.draft;
                designName = comp.designName;
                if (count != comp.remaining) { countBuffer = null; }
                count = comp.remaining;
            }

            Rect header = new Rect(inRect.x, inRect.y, inRect.width, HeaderHeight);
            DrawHeader(header, active);
            Rect footer = new Rect(inRect.x, inRect.yMax - DMSEUI.ButtonHeight, inRect.width, DMSEUI.ButtonHeight);
            float top = header.yMax + DMSEUI.Gap;
            Rect content = new Rect(inRect.x, top, inRect.width, footer.y - DMSEUI.SectionGap - top);
            Rect left = new Rect(content.x, content.y, LeftWidth, content.height);
            Rect right = new Rect(left.xMax + DMSEUI.SectionGap, content.y, content.width - LeftWidth - DMSEUI.SectionGap, content.height);

            if (working?.body == null)
            {
                DrawBodyList(left);
                DrawEmptyState(right);
            }
            else
            {
                DrawConfiguration(left, active);
                DrawProduct(right, active);
            }
            DrawFooter(footer, active);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        /// <summary>標題列：左側標題（Medium），右側「保存方案」「載入方案」（仿異種人編輯器右上的載入按鈕）。</summary>
        private void DrawHeader(Rect rect, bool active)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(rect, comp.parent.LabelCap);
            Text.Font = GameFont.Small;

            Rect load = new Rect(rect.xMax - PrimaryButtonWidth, rect.y, PrimaryButtonWidth, DMSEUI.ButtonHeight);
            if (Widgets.ButtonText(load, "DMSE.Bench.LoadDesign".Translate(), active: !active) && !active)
            {
                OpenDesignList();
            }
            if (working?.body != null)
            {
                Rect save = new Rect(load.x - DMSEUI.Gap / 2f - PrimaryButtonWidth, rect.y, PrimaryButtonWidth, DMSEUI.ButtonHeight);
                if (Widgets.ButtonText(save, "DMSE.Bench.SaveDesign".Translate())) { SaveDesign(); }
            }
        }

        // ================================================================== 空狀態：彈體清單

        private void DrawBodyList(Rect rect)
        {
            List<MissileBodyDef> bodies = MissileItemUtility.ProducibleBodies;
            Rect view = new Rect(0f, 0f, rect.width - DMSEUI.ScrollbarWidth, Mathf.Max(leftHeight, rect.height));
            Widgets.BeginScrollView(rect, ref leftScroll, view);
            float y = 0f;
            foreach (IGrouping<MissileSizeClass, MissileBodyDef> group in bodies.GroupBy(b => b.sizeClass))
            {
                y = SectionLabel(view.width, y, MissileDesignUI.SizeClassLabel(group.Key));
                foreach (MissileBodyDef body in group)
                {
                    Rect row = new Rect(0f, y, view.width, BodyButtonHeight);
                    bool usable = MissileItemUtility.CanProduce(body, out string reason);
                    Widgets.DrawHighlightIfMouseover(row);
                    MissileDesignUI.DrawBodyIcon(new Rect(row.x + 4f, row.y + 4f, 32f, 32f), body);
                    Rect info = new Rect(row.xMax - IconButtonSize, row.y + (row.height - IconButtonSize) / 2f, IconButtonSize, IconButtonSize);
                    ThingDef item = MissileItemUtility.ItemDefFor(body);
                    if (item != null) { Widgets.InfoCardButton(info, item); }
                    GUI.color = usable ? Color.white : DMSEUI.Muted;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Rect labelRect = new Rect(row.x + 44f, row.y, info.x - row.x - 44f - 28f, row.height);
                    Widgets.Label(labelRect, body.LabelCap.ToString().Truncate(labelRect.width));
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    Rect hit = new Rect(row.x, row.y, info.x - row.x - DMSEUI.Gap, row.height);
                    if (!usable)
                    {
                        GUI.DrawTexture(new Rect(info.x - 28f, row.y + 10f, 20f, 20f), MissileDesignUI.LockedTex);
                        TooltipHandler.TipRegion(hit, "Locked".Translate() + ": " + reason);
                    }
                    else
                    {
                        TooltipHandler.TipRegion(hit, body.description);
                        if (Widgets.ButtonInvisible(hit)) { ChangeBody(body); }
                    }
                    y += BodyButtonHeight;
                }
            }
            leftHeight = y;
            Widgets.EndScrollView();
        }

        private void DrawEmptyState(Rect rect)
        {
            Rect line = new Rect(rect.x, rect.center.y - 30f, rect.width, Text.LineHeight);
            GUI.color = DMSEUI.Muted;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(line, "DMSE.Bench.EmptyHint".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        // ================================================================== 左欄：構型

        private void DrawConfiguration(Rect rect, bool active)
        {
            // 彈體按鈕＋原版資訊卡按鈕
            Rect bodyRect = new Rect(rect.x, rect.y, rect.width, BodyButtonHeight);
            Widgets.DrawMenuSection(bodyRect);
            Rect info = new Rect(bodyRect.xMax - IconButtonSize - DMSEUI.Gap, bodyRect.y + (bodyRect.height - IconButtonSize) / 2f, IconButtonSize, IconButtonSize);
            Rect bodyHit = new Rect(bodyRect.x, bodyRect.y, info.x - bodyRect.x - DMSEUI.Gap / 2f, bodyRect.height);
            if (!active) { Widgets.DrawHighlightIfMouseover(bodyHit); }
            MissileDesignUI.DrawBodyIcon(new Rect(bodyRect.x + 4f, bodyRect.y + 4f, 32f, 32f), working.body);
            Text.Anchor = TextAnchor.MiddleLeft;
            Rect bodyLabel = new Rect(bodyRect.x + 44f, bodyRect.y, bodyHit.width - 44f - 20f, bodyRect.height);
            Widgets.Label(bodyLabel, working.body.LabelCap.ToString().Truncate(bodyLabel.width));
            if (!active)
            {
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(bodyHit.x, bodyHit.y, bodyHit.width - 4f, bodyHit.height), "▼");
            }
            Text.Anchor = TextAnchor.UpperLeft;
            ThingDef item = MissileItemUtility.ItemDefFor(working.body);
            if (item != null) { Widgets.InfoCardButton(info, item); }
            if (!active && Widgets.ButtonInvisible(bodyHit)) { OpenBodyMenu(); }

            float top = bodyRect.yMax + DMSEUI.Gap;
            if (active)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = DMSEUI.Muted;
                Rect lockedNote = new Rect(rect.x, top, rect.width, SectionLabelHeight);
                Widgets.Label(lockedNote, "DMSE.Bench.LockedWhileOrdered".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                top = lockedNote.yMax + DMSEUI.Gap;
            }

            Rect scroll = new Rect(rect.x, top, rect.width, rect.yMax - top);
            Rect view = new Rect(0f, 0f, scroll.width - DMSEUI.ScrollbarWidth, Mathf.Max(leftHeight, scroll.height));
            Widgets.BeginScrollView(scroll, ref leftScroll, view);
            float y = 0f;
            MissileConfig defaults = MissileConfig.NewWithDefaults(working.body);
            foreach (MissilePartCategory cat in working.body.slots)
            {
                y = SectionLabel(view.width, y, ("DMSE.Missile.Cat." + cat).Translate());
                foreach (MissilePartDef part in DefDatabase<MissilePartDef>.AllDefsListForReading)
                {
                    if (part.category != cat || !part.CompatibleWith(working.body)) { continue; }
                    y = PartRow(view.width, y, cat, part, defaults, active);
                }
                y = PartRow(view.width, y, cat, null, defaults, active);
            }
            leftHeight = y;
            Widgets.EndScrollView();
        }

        private float PartRow(float width, float y, MissilePartCategory cat, MissilePartDef part, MissileConfig defaults, bool active)
        {
            Rect row = new Rect(0f, y, width, DMSEUI.RowHeight);
            bool chosen = working.PartFor(cat) == part;
            bool usable = MissileItemUtility.PartUsable(part, out string reason);
            // 右端留給原版資訊卡按鈕；其餘是可點的選擇區。
            Rect info = new Rect(row.xMax - IconButtonSize, row.y + (row.height - IconButtonSize) / 2f, IconButtonSize, IconButtonSize);
            Rect hit = new Rect(row.x, row.y, info.x - row.x - DMSEUI.Gap / 2f, row.height);
            if (chosen) { Widgets.DrawHighlightSelected(row); }
            else if (!active && usable) { Widgets.DrawHighlightIfMouseover(hit); }

            Widgets.RadioButton(row.x, row.y + (row.height - Widgets.RadioButtonSize) / 2f, chosen, disabled: active || !usable);
            float x = row.x + Widgets.RadioButtonSize + DMSEUI.Gap;

            string label = part != null ? part.LabelCap.ToString() : "DMSE.Missile.None".Translate().ToString();
            string cost = part != null ? MissileDesignUI.CostText(part.costList) : string.Empty;
            bool isDefault = part != null && defaults.parts.Contains(part);
            string defaultTag = "Default".Translate();

            Text.Font = GameFont.Tiny;
            float costWidth = Mathf.Min(130f, Text.CalcSize(cost).x);
            float tagWidth = isDefault ? Text.CalcSize(defaultTag).x + DMSEUI.Gap : 0f;
            Text.Font = GameFont.Small;

            Rect labelRect = new Rect(x, row.y, hit.xMax - x - costWidth - tagWidth - DMSEUI.Gap, row.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = usable ? Color.white : DMSEUI.Muted;
            Widgets.Label(labelRect, label.Truncate(labelRect.width));

            Text.Font = GameFont.Tiny;
            GUI.color = DMSEUI.Muted;
            if (isDefault)
            {
                Widgets.Label(new Rect(labelRect.xMax, row.y, tagWidth, row.height), defaultTag);
            }
            Text.Anchor = TextAnchor.MiddleRight;
            if (usable)
            {
                Widgets.Label(new Rect(hit.xMax - costWidth, row.y, costWidth, row.height), cost.Truncate(costWidth));
            }
            else
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(hit.xMax - 20f, row.y + 4f, 20f, 20f), MissileDesignUI.LockedTex);
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            if (part != null) { Widgets.InfoCardButton(info, part); }
            TooltipHandler.TipRegion(hit, () => PartTooltip(cat, part, reason), (cat, part).GetHashCode());
            if (!active && usable && !chosen && Widgets.ButtonInvisible(hit))
            {
                working.SetPart(cat, part);
                OnConfigChanged();
            }
            return y + DMSEUI.RowHeight;
        }

        private string PartTooltip(MissilePartCategory cat, MissilePartDef part, string lockedReason)
        {
            List<string> lines = new List<string>();
            if (part != null) { lines.Add(part.LabelCap.AsTipTitle() + "\n" + part.description); }
            if (lockedReason != null) { lines.Add(("Locked".Translate() + ": " + lockedReason).Colorize(DMSEUI.Error)); }
            if (part != null && part.costList != null && part.costList.Count > 0)
            {
                lines.Add("BillRequires".Translate() + ": " + MissileDesignUI.CostText(part.costList));
            }
            // 換上後的變化
            MissileConfig trial = working.Clone();
            trial.SetPart(cat, part);
            List<string> changes = new List<string>();
            AddChange(changes, "DMSE.UI.RangeChange".Translate(), trial.Range, working.Range, "0", true);
            AddChange(changes, "DMSE.UI.DamageChange".Translate(), trial.DamageAmount, working.DamageAmount, "0", true);
            AddChange(changes, "DMSE.Bench.Stat.Radius".Translate(), trial.ExplosionRadius, working.ExplosionRadius, "0.#", true);
            AddChange(changes, "DMSE.Bench.Stat.Scatter".Translate(), trial.Scatter, working.Scatter, "0.#", false);
            AddChange(changes, "DMSE.Bench.Stat.TerminalUpdates".Translate(), trial.TerminalUpdates, working.TerminalUpdates, "0", true);
            if (changes.Count > 0) { lines.Add("DMSE.Bench.IfEquipped".Translate() + "\n" + string.Join("\n", changes)); }
            return string.Join("\n\n", lines.Where(l => !l.NullOrEmpty()));
        }

        private static void AddChange(List<string> lines, string label, float now, float before, string format, bool higherIsBetter)
        {
            string d = MissileDesignUI.Delta(now, before, format, higherIsBetter, out Color c);
            if (!d.NullOrEmpty()) { lines.Add("  " + label + "  " + d.Colorize(c)); }
        }

        private void OpenBodyMenu()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (MissileBodyDef b in MissileItemUtility.ProducibleBodies)
            {
                MissileBodyDef body = b;
                string label = body.LabelCap + " (" + MissileDesignUI.SizeClassLabel(body.sizeClass) + ")";
                ThingDef item = MissileItemUtility.ItemDefFor(body);
                if (!MissileItemUtility.CanProduce(body, out string reason))
                {
                    opts.Add(new FloatMenuOption(label + " (" + "Locked".Translate() + ": " + reason + ")", null, item));
                    continue;
                }
                opts.Add(new FloatMenuOption(label, () => ChangeBody(body), item));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void ChangeBody(MissileBodyDef body)
        {
            if (working == null)
            {
                working = MissileConfig.NewWithDefaults(body);
            }
            else if (working.body != body)
            {
                working.body = body;
                working.PruneIncompatible();
                foreach (MissilePartDef p in MissileConfig.NewWithDefaults(body).parts)
                {
                    if (working.PartFor(p.category) == null) { working.parts.Add(p); }
                }
            }
            leftScroll = Vector2.zero;
            OnConfigChanged();
        }

        /// <summary>構型改變：名稱未鎖定時只換詞、沿用同一條規則字串。</summary>
        private void OnConfigChanged()
        {
            if (!nameLocked || designName.NullOrEmpty())
            {
                designName = MissileDesignNamer.Generate(working, ruleString, UsedNames(), out ruleString);
            }
        }

        private HashSet<string> UsedNames() => MissileDesignNamer.UsedNames(comp.parent.Map, comp.designName);

        // ================================================================== 右欄：成品

        private void DrawProduct(Rect rect, bool active)
        {
            // 1. 名稱列
            Rect nameRow = new Rect(rect.x, rect.y, rect.width, NameRowHeight);
            DrawNameRow(nameRow, active);
            Text.Font = GameFont.Tiny;
            GUI.color = DMSEUI.Muted;
            Rect ruleRect = new Rect(rect.x, nameRow.yMax + 2f, rect.width, SectionLabelHeight);
            string sub = working.body.LabelCap + " · " + (ruleString ?? string.Empty);
            Widgets.Label(ruleRect, sub.Truncate(ruleRect.width));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 2. 數值卡
            Rect cards = new Rect(rect.x, ruleRect.yMax + DMSEUI.Gap, rect.width, MissileDesignUI.StatCardHeight);
            MissileDesignUI.DrawStatCards(cards, working, MissileConfig.NewWithDefaults(working.body));

            // 4. 需求區（固定貼底）
            float orderHeight = OrderHeight(active);
            Rect order = new Rect(rect.x, rect.yMax - orderHeight, rect.width, orderHeight);
            DrawOrder(order, active);

            // 3. 詳細數值（中間捲動）
            Rect details = new Rect(rect.x, cards.yMax + DMSEUI.SectionGap, rect.width, order.y - DMSEUI.SectionGap - cards.yMax - DMSEUI.SectionGap);
            if (details.height > 40f) { DrawDetails(details); }
        }

        /// <summary>名稱列：照 GeneCreationDialogBase 的隨機／「...」／鎖頭（含音效、焦點與提示）。</summary>
        private void DrawNameRow(Rect row, bool active)
        {
            float buttonsWidth = active ? 0f : 90f + 4f + 30f + 10f + IconButtonSize;
            Rect field = new Rect(row.x, row.y, row.width - buttonsWidth - (active ? 0f : DMSEUI.Gap), row.height);
            Text.Font = GameFont.Medium;
            if (active)
            {
                Widgets.Label(field, (designName ?? string.Empty).Truncate(field.width));
            }
            else
            {
                string before = designName ?? string.Empty;
                designName = Widgets.TextField(field, before, MissileDesignNamer.MaxNameLength, MissileDesignNamer.ValidSymbolRegex);
                if (designName != before)
                {
                    // 同原版：打字加長就自動鎖定，清空則解鎖。
                    if (designName.Length > before.Length && designName.Length > 3) { nameLocked = true; }
                    else if (designName.Length == 0) { nameLocked = false; }
                }
            }
            Text.Font = GameFont.Small;
            if (active) { return; }

            float y = row.y + (row.height - IconButtonSize) / 2f;
            Rect random = new Rect(field.xMax + DMSEUI.Gap, y, 90f, IconButtonSize);
            if (Widgets.ButtonText(random, "Randomize".Translate()))
            {
                GUI.FocusControl(null);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                nameLocked = false;
                designName = MissileDesignNamer.Generate(working, null, UsedNames(), out ruleString);
            }
            Rect more = new Rect(random.xMax + 4f, y, 30f, IconButtonSize);
            if (Widgets.ButtonText(more, "..."))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (KeyValuePair<string, string> kv in MissileDesignNamer.Candidates(working, UsedNames()))
                {
                    string n = kv.Key, r = kv.Value;
                    opts.Add(new FloatMenuOption(n, () => { designName = n; ruleString = r; nameLocked = false; }));
                }
                if (opts.Count > 0) { Find.WindowStack.Add(new FloatMenu(opts)); }
            }
            TooltipHandler.TipRegion(more, "DMSE.Bench.NameCandidates".Translate());
            Rect lockRect = new Rect(more.xMax + 10f, y, IconButtonSize, IconButtonSize);
            if (Widgets.ButtonImage(lockRect, nameLocked ? MissileDesignUI.LockedTex : MissileDesignUI.UnlockedTex))
            {
                nameLocked = !nameLocked;
                (nameLocked ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
            }
            if (Mouse.IsOver(lockRect))
            {
                TooltipHandler.TipRegion(lockRect, "DMSE.Bench.LockNameButtonDesc".Translate() + "\n\n"
                    + (nameLocked ? "DMSE.Bench.LockNameOn" : "DMSE.Bench.LockNameOff").Translate());
            }
        }

        private void DrawDetails(Rect rect)
        {
            MissileConfig baseline = MissileConfig.NewWithDefaults(working.body);
            Rect view = new Rect(0f, 0f, rect.width - DMSEUI.ScrollbarWidth, Mathf.Max(detailHeight, rect.height));
            Widgets.BeginScrollView(rect, ref detailScroll, view);
            float y = 0f;

            y = SectionLabel(view.width, y, "DMSE.Bench.Group.Strike".Translate());
            y = StatRow(view.width, y, "DMSE.Bench.Stat.Scatter".Translate(), working.Scatter, baseline.Scatter, "0.#", false);
            y = TextRow(view.width, y, "DMSE.Bench.Stat.DamageType".Translate(),
                working.DamageDef?.LabelCap.ToString() ?? "-", working.DamageDef != baseline.DamageDef);
            y = TextRow(view.width, y, "DMSE.Bench.Stat.ThickRoof".Translate(),
                (working.PenetratesThickRoof ? "Yes" : "No").Translate(), working.PenetratesThickRoof != baseline.PenetratesThickRoof);
            y = StatRow(view.width, y, "DMSE.Bench.Stat.Speed".Translate(), working.WorldSpeedFactor, baseline.WorldSpeedFactor, "0.##", true);

            y = SectionLabel(view.width, y, "DMSE.Bench.Group.Guidance".Translate());
            y = StatRow(view.width, y, "DMSE.Bench.Stat.TerminalUpdates".Translate(), working.TerminalUpdates, baseline.TerminalUpdates, "0", true);
            y = StatRow(view.width, y, "DMSE.Bench.Stat.Channels".Translate(), working.StrikeChannelCost, baseline.StrikeChannelCost, "0", false);
            if (working.body.sizeClass == MissileSizeClass.Loitering)
            {
                y = TextRow(view.width, y, "DMSE.Bench.Stat.Loiter".Translate(),
                    working.LoiterTicks > 0 ? working.LoiterTicks.ToStringTicksToPeriod() : "-", working.LoiterTicks != baseline.LoiterTicks);
            }

            bool survival = working.ChaffCharges > 0 || baseline.ChaffCharges > 0 || !Mathf.Approximately(working.SignatureFactor, 1f)
                            || !Mathf.Approximately(baseline.SignatureFactor, 1f);
            if (survival)
            {
                y = SectionLabel(view.width, y, "DMSE.Bench.Group.Survival".Translate());
                y = StatRow(view.width, y, "DMSE.Bench.Stat.Signature".Translate(), working.SignatureFactor, baseline.SignatureFactor, "0.00", null);
                if (working.ChaffCharges > 0 || baseline.ChaffCharges > 0)
                {
                    y = StatRow(view.width, y, "DMSE.Bench.Stat.Chaff".Translate(), working.ChaffCharges, baseline.ChaffCharges, "0", true);
                    y = StatRow(view.width, y, "DMSE.Bench.Stat.Jam".Translate(), working.JamStrength, baseline.JamStrength, "0.##", true);
                }
            }
            detailHeight = y;
            Widgets.EndScrollView();
        }

        private float StatRow(float width, float y, string label, float now, float baseline, string format, bool? higherIsBetter)
        {
            string delta = MissileDesignUI.Delta(now, baseline, format, higherIsBetter, out Color c);
            string value = now.ToString(format);
            return ValueRow(width, y, label, value, delta, c, !delta.NullOrEmpty());
        }

        private float TextRow(float width, float y, string label, string value, bool changed)
            => ValueRow(width, y, label, value, null, DMSEUI.Muted, changed);

        private static float ValueRow(float width, float y, string label, string value, string delta, Color deltaColor, bool changed)
        {
            Rect row = new Rect(0f, y, width, 22f);
            GUI.color = DMSEUI.Muted;
            Widgets.Label(new Rect(row.x, row.y, row.width * 0.55f, row.height), label);
            Text.Anchor = TextAnchor.UpperRight;
            float deltaWidth = delta.NullOrEmpty() ? 0f : 64f;
            GUI.color = changed ? Color.white : DMSEUI.Muted;
            Widgets.Label(new Rect(row.x, row.y, row.width - deltaWidth, row.height), value);
            if (deltaWidth > 0f)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = deltaColor;
                Widgets.Label(new Rect(row.xMax - deltaWidth, row.y + 2f, deltaWidth, row.height), delta);
                Text.Font = GameFont.Small;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            return y + row.height;
        }

        // ---------------- 需求區（照帳單設定：需要／工作量／重複／存放方式） ----------------

        private Dictionary<ThingDef, int> MaterialsPerUnit(bool active)
            => active ? comp.RequiredPerUnit() : MissileAssemblyCost.RequiredFor(working);

        private float OrderHeight(bool active)
        {
            int materials = Mathf.Max(1, MaterialsPerUnit(active).Count);
            return DMSEUI.Gap * 2f + SectionLabelHeight + (active ? MaterialRowHeight : 0f) + materials * MaterialRowHeight
                   + DMSEUI.Gap + 22f + DMSEUI.Gap + DMSEUI.RowHeight + DMSEUI.Gap / 2f + DMSEUI.RowHeight;
        }

        private void DrawOrder(Rect rect, bool active)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(DMSEUI.Gap);
            float y = inner.y;
            y = SectionLabelAt(inner.x, y, inner.width, "BillRequires".Translate());

            if (active)
            {
                int total = comp.producedThisOrder + comp.remaining;
                string state = !comp.ResourcesComplete ? "DMSE.Bench.State.Collecting".Translate()
                    : comp.OutputBlocked ? "DMSE.Bench.State.OutputBlocked".Translate()
                    : comp.WorkDone > 0f ? "DMSE.Bench.State.Working".Translate()
                    : "DMSE.Bench.State.Ready".Translate();
                Rect stateRow = new Rect(inner.x, y, inner.width, MaterialRowHeight);
                GUI.color = comp.OutputBlocked && comp.ResourcesComplete ? DMSEUI.Warning : Color.white;
                Widgets.Label(stateRow, "DMSE.Bench.Progress".Translate(comp.producedThisOrder + 1, total, state));
                GUI.color = Color.white;
                y = stateRow.yMax;
            }

            Map map = comp.parent.Map;
            Dictionary<ThingDef, int> per = MaterialsPerUnit(active);
            if (per.Count == 0)
            {
                GUI.color = DMSEUI.Muted;
                Widgets.Label(new Rect(inner.x, y, inner.width, MaterialRowHeight), "DMSE.Bench.NoMaterials".Translate());
                GUI.color = Color.white;
                y += MaterialRowHeight;
            }
            foreach (KeyValuePair<ThingDef, int> kv in per.OrderByDescending(kv => kv.Value))
            {
                Rect row = new Rect(inner.x, y, inner.width, MaterialRowHeight);
                Widgets.ThingIcon(new Rect(row.x, row.y + 2f, 20f, 20f), kv.Key);
                Text.Anchor = TextAnchor.MiddleLeft;
                Rect labelRect = new Rect(row.x + 26f, row.y, row.width * 0.42f, row.height);
                if (active)
                {
                    comp.Delivered.TryGetValue(kv.Key, out int got);
                    got = Mathf.Min(got, kv.Value);
                    Widgets.Label(labelRect, kv.Key.LabelCap.ToString().Truncate(labelRect.width));
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(row, got + " / " + kv.Value);
                    Widgets.FillableBar(new Rect(row.x + row.width * 0.45f, row.center.y - 3f, row.width * 0.3f, 6f), (float)got / Mathf.Max(1, kv.Value));
                }
                else
                {
                    int need = kv.Value * count;
                    int have = MissileAssemblyCost.CountOnMap(map, kv.Key);
                    bool shortOf = have < need;
                    GUI.color = shortOf ? DMSEUI.Error : Color.white;
                    Widgets.Label(labelRect, kv.Key.LabelCap.ToString().Truncate(labelRect.width));
                    Text.Anchor = TextAnchor.MiddleRight;
                    string amount = need + " / " + have;
                    if (shortOf) { amount += "  " + "DMSE.Bench.Short".Translate(need - have); }
                    Widgets.Label(row, amount);
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(row, "DMSE.Bench.MaterialTip".Translate(kv.Value, need, have));
                }
                Text.Anchor = TextAnchor.UpperLeft;
                y += MaterialRowHeight;
            }
            y += DMSEUI.Gap;

            // 工作量（原版「工作量」＋ToStringWorkAmount）＋工作速度係數；完整算式與預估時數在提示裡。
            Rect workRow = new Rect(inner.x, y, inner.width, 22f);
            float tableSpeed = comp.TableSpeed;
            string speedText = StatDefOf.WorkTableWorkSpeedFactor.LabelCap + " " + tableSpeed.ToStringPercent();
            if (active)
            {
                float progress = comp.WorkDone / Mathf.Max(1f, comp.WorkAmount);
                Widgets.Label(workRow.LeftPart(0.45f), "WorkAmount".Translate() + ": " + progress.ToStringPercent());
                Widgets.FillableBar(new Rect(workRow.x + workRow.width * 0.45f, workRow.center.y - 3f, workRow.width * 0.55f, 6f), progress);
            }
            else
            {
                float work = MissileAssemblyCost.WorkFor(working) * count;
                string text = "WorkAmount".Translate() + ": " + work.ToStringWorkAmount() + " · " + speedText;
                if (tableSpeed < 0.999f)
                {
                    GUI.color = DMSEUI.Warning;
                    text = "⚠ " + text;
                }
                Widgets.Label(workRow, text.Truncate(workRow.width));
                GUI.color = Color.white;
            }
            TooltipHandler.TipRegion(workRow, () => WorkTooltip(), 0x0D5E0002);
            y = workRow.yMax + DMSEUI.Gap;

            // 重複次數（原版 IntEntry：-10 -1 [n] +1 +10）
            Rect repeatRow = new Rect(inner.x, y, inner.width, DMSEUI.RowHeight);
            Text.Anchor = TextAnchor.MiddleLeft;
            Rect repeatLabel = new Rect(repeatRow.x, repeatRow.y, 90f, repeatRow.height);
            Widgets.Label(repeatLabel, "RepeatCount".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            int value = count;
            Widgets.IntEntry(new Rect(repeatLabel.xMax, repeatRow.y, repeatRow.width - repeatLabel.width, repeatRow.height), ref value, ref countBuffer);
            int clamped = Mathf.Clamp(value, 1, comp.Props.maxBatch);
            if (clamped != value) { countBuffer = clamped.ToString(); }
            if (clamped != count)
            {
                count = clamped;
                if (active) { comp.SetRemaining(clamped); }
            }
            y = repeatRow.yMax + DMSEUI.Gap / 2f;

            // 存放方式（照帳單設定的下拉按鈕）
            Rect storeRow = new Rect(inner.x, y, inner.width, DMSEUI.RowHeight);
            if (Widgets.ButtonText(storeRow, OutputModeLabel(outputMode)))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (BenchOutputMode mode in new[] { BenchOutputMode.KeepOnBench, BenchOutputMode.SendToStorage })
                {
                    BenchOutputMode m = mode;
                    opts.Add(new FloatMenuOption(OutputModeLabel(m), () =>
                    {
                        outputMode = m;
                        comp.SetOutputMode(m);
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(storeRow, (outputMode == BenchOutputMode.KeepOnBench
                ? "DMSE.Bench.Output.KeepOnBenchDesc" : "DMSE.Bench.Output.SendToStorageDesc").Translate());
        }

        /// <summary>留在台上（本模組）／原版「拿到最佳的儲存區」（BillStoreModeDefOf.BestStockpile）。</summary>
        private static string OutputModeLabel(BenchOutputMode mode)
            => mode == BenchOutputMode.KeepOnBench
                ? "DMSE.Bench.Output.KeepOnBench".Translate().ToString()
                : BillStoreModeDefOf.BestStockpile.LabelCap.ToString();

        private string WorkTooltip()
        {
            StatDef stat = StatDefOf.WorkTableWorkSpeedFactor;
            StatRequest req = StatRequest.For(comp.parent);
            float hours = MissileAssemblyCost.WorkFor(working) * count / Mathf.Max(0.01f, bestPawnSpeed * comp.TableSpeed) / GenDate.TicksPerHour;
            return "DMSE.Bench.WorkTimeTip".Translate(hours.ToString("0.#"), bestPawnSpeed.ToStringPercent())
                   + "\n\n" + stat.LabelCap.AsTipTitle() + "\n" + stat.Worker.GetExplanationFull(req, stat.toStringNumberSense, comp.TableSpeed);
        }

        // ================================================================== 底列（左「關閉」、右主按鈕，同原版）

        private void DrawFooter(Rect rect, bool active)
        {
            Rect close = new Rect(rect.x, rect.y, PrimaryButtonWidth, rect.height);
            if (Widgets.ButtonText(close, "Close".Translate())) { Close(); }
            if (working?.body == null) { return; }

            Rect primary = new Rect(rect.xMax - PrimaryButtonWidth, rect.y, PrimaryButtonWidth, rect.height);
            Rect status = new Rect(close.xMax + DMSEUI.Gap, rect.y, primary.x - DMSEUI.Gap - close.xMax - DMSEUI.Gap, rect.height);

            if (active)
            {
                DrawStatus(status, StatusForActive(out Color c1), c1);
                if (Widgets.ButtonText(primary, "DMSE.Bench.CancelOrder".Translate()))
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DMSE.Bench.CancelOrderConfirm".Translate(), comp.CancelOrder));
                }
                return;
            }

            string blocking = Blocking();
            bool warn = false;
            string statusText = blocking != null ? "✕ " + blocking : Warning(out warn);
            Color statusColor = blocking != null ? DMSEUI.Error : warn ? DMSEUI.Warning : DMSEUI.Muted;
            if (MissileAssemblyUtility.GodModeInstant) { statusText = "DEV · " + statusText; }
            DrawStatus(status, statusText, statusColor);

            string primaryLabel = (MissileAssemblyUtility.GodModeInstant ? "DMSE.Bench.InstantComplete" : "DMSE.Bench.PlaceOrder").Translate();
            if (Widgets.ButtonText(primary, primaryLabel, active: blocking == null)) { PlaceOrder(); }
        }

        private static void DrawStatus(Rect rect, string text, Color color)
        {
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = color;
            Widgets.Label(rect, text.Truncate(rect.width));
            TooltipHandler.TipRegion(rect, text);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        /// <summary>第一個阻擋下單的原因；null＝可下單。窗口狀態列與下單防呆共用。</summary>
        private string Blocking()
        {
            if (working?.body == null) { return "DMSE.Bench.Block.NoBody".Translate(); }
            if (!MissileItemUtility.CanProduce(working.body, out string reason)) { return reason; }
            foreach (MissilePartDef p in working.parts)
            {
                if (!p.CompatibleWith(working.body)) { return "DMSE.Blueprint.Incompatible".Translate(p.LabelCap); }
                if (!MissileItemUtility.PartUsable(p, out string partReason)) { return p.LabelCap + ": " + partReason; }
            }
            if (!designName.NullOrEmpty() && !MissileDesignNamer.IsValidName(designName)) { return "DMSE.Bench.Block.BadName".Translate(); }
            return null;
        }

        /// <summary>不阻擋下單的提示：材料不足、出廠暫存已滿、沒有發射平台接受此彈；都沒有時顯示預估。</summary>
        private string Warning(out bool warn)
        {
            warn = true;
            Map map = comp.parent.Map;
            foreach (KeyValuePair<ThingDef, int> kv in MissileAssemblyCost.RequiredFor(working))
            {
                int have = MissileAssemblyCost.CountOnMap(map, kv.Key);
                if (have < kv.Value * count)
                {
                    return "⚠ " + "DMSE.Bench.Warn.Short".Translate(kv.Key.LabelCap, kv.Value * count - have);
                }
            }
            if (comp.OutputBlocked) { return "⚠ " + "DMSE.Bench.OutputFull".Translate(); }
            if (!AnyLauncherAccepts()) { return "⚠ " + "DMSE.Bench.Warn.NoLauncher".Translate(); }
            warn = false;
            float hours = MissileAssemblyCost.WorkFor(working) * count / Mathf.Max(0.01f, bestPawnSpeed * comp.TableSpeed) / GenDate.TicksPerHour;
            return "DMSE.Bench.Summary".Translate(count, hours.ToString("0.#"));
        }

        private string StatusForActive(out Color color)
        {
            color = DMSEUI.Muted;
            if (!comp.ResourcesComplete)
            {
                Dictionary<ThingDef, int> need = comp.StillNeeded();
                return "DMSE.Missile.NeedResources".Translate(string.Join(", ", need.Select(kv => kv.Key.label + " x" + kv.Value)));
            }
            if (comp.OutputBlocked)
            {
                color = DMSEUI.Warning;
                return "⚠ " + "DMSE.Bench.OutputFull".Translate();
            }
            return "DMSE.Bench.State.Ready".Translate();
        }

        private bool AnyLauncherAccepts()
        {
            if (launcherCheckedFor == working.body) { return anyLauncherAccepts; }
            launcherCheckedFor = working.body;
            anyLauncherAccepts = false;
            ThingDef item = MissileItemUtility.ItemDefFor(working.body);
            Map map = comp.parent.Map;
            if (item == null || map == null) { return false; }
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (!WorkGiver_LoadMissileLauncher.IsLauncher(b)) { continue; }
                IMissileRack rack = MissileRackUtil.Of(b);
                if (rack?.GetParentStoreSettings()?.filter?.Allows(item) == true)
                {
                    anyLauncherAccepts = true;
                    break;
                }
            }
            return anyLauncherAccepts;
        }

        // ================================================================== 動作

        private void PlaceOrder()
        {
            if (Blocking() != null) { return; }
            if (designName.NullOrEmpty()) { designName = MissileDesignNamer.Generate(working, ruleString, UsedNames(), out ruleString); }
            comp.PlaceOrder(working, designName, nameLocked, ruleString, count, outputMode);
        }

        private void SaveDesign()
        {
            if (designName.NullOrEmpty() || !MissileDesignNamer.IsValidName(designName))
            {
                designName = MissileDesignNamer.Generate(working, ruleString, UsedNames(), out ruleString);
            }
            MissileDesignBlueprint bp = MissileDesignBlueprint.From(working, designName, nameLocked, ruleString);
            void DoSave()
            {
                if (MissileDesignBlueprintDatabase.Save(bp, out string error))
                {
                    Messages.Message("DMSE.Blueprint.Saved".Translate(bp.name), MessageTypeDefOf.TaskCompletion, historical: false);
                }
                else
                {
                    Messages.Message("DMSE.Blueprint.SaveFailed".Translate(error), MessageTypeDefOf.RejectInput, historical: false);
                }
            }
            if (MissileDesignBlueprintDatabase.FindByName(bp.name) != null)
            {
                // 同原版存檔覆蓋：按鈕用「覆蓋」／「取消」。
                Find.WindowStack.Add(new Dialog_MessageBox("DMSE.Blueprint.OverwriteConfirm".Translate(bp.name),
                    "OverwriteButton".Translate(), DoSave, "Cancel".Translate(), null, null, true));
            }
            else
            {
                DoSave();
            }
        }

        private void OpenDesignList()
        {
            Find.WindowStack.Add(new Dialog_MissileDesignList(bp =>
            {
                MissileConfig cfg = bp.ToConfig();
                if (cfg == null) { return; }
                working = cfg;
                designName = bp.name;
                nameLocked = bp.nameLocked;
                ruleString = bp.ruleString;
                leftScroll = Vector2.zero;
                if (!nameLocked) { OnConfigChanged(); }
            }));
        }

        // ================================================================== 小工具

        private static float SectionLabel(float width, float y, string label)
        {
            if (y > 0f) { y += DMSEUI.SectionGap - DMSEUI.Gap; }
            return SectionLabelAt(0f, y, width, label);
        }

        private static float SectionLabelAt(float x, float y, float width, string label)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = DMSEUI.Muted;
            Widgets.Label(new Rect(x, y, width, SectionLabelHeight), label);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            return y + SectionLabelHeight + DMSEUI.Gap / 2f;
        }

        private static float BestPawnSpeed(Map map)
        {
            float best = 0f;
            if (map != null)
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p.WorkTypeIsDisabled(WorkTypeDefOf.Crafting)) { continue; }
                    best = Mathf.Max(best, MissileAssemblyUtility.PawnWorkSpeed(p));
                }
            }
            return best > 0f ? best : 1f;
        }
    }
}
