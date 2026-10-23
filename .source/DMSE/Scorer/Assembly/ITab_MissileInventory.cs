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
        private const float RowHeight   = 54f;   // icon(28) + 名稱(18) + 2 stats(16×2) + 間距
        private const float IconSize    = 28f;   // 對齊原版
        private const float IconLeft    = 4f;
        private const float ContentLeft = IconLeft + IconSize + 6f; // 38f

        private static readonly Color DimColor     = DMSEUI.Muted;
        private static readonly Color PendingColor = DMSEUI.Warning;
        private static readonly Color ReadyColor   = DMSEUI.Success;

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

        // ── FillTab ─────────────────────────────────────────────────

        protected override void FillTab()
        {
            thingToSelect = null;

            IMissileRack rack = Rack;
            if (rack == null) { return; }

            IReadOnlyList<Thing> missiles = rack.HeldThings;

            Rect outerRect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);

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
            bool pending = missile.TryGetComp<CompMissileConfig>()?.NeedsAssembly == true;
            float height = RowHeight + (pending ? 20f : 0f);
            Rect row = new Rect(0f, curY, width, height);

            // 背景：選中 / hover
            if (index % 2 == 1) { Widgets.DrawAltRect(row); }
            if (Find.Selector.IsSelected(missile)) { Widgets.DrawHighlight(row); }
            Widgets.DrawHighlightIfMouseover(row);

            // 圖示（原版 28 × 28，垂直置中）
            float iconY = curY + (height - IconSize) * 0.5f;
            Widgets.ThingIcon(new Rect(IconLeft, iconY, IconSize, IconSize), missile);

            // ── 名稱 + 重命名按鈕 ──────────────────────────────────
            const float RenameSize = 20f;
            CompRenameable rnComp = missile.TryGetComp<CompRenameable>();
            float contentW = width - ContentLeft;
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

            // ── 統計摘要（名稱佔 20px，剩餘給兩行 stats）──────────
            DrawStats(new Rect(ContentLeft, curY + 20f, contentW, height - 20f), missile);

            // ── 點選整列 → SelectLater ─────────────────────────────
            // ButtonInvisible 在所有子控件之後：ButtonImage（rename）先消耗事件，故無衝突
            if (Widgets.ButtonInvisible(row))
            {
                thingToSelect = missile;
            }

            curY += height;
        }

        // ── 統計顯示 ────────────────────────────────────────────────

        private static void DrawStats(Rect r, Thing missile)
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
                MissilePartDef showWH = active.PartFor(MissilePartCategory.Warhead);
                MissilePartDef showGUD = active.PartFor(MissilePartCategory.Guidance);
                // 第一列：彈頭（左半）| 導引（右半）
                float halfW = r.width * 0.5f;
                DrawPartLabel(new Rect(r.x,         r.y, halfW, 16f),
                    "DMSE.Missile.Cat.Warhead".Translate(),  showWH,  noneStr);
                DrawPartLabel(new Rect(r.x + halfW, r.y, halfW, 16f),
                    "DMSE.Missile.Cat.Guidance".Translate(), showGUD, noneStr);

                // 第二列：生效中的爆炸半徑與傷害；待裝配狀態另列。
                GUI.color = DimColor;
                string statsLine = "DMSE.MissileInventory.Stats"
                    .Translate(active.ExplosionRadius.ToString("0.#"), active.DamageAmount);
                if (isPending)
                {
                    GUI.color = PendingColor;
                    Rect pendingRect = new Rect(r.x, r.y + 34f, r.width, 20f);
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
                    TooltipHandler.TipRegion(new Rect(r.x, r.y + 34f, r.width, 20f), tip);
                }
                statsLine = "DMSE.UI.ActiveConfig".Translate() + ": " + statsLine;
                Rect statsRect = new Rect(r.x, r.y + 17f, r.width, 16f);
                Widgets.Label(statsRect, statsLine.Truncate(r.width));
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

        /// <summary>
        /// 繪製目前生效的部件標籤；待裝配差異在獨立狀態列顯示。
        /// 若超出寬度則截短並加省略號。
        /// </summary>
        private static void DrawPartLabel(Rect r, TaggedString prefix,
            MissilePartDef part, TaggedString noneStr)
        {
            GUI.color = DimColor;
            string val   = part != null ? part.LabelCap.ToString() : noneStr.ToString();
            string label = prefix + ": " + val;
            Widgets.Label(r, label.Truncate(r.width));
            TooltipHandler.TipRegion(r, label);
        }
    }
}
