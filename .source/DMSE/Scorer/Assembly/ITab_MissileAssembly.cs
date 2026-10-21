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
                if (SelThing is Building_MissileRack rack)
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
                if (SelThing is Building_MissileRack rack)
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

            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(12f);
            Listing_Standard list = new Listing_Standard();
            list.Begin(rect);

            Text.Font = GameFont.Medium;
            list.Label("DMSE.Missile.Tab".Translate());
            Text.Font = GameFont.Small;

            // 儲存架模式：顯示正在編輯哪枚導彈
            string rackLabel = RackIndexLabel;
            if (rackLabel != null)
            {
                GUI.color = Color.gray;
                list.Label(rackLabel);
                GUI.color = Color.white;
            }

            // 上帝模式即時裝配：變更立即套用，不耗資源、不需研究
            bool instant = MissileAssemblyUtility.GodModeInstant;
            if (instant)
            {
                GUI.color = Color.gray;
                list.Label("DMSE.Missile.GodModeInstant".Translate());
                GUI.color = Color.white;
            }

            // 被打擊任務保留的導彈／彈道井：任務結束前不可改裝（按鈕停用，數值照常顯示）
            bool reserved = StrikeReservation.IsReserved(comp.parent);
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
                GUI.color = Color.gray;
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
            list.Label(CompMissileLauncher_WorldTargeting.GuidanceTargetingText(cfg));

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
                    GUI.color = Color.white;
                }
                else
                {
                    GUI.color = Color.yellow;
                    list.Label("DMSE.Missile.ReadyToAssemble".Translate());
                    GUI.color = Color.white;
                }

                list.Gap(4f);
                GUI.enabled = !reserved;

                // 確認裝配按鈕（未確認時顯示；確認後轉為提示文字）
                if (!comp.assemblyConfirmed)
                {
                    if (list.ButtonText("DMSE.Missile.ConfirmAssembly".Translate()))
                    {
                        comp.assemblyConfirmed = true;
                    }
                }
                else
                {
                    GUI.color = Color.green;
                    list.Label("DMSE.Missile.AssemblyQueued".Translate());
                    GUI.color = Color.white;
                }

                list.Gap(4f);

                // 還原為已裝配設定
                if (list.ButtonText("DMSE.Missile.ResetToCurrent".Translate()))
                {
                    comp.pending.CopyFrom(comp.config);
                    comp.assemblyConfirmed = false;
                    comp.RefundDeliveredIfIdle();
                }
                GUI.enabled = true;
            }

            list.End();

            if (instant) { MissileAssemblyUtility.TryInstantAssemble(SelThing); }
        }

        private void DrawSlot(Listing_Standard list, CompMissileConfig comp, MissileConfig cfg, MissilePartCategory cat)
        {
            MissilePartDef cur = cfg.PartFor(cat);
            Rect row = list.GetRect(28f);
            Rect labelRect = row.LeftPart(0.38f);
            Rect btnRect = row.RightPart(0.60f);

            Widgets.Label(labelRect, ("DMSE.Missile.Cat." + cat).Translate());
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
