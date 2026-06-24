using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 掛在導彈物品上的裝配介面（原版風格）。編輯的是 pending（待裝配）設定；
    /// 實際套用需由小人搬運資源完成（見 WorkGiver_AssembleMissile）。
    /// </summary>
    public class ITab_MissileAssembly : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(440f, 520f);

        public CompMissileConfig Comp => SelThing != null ? SelThing.TryGetComp<CompMissileConfig>() : null;

        public override bool IsVisible => Comp != null;

        public ITab_MissileAssembly()
        {
            size = WinSize;
            labelKey = "DMSE.Missile.Tab";
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
            list.GapLine(6f);

            // 彈體
            list.Label("DMSE.Missile.Body".Translate());
            if (list.ButtonText(cfg.body != null ? cfg.body.LabelCap : (TaggedString)"DMSE.Missile.None".Translate()))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (MissileBodyDef b in DefDatabase<MissileBodyDef>.AllDefsListForReading)
                {
                    MissileBodyDef bl = b;
                    opts.Add(new FloatMenuOption(bl.LabelCap, delegate
                    {
                        cfg.body = bl;
                        cfg.PruneIncompatible();
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

            // 裝配狀態
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
