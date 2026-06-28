using System.Collections.Generic;
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

        private static readonly Color DimColor     = new Color(0.65f, 0.65f, 0.65f);
        private static readonly Color PendingColor = new Color(1f,    0.80f, 0.25f);
        private static readonly Color ReadyColor   = new Color(0.55f, 0.90f, 0.55f);

        // ── 狀態 ────────────────────────────────────────────────────
        private Vector2 scrollPos;
        private float   lastDrawnHeight;
        private Thing   thingToSelect;   // SelectLater 模式

        private Building_MissileRack Rack => SelThing as Building_MissileRack;

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

            Building_MissileRack rack = Rack;
            if (rack == null) { return; }

            IReadOnlyList<Thing> missiles = rack.HeldThings;

            Rect outerRect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);

            // 標題
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(outerRect.x, outerRect.y, outerRect.width, 28f),
                "DMSE.Tab.MissileInventory".Translate());
            Text.Font = GameFont.Small;

            // 容量副標題
            GUI.color = Color.gray;
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
                GUI.color   = Color.gray;
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
            Rect row = new Rect(0f, curY, width, RowHeight);

            // 背景：選中 / hover
            if (index % 2 == 1) { Widgets.DrawAltRect(row); }
            if (Find.Selector.IsSelected(missile)) { Widgets.DrawHighlight(row); }
            Widgets.DrawHighlightIfMouseover(row);

            // 圖示（原版 28 × 28，垂直置中）
            float iconY = curY + (RowHeight - IconSize) * 0.5f;
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
            DrawStats(new Rect(ContentLeft, curY + 20f, contentW, RowHeight - 20f), missile);

            // ── 點選整列 → SelectLater ─────────────────────────────
            // ButtonInvisible 在所有子控件之後：ButtonImage（rename）先消耗事件，故無衝突
            if (Widgets.ButtonInvisible(row))
            {
                thingToSelect = missile;
            }

            curY += RowHeight;
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

                // 判斷彈頭 / 導引的 pending 差異
                MissilePartDef activeWH  = active.PartFor(MissilePartCategory.Warhead);
                MissilePartDef activeGUD = active.PartFor(MissilePartCategory.Guidance);
                MissilePartDef showWH    = activeWH;
                MissilePartDef showGUD   = activeGUD;
                bool           whDiff    = false;
                bool           gudDiff   = false;

                if (isPending)
                {
                    MissilePartDef pWH  = cfg.pending.PartFor(MissilePartCategory.Warhead);
                    MissilePartDef pGUD = cfg.pending.PartFor(MissilePartCategory.Guidance);
                    whDiff  = pWH  != activeWH;
                    gudDiff = pGUD != activeGUD;
                    if (whDiff)  { showWH  = pWH; }
                    if (gudDiff) { showGUD = pGUD; }
                }

                // 第一列：彈頭（左半）| 導引（右半）
                float halfW = r.width * 0.5f;
                DrawPartLabel(new Rect(r.x,         r.y, halfW, 16f),
                    "DMSE.MissileInventory.Warhead".Translate(),  showWH,  noneStr, whDiff);
                DrawPartLabel(new Rect(r.x + halfW, r.y, halfW, 16f),
                    "DMSE.MissileInventory.Guidance".Translate(), showGUD, noneStr, gudDiff);

                // 第二列：爆炸半徑 & 傷害（僅繪製一次，有 pending 時橙黃色）
                GUI.color = isPending ? PendingColor : DimColor;
                string statsLine = "DMSE.MissileInventory.Stats"
                    .Translate(active.ExplosionRadius.ToString("0.#"), active.DamageAmount);
                if (isPending)
                {
                    statsLine += "  [" + "DMSE.MissileInventory.Pending".Translate() + "]";
                }
                Widgets.Label(new Rect(r.x, r.y + 17f, r.width, 16f), statsLine);
            }
            else
            {
                // 攔截彈：固定戰備狀態
                GUI.color = ReadyColor;
                Widgets.Label(new Rect(r.x, r.y, r.width, 16f),
                    "DMSE.MissileInventory.InterceptorReady".Translate());
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        /// <summary>
        /// 繪製單個部件標籤，差異欄位橙黃並加 " *"，否則顯示暗灰。
        /// 若超出寬度則截短並加省略號。
        /// </summary>
        private static void DrawPartLabel(Rect r, TaggedString prefix,
            MissilePartDef part, TaggedString noneStr, bool changed)
        {
            GUI.color = changed ? PendingColor : DimColor;
            string val   = part != null ? part.LabelCap.ToString() : noneStr.ToString();
            string label = prefix + ": " + val + (changed ? " *" : "");
            Widgets.Label(r, label.Truncate(r.width));
        }
    }
}
