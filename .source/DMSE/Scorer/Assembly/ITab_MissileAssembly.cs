using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈裝配介面（原版風格）。
    ///
    /// 可掛於兩種 Thing 上：
    /// <list type="bullet">
    ///   <item><description>導彈物品（<see cref="CompMissileConfig"/> 直接掛在物品上）</description></item>
    ///   <item><description>導彈儲存架（<see cref="Building_MissileRack"/>）：顯示容器內第一枚導彈的設定</description></item>
    /// </list>
    ///
    /// 編輯的是 pending（待裝配）設定；實際套用需由小人搬運資源完成
    /// （見 <see cref="WorkGiver_AssembleMissile"/>）。
    /// </summary>
    public class ITab_MissileAssembly : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(440f, 560f);
        private Vector2 scrollPos;
        private float lastHeight;
        private Thing lastThing;
        private bool showStats;

        /// <summary>
        /// 取得當前選取物的 <see cref="CompMissileConfig"/>。
        /// 若選取物為 <see cref="Building_MissileRack"/>，則回傳其第一枚導彈的 Comp。
        /// </summary>
        public CompMissileConfig Comp
        {
            get
            {
                if (SelThing == null) { return null; }
                // 直接情況：導彈物品
                CompMissileConfig direct = SelThing.TryGetComp<CompMissileConfig>();
                if (direct != null) { return direct; }
                // 間接情況：導彈儲存架 → 取容器第一枚導彈的 Comp
                IMissileRack rack = MissileRackUtil.Of(SelThing);
                if (rack != null)
                {
                    IReadOnlyList<Thing> held = rack.HeldThings;
                    if (held != null && held.Count > 0)
                    {
                        return held[0].TryGetComp<CompMissileConfig>();
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// 若選取物為儲存架，回傳「正在編輯第幾枚 / 共幾枚」的提示字串；否則為 null。
        /// </summary>
        private string RackIndexLabel
        {
            get
            {
                IMissileRack rack = MissileRackUtil.Of(SelThing);
                if (rack != null)
                {
                    IReadOnlyList<Thing> held = rack.HeldThings;
                    if (held != null && held.Count > 0)
                    {
                        return "DMSE.Missile.EditingSlot".Translate(held.Count);
                    }
                }
                return null;
            }
        }

        public override bool IsVisible => Comp != null;

        public ITab_MissileAssembly()
        {
            size = WinSize;
            labelKey = "DMSE.Missile.Tab";
        }

        /// <summary>
        /// 此選取物身上的 <see cref="CompProperties_MissileConfig"/>（沒有則為 null）。
        /// </summary>
        private CompProperties_MissileConfig ConfigProps
            => SelThing?.def?.comps?.OfType<CompProperties_MissileConfig>().FirstOrDefault();

        /// <summary>
        /// 此選取物是否可由玩家選擇彈體。
        /// 只有「沒有物品型態、直接在建築上裝配」的平台才開放（<see cref="MissileCustomizeLocation.Launcher"/>，
        /// 目前即彈道導彈發射井）；導彈物品與儲存架的彈體由 def 的 defaultBody 固定。
        /// </summary>
        private bool CanChooseBody
        {
            get
            {
                if (!(SelThing is Building)) { return false; }
                MissileBodyDef anchor = ConfigProps?.defaultBody;
                return anchor != null && anchor.customizeLocation == MissileCustomizeLocation.Launcher;
            }
        }

        /// <summary>
        /// 可選彈體：同分級、同為 Launcher 型，且發射方式相容於平台的預設彈體。
        /// 以 <see cref="CompProperties_MissileConfig.defaultBody"/> 當平台分級錨點，
        /// 所以換過彈體後選單仍穩定。研究門檻（<see cref="MissileBodyDef.researchPrerequisites"/>）在選單上顯示為鎖定。
        /// </summary>
        private IEnumerable<MissileBodyDef> SelectableBodies()
        {
            MissileBodyDef anchor = ConfigProps?.defaultBody;
            if (anchor == null) { yield break; }
            foreach (MissileBodyDef b in DefDatabase<MissileBodyDef>.AllDefsListForReading)
            {
                if (b.customizeLocation != MissileCustomizeLocation.Launcher) { continue; }
                if (b.sizeClass != anchor.sizeClass) { continue; }
                if (!MissileTaxonomy.LaunchModeCompatible(b.launchMode, anchor.launchMode)) { continue; }
                yield return b;
            }
        }

        protected override void FillTab()
        {
            CompMissileConfig comp = Comp;
            if (comp == null) { return; }
            comp.EnsureInit();
            MissileConfig cfg = comp.pending;
            bool reserved = StrikeReservation.IsReserved(comp.parent);

            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(12f);
            // §2.1 / §3.3: configuration scrolls; the decision stays visible.
            if (lastThing != comp.parent)
            {
                lastThing = comp.parent;
                scrollPos = Vector2.zero;
                lastHeight = 0f;
                showStats = false;
            }
            // §2.1: a short resource/status summary stays with the action, even when scrolled.
            string footerStatus = reserved ? "DMSE.Strike.ReservedNoEdit".Translate().ToString()
                : !comp.NeedsAssembly ? "DMSE.UI.ActiveConfig".Translate().ToString()
                : comp.assemblyConfirmed ? "DMSE.Missile.AssemblyQueued".Translate().ToString()
                : "DMSE.UI.PendingConfig".Translate().ToString();
            Rect footer = new Rect(rect.x, rect.yMax - DMSEUI.ButtonHeight, rect.width, DMSEUI.ButtonHeight);
            Rect content = new Rect(rect.x, rect.y, rect.width, rect.height - DMSEUI.ButtonHeight - DMSEUI.SectionGap - Text.LineHeight - DMSEUI.Gap);
            Rect view = new Rect(0f, 0f, content.width - DMSEUI.ScrollbarWidth, Mathf.Max(lastHeight, content.height));
            Widgets.BeginScrollView(content, ref scrollPos, view);
            Listing_Standard list = new Listing_Standard();
            list.Begin(new Rect(0f, 0f, view.width, 99999f));

            Text.Font = GameFont.Medium;
            list.Label("DMSE.Missile.Tab".Translate());
            Text.Font = GameFont.Small;

            GUI.color = comp.NeedsAssembly ? DMSEUI.Warning : DMSEUI.Success;
            list.Label((comp.NeedsAssembly
                ? (comp.assemblyConfirmed ? "DMSE.Missile.AssemblyQueued" : "DMSE.UI.PendingConfig")
                : "DMSE.UI.ActiveConfig").Translate());
            GUI.color = Color.white;

            // 儲存架模式：顯示正在編輯哪枚導彈
            string rackLabel = RackIndexLabel;
            if (rackLabel != null)
            {
                GUI.color = DMSEUI.Muted;
                list.Label(rackLabel);
                GUI.color = Color.white;
            }

            // 上帝模式即時裝配：變更立即套用，不耗資源、不需研究
            bool instant = MissileAssemblyUtility.GodModeInstant;
            if (instant)
            {
                GUI.color = DMSEUI.Muted;
                list.Label("DMSE.Missile.GodModeInstant".Translate());
                GUI.color = Color.white;
            }

            // 被打擊任務保留的導彈／彈道井：任務結束前不可改裝（按鈕停用，數值照常顯示）
            if (reserved)
            {
                GUI.color = ColorLibrary.RedReadable;
                list.Label("DMSE.Strike.ReservedNoEdit".Translate());
                GUI.color = Color.white;
            }

            list.GapLine(6f);
            GUI.enabled = !reserved;

            // 彈體：發射井（Launcher 型，無物品型態）可切換；物品／儲存架唯讀。
            list.Label("DMSE.Missile.Body".Translate());
            TaggedString bodyLabel = cfg.body != null
                ? cfg.body.LabelCap
                : "DMSE.Missile.None".Translate();
            if (CanChooseBody)
            {
                if (list.ButtonText(bodyLabel))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (MissileBodyDef b in SelectableBodies())
                    {
                        MissileBodyDef bl = b;
                        // 未研究的彈體顯示為鎖定（上帝模式即時裝配不受研究限制，與部件選單一致）。
                        if (bl != cfg.body && !bl.ResearchSatisfied && !MissileAssemblyUtility.GodModeInstant)
                        {
                            opts.Add(new FloatMenuOption(bl.LabelCap + " (" + "DMSE.Missile.Locked".Translate() + ")", null));
                            continue;
                        }
                        opts.Add(new FloatMenuOption(bl.LabelCap, delegate
                        {
                            cfg.body = bl;
                            cfg.PruneIncompatible();
                            comp.assemblyConfirmed = false;
                            comp.RefundDeliveredIfIdle();
                        }));
                    }
                    if (opts.Any()) { Find.WindowStack.Add(new FloatMenu(opts)); }
                }
            }
            else
            {
                // 唯讀：彈體由物品 def／平台決定
                string bodyFixed = "DMSE.Missile.BodyFixed".Translate();
                GUI.color = DMSEUI.Muted;
                list.Label(bodyLabel + " (" + bodyFixed + ")");
                GUI.color = Color.white;
            }
            list.Gap(8f);

            // 各槽位
            if (cfg.body != null && cfg.body.slots != null)
            {
                foreach (MissilePartCategory cat in cfg.body.slots)
                {
                    DrawSlot(list, comp, cfg, cat);
                }
            }

            GUI.enabled = true;
            list.GapLine(8f);

            // 有效數值（待裝配設定）
            // §2.1: keep the decision metrics visible; detailed specifications are optional.
            if (comp.NeedsAssembly) { list.Label("DMSE.UI.Comparison".Translate()); }
            DMSEUI.ValueRow(list, "DMSE.UI.RangeChange".Translate(), comp.NeedsAssembly
                ? comp.config.Range + " → " + cfg.Range : cfg.Range.ToString());
            DMSEUI.ValueRow(list, "DMSE.UI.DamageChange".Translate(), comp.NeedsAssembly
                ? comp.config.DamageAmount + " → " + cfg.DamageAmount : cfg.DamageAmount.ToString());
            list.Gap(DMSEUI.Gap);
            if (list.ButtonText((showStats ? "DMSE.UI.HideStats" : "DMSE.UI.ShowStats").Translate())) { showStats = !showStats; }
            if (showStats)
            {
                list.Label("DMSE.Missile.Stats".Translate());
                list.Label("DMSE.Missile.Stat.Radius".Translate(cfg.ExplosionRadius.ToString("0.#")));
                list.Label("DMSE.Missile.Stat.Damage".Translate(
                    cfg.DamageDef != null ? cfg.DamageDef.LabelCap : (TaggedString)"-", cfg.DamageAmount));
                list.Label("DMSE.Missile.Stat.Scatter".Translate(cfg.Scatter.ToString("0.#")));
                list.Label("DMSE.Missile.Stat.Speed".Translate(cfg.WorldSpeedFactor.ToString("0.##")));
                list.Label("DMSE.Missile.Stat.Fuel".Translate(cfg.Fuel.ToString("0")));
                list.Label("DMSE.Missile.Stat.Impulse".Translate(cfg.SpecificImpulse.ToString("0.#")));
                if (cfg.Range > 0)
                {
                    list.Label("DMSE.Missile.Stat.Range".Translate(cfg.Range));
                }
                if (!Mathf.Approximately(cfg.SignatureFactor, 1f))
                {
                    list.Label("DMSE.Missile.Stat.Signature".Translate(cfg.SignatureFactor.ToString("0.#")));
                }
                if (cfg.ChaffCharges > 0)
                {
                    list.Label("DMSE.Missile.Stat.Chaff".Translate(cfg.ChaffCharges, cfg.JamStrength.ToString("0.##")));
                }
                list.Label(cfg.HasTerminalGuidance
                    ? "DMSE.Missile.Stat.TerminalGuidance".Translate(cfg.TerminalUpdates)
                    : "DMSE.Missile.Stat.NoTerminalGuidance".Translate());
                // 盤旋飛控：導引頭找不到目標時在目標上空等待的時間。
                if (cfg.LoiterTicks > 0)
                {
                    list.Label("DMSE.Missile.Stat.Loiter".Translate(cfg.LoiterTicks.ToStringTicksToPeriod()));
                }
                list.Label(CompMissileLauncher_WorldTargeting.GuidanceTargetingText(cfg));
            }

            // 裝配狀態 + 確認／還原按鈕
            if (comp.NeedsAssembly)
            {
                list.GapLine(8f);
                Dictionary<ThingDef, int> need = comp.StillNeeded();
                if (need.Count > 0)
                {
                    string costStr = string.Join(", ", need.Select(kv => kv.Key.label + " x" + kv.Value));
                    GUI.color = ColorLibrary.RedReadable;
                    list.Label("DMSE.Missile.NeedResources".Translate(costStr));
                    if (!reserved) { footerStatus = "DMSE.Missile.NeedResources".Translate(costStr); }
                    GUI.color = Color.white;
                }
                else
                {
                    GUI.color = DMSEUI.Warning;
                    list.Label("DMSE.Missile.ReadyToAssemble".Translate());
                    if (!reserved && !comp.assemblyConfirmed) { footerStatus = "DMSE.Missile.ReadyToAssemble".Translate(); }
                    GUI.color = Color.white;
                }
            }

            lastHeight = list.CurHeight + DMSEUI.Gap;
            list.End();
            Widgets.EndScrollView();

            // §2.8: one primary action; reverting is a quiet secondary action.
            Rect statusRect = new Rect(rect.x, footer.y - Text.LineHeight - DMSEUI.Gap, rect.width, Text.LineHeight);
            GUI.color = reserved ? DMSEUI.Error : comp.NeedsAssembly ? DMSEUI.Warning : DMSEUI.Success;
            Widgets.Label(statusRect, footerStatus.Truncate(statusRect.width));
            TooltipHandler.TipRegion(statusRect, footerStatus);
            GUI.color = Color.white;
            Widgets.DrawLineHorizontal(rect.x, statusRect.y - DMSEUI.Gap, rect.width);
            if (comp.NeedsAssembly)
            {
                float half = (footer.width - DMSEUI.Gap) / 2f;
                Rect reset = new Rect(footer.x, footer.y, half, footer.height);
                Rect confirm = new Rect(reset.xMax + DMSEUI.Gap, footer.y, half, footer.height);
                if (Widgets.ButtonText(reset, "DMSE.Missile.ResetToCurrent".Translate(), drawBackground: false, active: !reserved))
                {
                    comp.pending.CopyFrom(comp.config);
                    comp.assemblyConfirmed = false;
                    comp.RefundDeliveredIfIdle();
                }
                if (Widgets.ButtonText(confirm, (comp.assemblyConfirmed ? "DMSE.Missile.AssemblyQueued" : "DMSE.Missile.ConfirmAssembly").Translate(),
                    active: !reserved && !comp.assemblyConfirmed))
                {
                    comp.assemblyConfirmed = true;
                }
                if (reserved) { TooltipHandler.TipRegion(footer, "DMSE.Strike.ReservedNoEdit".Translate()); }
            }
            else
            {
                GUI.color = DMSEUI.Success;
                Widgets.Label(footer, "DMSE.UI.ActiveConfig".Translate());
                GUI.color = Color.white;
            }

            if (instant) { MissileAssemblyUtility.TryInstantAssemble(SelThing); }
        }

        private void DrawSlot(Listing_Standard list, CompMissileConfig comp, MissileConfig cfg, MissilePartCategory cat)
        {
            MissilePartDef cur = cfg.PartFor(cat);
            string categoryLabel = ("DMSE.Missile.Cat." + cat).Translate();
            string partLabel = cur != null ? cur.LabelCap.ToString() : "DMSE.Missile.None".Translate().ToString();
            float height = Mathf.Max(28f, Text.CalcHeight(categoryLabel, list.ColumnWidth * 0.38f),
                Text.CalcHeight(partLabel, list.ColumnWidth * 0.60f - DMSEUI.Gap) + DMSEUI.Gap);
            Rect row = list.GetRect(height);
            Rect labelRect = row.LeftPart(0.38f);
            Rect btnRect = row.RightPart(0.60f);

            Widgets.Label(labelRect, categoryLabel);
            TooltipHandler.TipRegion(btnRect, partLabel);
            if (Widgets.ButtonText(btnRect, cur != null ? cur.LabelCap : (TaggedString)"DMSE.Missile.None".Translate()))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                opts.Add(new FloatMenuOption("DMSE.Missile.None".Translate(), delegate
                {
                    cfg.SetPart(cat, null);
                    comp.assemblyConfirmed = false;
                    comp.RefundDeliveredIfIdle();
                }));

                foreach (MissilePartDef part in DefDatabase<MissilePartDef>.AllDefsListForReading)
                {
                    if (part.category != cat || !part.CompatibleWith(cfg.body)) { continue; }
                    MissilePartDef p = part;
                    string label = p.LabelCap + CostSuffix(p);
                    if (!p.ResearchSatisfied && !MissileAssemblyUtility.GodModeInstant)
                    {
                        opts.Add(new FloatMenuOption(label + " (" + "DMSE.Missile.Locked".Translate() + ")", null));
                    }
                    else
                    {
                        opts.Add(new FloatMenuOption(label, delegate
                        {
                            cfg.SetPart(cat, p);
                            comp.assemblyConfirmed = false;
                            comp.RefundDeliveredIfIdle();
                        }));
                    }
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            list.Gap(4f);
        }

        private static string CostSuffix(MissilePartDef p)
        {
            if (p.costList == null || p.costList.Count == 0) { return string.Empty; }
            return " (" + string.Join(", ", p.costList.Select(c => c.thingDef.label + " x" + c.count)) + ")";
        }
    }
}
