using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 在原版筆形按鈕的同一列（選取面板右上角）插入重命名按鈕。
    /// <para>
    ///   原版流程（<c>MainTabWindow_Inspect.DoInspectPaneButtons</c>）以一連串 else-if 判斷
    ///   何時顯示筆形按鈕：最後一個分支為 <c>singleSelectedThing is IRenameable</c>。
    ///   由於按鈕是由 Comp 提供而非 Thing 本身，無法命中原版判斷；
    ///   Postfix 在原版邏輯結束後，補上對 <see cref="CompRenameable"/> 的判斷。
    /// </para>
    /// <para>
    ///   防衛條件：若建築已被原版任一分支處理（自身是 <see cref="IRenameable"/>、
    ///   或是 <see cref="IStorageGroupMember"/>），跳過，避免重複繪製。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(MainTabWindow_Inspect), nameof(MainTabWindow_Inspect.DoInspectPaneButtons))]
    public static class Patch_InspectPaneRename
    {
        public static void Postfix(Rect rect, ref float lineEndWidth)
        {
            // 只在單選時作用。
            if (Find.Selector.NumSelected != 1) { return; }

            Thing thing = Find.Selector.SingleSelectedThing;
            if (thing == null || !thing.Spawned || thing.Faction != Faction.OfPlayer) { return; }

            // 若原版已能處理此 Thing（自身 IRenameable 或 IStorageGroupMember），不重複插入。
            if (thing is IRenameable) { return; }
            IStorageGroupMember sgm = thing as IStorageGroupMember;
            if (sgm != null && sgm.ShowRenameButton) { return; }

            CompRenameable comp = thing.TryGetComp<CompRenameable>();
            if (comp == null) { return; }

            // 在已有按鈕群的左側插入，與原版筆形按鈕的排列邏輯一致。
            float x = rect.width - lineEndWidth - 30f;
            Rect btnRect = new Rect(x, 0f, 30f, 30f);

            TooltipHandler.TipRegionByKey(btnRect, "Rename");
            if (Widgets.ButtonImage(btnRect, TexButton.Rename))
            {
                Find.WindowStack.Add(new Dialog_RenameBuilding(comp));
            }
            lineEndWidth += 30f;
        }
    }
}
