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
        private static readonly Vector2 WinSize = new Vector2(440f, 520f);

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
                        return "DMSE.Missile.EditingSlot".Translate(1, held.Count);
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
        /// 彈體欄位是否應鎖定（不允許更換彈體）。
        /// 以下情況鎖定：
        ///   - SelThing 是建築（儲存架或彈道發射井）：彈體由架設計決定，不可更換。
        ///   - SelThing 的 CompMissileConfig.defaultBody 非 null：有明確指定預設彈體。
        /// </summary>
        private bool IsBodyLocked
        {
            get
            {
                if (SelThing == null) { return false; }
                // 任何建築（儲存架、彈道發射井）均鎖定彈體
                if (SelThing is Building) { return true; }
                // 物品若有固定 defaultBody 亦鎖定
                CompProperties_MissileConfig props = (SelThing.def.comps?
                    .OfType<CompProperties_MissileConfig>()
                    .FirstOrDefault());
                return props?.defaultBody != null;
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

            list.GapLine(6f);

            // 彈體（建築上鎖定：僅顯示，不可更換）
            list.Label("DMSE.Missile.Body".Translate());
            if (IsBodyLocked)
            {
                // 顯示灰色不可點選的彈體名稱
                GUI.color = Color.gray;
                list.Label(cfg.body != null ? cfg.body.LabelCap : (TaggedString)"DMSE.Missile.None".Translate());
                GUI.color = Color.white;
            }
            else if (list.ButtonText(cfg.body != null ? cfg.body.LabelCap : (TaggedString)"DMSE.Missile.None".Translate()))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (MissileBodyDef b in DefDatabase<MissileBodyDef>.AllDefsListForReading)
                {
                    MissileBodyDef bl = b;
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
            list.Gap(8f);

            // 各槽位
            if (cfg.body != null && cfg.body.slots != null)
            {
                foreach (MissilePartCategory cat in cfg.body.slots)
                {
                    DrawSlot(list, comp, cfg, cat);
                }
            }

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
            }

            list.End();
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
                    if (!p.ResearchSatisfied)
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
