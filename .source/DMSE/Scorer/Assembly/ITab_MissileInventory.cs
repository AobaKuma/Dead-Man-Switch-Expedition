using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 顯示 <see cref="Building_MissileRack"/> 內部容器所有已裝載導彈的清單。
    /// 原版 ITab_ContentsBase 風格：28px 圖示、flowing curY、lastDrawnHeight scrollview、
    /// SelectLater 模式（EndScrollView 後才切換選取）。
    /// </summary>
    public class ITab_MissileInventory : ITab
    {
        // ── 常數（對齊原版 ITab_ContentsBase 慣例）──────────────────
        private static readonly Vector2 WinSize    = new Vector2(440f, 480f);
        private const float RowHeight   = 84f;   // 名稱、特性標籤、有效數值
        private const float FeatureTagStep = 19f;
        private const float IconSize    = 28f;   // 對齊原版
        private const float IconLeft    = 4f;
        private const float ContentLeft = IconLeft + IconSize + 6f; // 38f

        private static readonly Color DimColor     = DMSEUI.Muted;
        private static readonly Color PendingColor = DMSEUI.Warning;
        private static readonly Color ReadyColor   = DMSEUI.Success;
        private static readonly Color FeatureHeaderColor = new Color(0.95f, 0.82f, 0.25f);
        private static readonly Color FeatureTagColor = new Color(0.19f, 0.20f, 0.22f);

        // ── 狀態 ────────────────────────────────────────────────────
        private Vector2 scrollPos;
        private float   lastDrawnHeight;
        private Thing   thingToSelect;   // SelectLater 模式

        private IMissileRack Rack => MissileRackUtil.Of(SelThing);

        public override bool IsVisible => Rack != null;

        public ITab_MissileInventory()
        {
            size     = WinSize;
            labelKey = "DMSE.Tab.MissileInventory";
        }

        protected override void UpdateSize()
        {
            IReadOnlyList<Thing> missiles = Rack?.HeldThings;
            float contentHeight = missiles == null || missiles.Count == 0 ? 36f : 0f;
            float contentWidth = WinSize.x - 36f - ContentLeft;

            if (missiles != null)
            {
                for (int i = 0; i < missiles.Count; i++)
                {
                    Thing missile = missiles[i];
                    if (missile == null) { continue; }
                    CompMissileConfig cfg = missile.TryGetComp<CompMissileConfig>();
                    if (cfg != null) { cfg.EnsureInit(); }
                    int tagRows = cfg?.config != null ? FeatureTagRowCount(cfg.config, contentWidth) : 0;
                    contentHeight += RowHeight + Mathf.Max(0, tagRows - 1) * FeatureTagStep
                        + (cfg?.NeedsAssembly == true ? 20f : 0f);
                }
            }

            // Keep a compact tab for small inventories and retain the existing scrollable cap.
            size = new Vector2(WinSize.x, Mathf.Min(WinSize.y, contentHeight + 78f));
        }

        // ── FillTab ─────────────────────────────────────────────────

        protected override void FillTab()
        {
            thingToSelect = null;

            IMissileRack rack = Rack;
            if (rack == null) { return; }

            IReadOnlyList<Thing> missiles = rack.HeldThings;

            Rect outerRect = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);

            // 標題
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(outerRect.x, outerRect.y, outerRect.width, 28f),
                "DMSE.Tab.MissileInventory".Translate());
            Text.Font = GameFont.Small;

            // 容量副標題
            GUI.color = DMSEUI.Muted;
            Widgets.Label(new Rect(outerRect.x, outerRect.y + 30f, outerRect.width, 20f),
                "DMSE.MissileInventory.Count".Translate(missiles.Count, rack.MaxStored));
            GUI.color = Color.white;

            // 分隔線
            float divY    = outerRect.y + 54f;
            Widgets.DrawLineHorizontal(outerRect.x, divY, outerRect.width);
            float listTop = divY + 4f;

            // Scrollview（lastDrawnHeight 模式）
            Rect listOutRect = new Rect(outerRect.x, listTop, outerRect.width,
                                        outerRect.yMax - listTop);
            Rect viewRect = new Rect(0f, 0f,
                listOutRect.width - 16f,
                Mathf.Max(lastDrawnHeight, listOutRect.height));

            Widgets.BeginScrollView(listOutRect, ref scrollPos, viewRect);
            Widgets.BeginGroup(viewRect);

            float curY = 0f;

            if (missiles.Count == 0)
            {
                GUI.color   = DMSEUI.Muted;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, 0f, viewRect.width, 36f),
                    "DMSE.MissileInventory.Empty".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color   = Color.white;
                curY        = 36f;
            }
            else
            {
                for (int i = 0; i < missiles.Count; i++)
                {
                    Thing missile = missiles[i];
                    if (missile == null) { continue; }
                    DrawRow(missile, viewRect.width, ref curY, i);
                }
            }

            lastDrawnHeight = curY;
            Widgets.EndGroup();
            Widgets.EndScrollView();

            // SelectLater：EndScrollView 之後才切換選取（原版慣例）
            if (thingToSelect != null)
            {
                Find.Selector.ClearSelection();
                Find.Selector.Select(thingToSelect);
                thingToSelect = null;
            }
        }

        // ── 單列 ────────────────────────────────────────────────────

        private void DrawRow(Thing missile, float width, ref float curY, int index)
        {
            CompMissileConfig cfg = missile.TryGetComp<CompMissileConfig>();
            if (cfg != null) { cfg.EnsureInit(); }
            bool pending = cfg?.NeedsAssembly == true;
            float contentW = width - ContentLeft;
            int tagRows = cfg?.config != null ? FeatureTagRowCount(cfg.config, contentW) : 0;
            float height = RowHeight + Mathf.Max(0, tagRows - 1) * FeatureTagStep + (pending ? 20f : 0f);
            Rect row = new Rect(0f, curY, width, height);

            // 背景：選中 / hover
            if (index % 2 == 1) { Widgets.DrawAltRect(row); }
            if (Find.Selector.IsSelected(missile)) { Widgets.DrawHighlight(row); }
            Widgets.DrawHighlightIfMouseover(row);

            // 圖示（原版 28 × 28，垂直置中）
            float iconY = curY + (height - IconSize) * 0.5f;
            Widgets.ThingIcon(new Rect(IconLeft, iconY, IconSize, IconSize), missile, scale: 0.75f);

            // ── 名稱 + 重命名按鈕 ──────────────────────────────────
            const float RenameSize = 20f;
            CompRenameable rnComp = missile.TryGetComp<CompRenameable>();
            float nameW    = contentW - (rnComp != null ? RenameSize + 4f : 0f);

            string displayName = rnComp != null ? rnComp.RenamableLabel : missile.LabelNoCount;

            Text.Font   = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color   = Color.white;
            Widgets.Label(new Rect(ContentLeft, curY, nameW, 20f),
                displayName.CapitalizeFirst().Truncate(nameW));
            TooltipHandler.TipRegion(new Rect(ContentLeft, curY, nameW, 20f), displayName);
            Text.Anchor = TextAnchor.UpperLeft;

            if (rnComp != null)
            {
                Rect renameBtn = new Rect(ContentLeft + nameW + 2f, curY + 1f, RenameSize, RenameSize);
                TooltipHandler.TipRegionByKey(renameBtn, "Rename");
                if (Widgets.ButtonImage(renameBtn, TexButton.Rename))
                {
                    Find.WindowStack.Add(new Dialog_RenameBuilding(rnComp));
                }
            }

            // ── 特性標籤與統計摘要（名稱佔 20px）──────────────────
            DrawStats(new Rect(ContentLeft, curY + 20f, contentW, height - 20f), missile, tagRows);

            // ── 點選整列 → SelectLater ─────────────────────────────
            // ButtonInvisible 在所有子控件之後：ButtonImage（rename）先消耗事件，故無衝突
            if (Widgets.ButtonInvisible(row))
            {
                thingToSelect = missile;
            }

            curY += height;
        }

        // ── 統計顯示 ────────────────────────────────────────────────

        private static void DrawStats(Rect r, Thing missile, int tagRows)
        {
            Text.Font = GameFont.Tiny;

            CompMissileConfig cfg = missile.TryGetComp<CompMissileConfig>();
            if (cfg != null)
            {
                cfg.EnsureInit();
                MissileConfig active    = cfg.config;
                bool          isPending = cfg.NeedsAssembly;

                TaggedString noneStr = "DMSE.Missile.None".Translate();

                // §2.1 / §3.6: every visible stat describes the active configuration.
                GUI.color = FeatureHeaderColor;
                Widgets.Label(new Rect(r.x, r.y, r.width, 14f), "DMSE.MissileInventory.Features".Translate());
                GUI.color = Color.white;
                DrawFeatureTags(new Rect(r.x, r.y + 14f, r.width, Mathf.Max(FeatureTagStep, tagRows * FeatureTagStep)), active);

                // 第二列：生效中的爆炸半徑與傷害；待裝配狀態另列。
                GUI.color = DimColor;
                string statsLine = "DMSE.MissileInventory.Stats"
                    .Translate(active.ExplosionRadius.ToString("0.#"), active.DamageAmount);
                if (isPending)
                {
                    GUI.color = PendingColor;
                    float tagExtraHeight = Mathf.Max(0, tagRows - 1) * FeatureTagStep;
                    Rect pendingRect = new Rect(r.x, r.y + 64f + tagExtraHeight, r.width, 20f);
                    Widgets.Label(pendingRect, "DMSE.UI.PendingConfig".Translate().ToString().Truncate(r.width));
                    GUI.color = DimColor;
                    string tip = "DMSE.UI.ActiveConfig".Translate() + "\n" +
                        "DMSE.MissileInventory.Stats".Translate(active.ExplosionRadius.ToString("0.#"), active.DamageAmount)
                        + "\n\n" + "DMSE.UI.PendingConfig".Translate() + "\n"
                        + "DMSE.Missile.Body".Translate() + ": "
                        + (active.body?.LabelCap.ToString() ?? noneStr.ToString()) + " → "
                        + (cfg.pending.body?.LabelCap.ToString() ?? noneStr.ToString()) + "\n"
                        + string.Join("\n", (cfg.pending.body?.slots ?? new List<MissilePartCategory>()).Union(active.body?.slots ?? new List<MissilePartCategory>()).Select(cat =>
                            ("DMSE.Missile.Cat." + cat).Translate() + ": "
                            + (active.PartFor(cat)?.LabelCap.ToString() ?? noneStr.ToString()) + " → "
                            + (cfg.pending.PartFor(cat)?.LabelCap.ToString() ?? noneStr.ToString())))
                        + "\n" + "DMSE.MissileInventory.Stats".Translate(cfg.pending.ExplosionRadius.ToString("0.#"), cfg.pending.DamageAmount);
                    TooltipHandler.TipRegion(pendingRect, tip);
                }
                statsLine = "DMSE.UI.ActiveConfig".Translate() + ": " + statsLine;
                Rect statsRect = new Rect(r.x, r.y + 48f + Mathf.Max(0, tagRows - 1) * FeatureTagStep, r.width, 16f);
                DrawActiveStats(statsRect, active);
                TooltipHandler.TipRegion(statsRect, statsLine);
            }
            else
            {
                // 攔截彈：固定戰備狀態
                GUI.color = ReadyColor;
                Widgets.Label(new Rect(r.x, r.y, r.width, 16f),
                    "DMSE.MissileLauncher.Interceptor.Ready".Translate());
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void DrawActiveStats(Rect r, MissileConfig config)
        {
            Text.Font = GameFont.Tiny;
            string activeLabel = "DMSE.UI.ActiveConfig".Translate() + ":";
            float activeWidth = Text.CalcSize(activeLabel).x + 6f;
            float gap = 8f;
            float metricWidth = (r.width - activeWidth - gap) * 0.5f;

            GUI.color = DimColor;
            Widgets.Label(new Rect(r.x, r.y, activeWidth, r.height), activeLabel);
            DrawStatMetric(new Rect(r.x + activeWidth, r.y, metricWidth, r.height),
                "DMSE.MissileInventory.Stat.BlastRadius".Translate(),
                config.ExplosionRadius.ToString("0.#") + " " + "DMSE.MissileInventory.Unit.Tiles".Translate());
            DrawStatMetric(new Rect(r.x + activeWidth + metricWidth + gap, r.y, metricWidth, r.height),
                "DMSE.MissileInventory.Stat.Damage".Translate(), config.DamageAmount.ToString());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static void DrawStatMetric(Rect r, string label, string value)
        {
            float labelWidth = Mathf.Min(Text.CalcSize(label).x + 4f, r.width * 0.62f);
            GUI.color = DimColor;
            Widgets.Label(new Rect(r.x, r.y, labelWidth, r.height), label);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(r.x + labelWidth, r.y, r.width - labelWidth, r.height), value);
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static void DrawFeatureTags(Rect r, MissileConfig config)
        {
            if (config?.parts == null || config.parts.Count == 0) { return; }

            const float tagHeight = 17f;
            const float padX = 8f;
            const float gapX = 4f;
            const float gapY = 2f;
            float x = r.x;
            float y = r.y;
            Text.Font = GameFont.Tiny;
            for (int i = 0; i < config.parts.Count; i++)
            {
                MissilePartDef part = config.parts[i];
                if (part == null) { continue; }
                string label = part.LabelCap.ToString();
                float tagWidth = Mathf.Min(r.width, Text.CalcSize(label).x + padX * 2f);
                if (x > r.x && x + tagWidth > r.xMax)
                {
                    x = r.x;
                    y += tagHeight + gapY;
                }

                Rect tag = new Rect(x, y, tagWidth, tagHeight);
                Widgets.DrawBoxSolid(tag, FeatureTagColor);
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = Color.white;
                Widgets.Label(new Rect(tag.x + 4f, tag.y, tag.width - 8f, tag.height), label.Truncate(tag.width - 8f));
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(tag, BuildFeatureTooltip(config, part));
                x += tagWidth + gapX;
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Tiny;
        }

        private static int FeatureTagRowCount(MissileConfig config, float width)
        {
            if (config?.parts == null || config.parts.Count == 0) { return 0; }
            GameFont previousFont = Text.Font;
            Text.Font = GameFont.Tiny;
            int rows = 1;
            float x = 0f;
            for (int i = 0; i < config.parts.Count; i++)
            {
                MissilePartDef part = config.parts[i];
                if (part == null) { continue; }
                float tagWidth = Mathf.Min(width, Text.CalcSize(part.LabelCap.ToString()).x + 16f);
                if (x > 0f && x + tagWidth > width)
                {
                    rows++;
                    x = 0f;
                }
                x += tagWidth + 4f;
            }
            Text.Font = previousFont;
            return rows;
        }

        private static string BuildFeatureTooltip(MissileConfig config, MissilePartDef part)
        {
            string category = ("DMSE.Missile.Cat." + part.category).Translate();
            string tip = category + ": " + part.LabelCap;
            if (!part.description.NullOrEmpty()) { tip += "\n\n" + part.description; }

            string mechanics;
            switch (part.category)
            {
                case MissilePartCategory.Warhead:
                    mechanics = "DMSE.MissileInventory.Feature.Warhead".Translate(
                        config.DamageDef.LabelCap, config.DamageAmount,
                        config.ExplosionRadius.ToString("0.#"), config.PayloadCapacity.ToString("0.#"));
                    if (part.warheadEffect != null)
                    {
                        float bonus = part.warheadEffect.InterceptBonus(config.PayloadCapacity);
                        int extraRolls = part.warheadEffect.ExtraInterceptRolls(config.PayloadCapacity);
                        float range = part.warheadEffect.RangeOffset(config.PayloadCapacity);
                        if (bonus > 0f) { mechanics += "\n" + "DMSE.MissileInventory.Feature.InterceptBonus".Translate((bonus * 100f).ToString("0.#")); }
                        if (extraRolls > 0) { mechanics += "\n" + "DMSE.MissileInventory.Feature.ExtraRolls".Translate(extraRolls); }
                        if (!Mathf.Approximately(range, 0f)) { mechanics += "\n" + "DMSE.MissileInventory.Feature.RangeOffset".Translate(range.ToString("+0.#;-0.#")); }
                        if (part.warheadEffect.PenetratesThickRoof) { mechanics += "\n" + "DMSE.MissileInventory.Feature.PenetratesRoof".Translate(); }
                    }
                    break;
                case MissilePartCategory.Guidance:
                    mechanics = "DMSE.MissileInventory.Feature.Guidance".Translate(
                        config.Scatter.ToString("0.#"), config.TerminalUpdates,
                        config.HasTerminalGuidance ? "DMSE.MissileInventory.Feature.Guided".Translate() : "DMSE.MissileInventory.Feature.Unguided".Translate());
                    break;
                case MissilePartCategory.Propulsion:
                    mechanics = "DMSE.MissileInventory.Feature.Propulsion".Translate(
                        config.Range, config.WorldSpeedFactor.ToString("0.##"),
                        config.Fuel.ToString("0.#"), config.SpecificImpulse.ToString("0.#"));
                    break;
                default:
                    mechanics = "DMSE.MissileInventory.Feature.Payload".Translate(
                        config.PayloadCapacity.ToString("0.#"), config.Range);
                    if (part.payloadEffect?.PenetratesThickRoof == true) { mechanics += "\n" + "DMSE.MissileInventory.Feature.PenetratesRoof".Translate(); }
                    break;
            }
            if (!Mathf.Approximately(part.signatureFactor, 1f))
            {
                mechanics += "\n" + "DMSE.MissileInventory.Feature.Signature".Translate(part.signatureFactor.ToString("0.##"));
            }
            if (part.jamStrength > 0f)
            {
                mechanics += "\n" + "DMSE.MissileInventory.Feature.Jamming".Translate((part.jamStrength * 100f).ToString("0.#"));
            }
            if (part.chaffCharges > 0)
            {
                mechanics += "\n" + "DMSE.MissileInventory.Feature.Chaff".Translate(part.chaffCharges);
            }
            tip += "\n\n" + "DMSE.MissileInventory.Feature.Mechanics".Translate() + "\n" + mechanics;
            return tip;
        }
    }
}
