using HarmonyLib;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 讓持有 <see cref="CompRenameable"/> 的建築在所有 UI 中顯示玩家設定的自訂名稱。
    /// <para>
    ///   Postfix 於 <see cref="Thing.LabelNoCount"/> getter 之後執行：
    ///   若 Comp 存在且 <see cref="CompRenameable.customLabel"/> 非空，即以自訂名稱覆蓋原始結果。
    ///   LabelNoCount 是 RimWorld 標籤顯示的最底層來源，覆蓋此處即可影響選取框、標籤、
    ///   檢視字串、Alert 的 LabelShort 等所有顯示路徑。
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.LabelNoCount), MethodType.Getter)]
    public static class Patch_ThingLabelNoCount
    {
        public static void Postfix(Thing __instance, ref string __result)
        {
            CompRenameable r = __instance.TryGetComp<CompRenameable>();
            if (r != null && !r.customLabel.NullOrEmpty())
            {
                __result = r.customLabel;
            }
        }
    }
}
