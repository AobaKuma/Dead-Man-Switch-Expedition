using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 裝配方案清單，照原版檔案清單（<c>Dialog_FileList</c>，存讀檔與自訂異種人用的那個）：
    /// 40 高的交錯底色列、每列右側「載入」按鈕與刪除圖示（確認框用原版 ConfirmDelete）、底部置中「關閉」。
    /// 原版沒有預覽，所以構型摘要放在列的提示裡；不可用的方案名稱變灰並附原因，載入按鈕停用。
    /// </summary>
    public class Dialog_MissileDesignList : Window
    {
        private const float EntryHeight = 40f;
        private const float InteractButWidth = 100f;
        private const float DeleteButSize = 36f;

        private readonly Action<MissileDesignBlueprint> onLoad;
        private Vector2 scrollPosition;

        public override Vector2 InitialSize
            => new Vector2(Mathf.Min(620f, UI.screenWidth), Mathf.Min(700f, UI.screenHeight));

        public Dialog_MissileDesignList(Action<MissileDesignBlueprint> onLoad)
        {
            this.onLoad = onLoad;
            doCloseButton = true;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            MissileDesignBlueprintDatabase.Reload();   // 玩家可能在遊戲外新增或刪除了檔案
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Rect title = new Rect(inRect.x, inRect.y, inRect.width, 35f);
            Widgets.Label(title, "DMSE.Blueprint.Title".Translate());
            Text.Font = GameFont.Small;

            // 左下「打開資料夾」與原版底部「關閉」同列。
            Rect folder = new Rect(inRect.x, inRect.yMax - CloseButSize.y, 160f, CloseButSize.y);
            if (Widgets.ButtonText(folder, "DMSE.Blueprint.OpenFolder".Translate(), drawBackground: false))
            {
                System.IO.Directory.CreateDirectory(MissileDesignBlueprintDatabase.FolderPath);
                Application.OpenURL("file://" + MissileDesignBlueprintDatabase.FolderPath);
            }

            Rect outRect = new Rect(inRect.x, title.yMax + 10f, inRect.width, folder.y - 10f - title.yMax - 10f);
            IReadOnlyList<MissileDesignBlueprint> all = MissileDesignBlueprintDatabase.All;
            if (all.Count == 0)
            {
                GUI.color = DMSEUI.Muted;
                Widgets.Label(outRect, "DMSE.Blueprint.Empty".Translate());
                GUI.color = Color.white;
                return;
            }

            Rect viewRect = new Rect(0f, 0f, outRect.width - DMSEUI.ScrollbarWidth, all.Count * EntryHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            for (int i = 0; i < all.Count; i++)
            {
                Rect row = new Rect(0f, i * EntryHeight, viewRect.width, EntryHeight);
                if (i % 2 == 1) { Widgets.DrawAltRect(row); }
                DrawEntry(row, all[i]);
            }
            Widgets.EndScrollView();
        }

        private void DrawEntry(Rect row, MissileDesignBlueprint bp)
        {
            Widgets.BeginGroup(row);
            Rect delete = new Rect(row.width - DeleteButSize, (row.height - DeleteButSize) / 2f, DeleteButSize, DeleteButSize);
            if (Widgets.ButtonImage(delete, TexButton.Delete, Color.white, GenUI.SubtleMouseoverColor))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("ConfirmDelete".Translate(bp.name),
                    () => MissileDesignBlueprintDatabase.Delete(bp), destructive: true));
            }
            TooltipHandler.TipRegion(delete, "Delete".Translate());

            Rect load = new Rect(delete.x - InteractButWidth, (row.height - DeleteButSize) / 2f, InteractButWidth, DeleteButSize);
            if (Widgets.ButtonText(load, "Load".Translate(), active: bp.Usable) && bp.Usable)
            {
                onLoad?.Invoke(bp);
                Close();
            }

            MissileBodyDef body = DefDatabase<MissileBodyDef>.GetNamedSilentFail(bp.bodyDefName ?? string.Empty);
            if (body != null) { MissileDesignUI.DrawBodyIcon(new Rect(4f, 4f, 32f, 32f), body); }
            Rect text = new Rect(44f, 0f, load.x - 44f - DMSEUI.Gap, row.height);
            GUI.color = bp.Usable ? Color.white : DMSEUI.Muted;
            Widgets.Label(new Rect(text.x, 2f, text.width, 22f), (bp.name ?? "?").Truncate(text.width));
            Text.Font = GameFont.Tiny;
            GUI.color = bp.Usable ? DMSEUI.Muted : DMSEUI.Error;
            string sub = bp.Usable ? (body?.LabelCap.ToString() ?? string.Empty) : "✕ " + bp.unusableReason;
            Widgets.Label(new Rect(text.x, 22f, text.width, 16f), sub.Truncate(text.width));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            if (Mouse.IsOver(text))
            {
                Widgets.DrawHighlight(text);
                TooltipHandler.TipRegion(text, () => Summary(bp), bp.GetHashCode());
            }
            Widgets.EndGroup();
        }

        /// <summary>提示：構型、關鍵數值、能否生產、備註。</summary>
        private static string Summary(MissileDesignBlueprint bp)
        {
            MissileConfig cfg = bp.ToConfig();
            if (cfg == null) { return (bp.name ?? "?").AsTipTitle() + "\n\n" + ("✕ " + bp.unusableReason).Colorize(DMSEUI.Error); }
            List<string> lines = new List<string> { (bp.name ?? "?").AsTipTitle(), cfg.body.LabelCap };
            foreach (MissilePartCategory cat in cfg.body.slots)
            {
                MissilePartDef p = cfg.PartFor(cat);
                lines.Add("  " + ("DMSE.Missile.Cat." + cat).Translate() + ": " + (p != null ? p.LabelCap.ToString() : "DMSE.Missile.None".Translate().ToString()));
            }
            lines.Add(string.Empty);
            lines.Add("DMSE.UI.RangeChange".Translate() + " " + cfg.Range + " · " + "DMSE.UI.DamageChange".Translate() + " " + cfg.DamageAmount
                      + " · " + "DMSE.Bench.Stat.Radius".Translate() + " " + cfg.ExplosionRadius.ToString("0.#"));
            if (!MissileItemUtility.CanProduce(cfg.body, out string reason)) { lines.Add(("Locked".Translate() + ": " + reason).Colorize(DMSEUI.Warning)); }
            if (!bp.note.NullOrEmpty()) { lines.Add(string.Empty); lines.Add(bp.note); }
            return string.Join("\n", lines);
        }
    }
}
