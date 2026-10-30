using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 掛在非 <see cref="Building_WorkTable"/> 的建築上：資訊卡照工作台的規則顯示
    /// <c>showOnNonWorkTables = false</c> 的建築屬性（工作速度係數等），讓設施、戶外、溫度各項加減逐條可見。
    /// 用 modExtension 而不改 thingClass，避免組裝台變成 IBillGiver、被其他模組當成工作台加分頁。
    /// </summary>
    public class WorkTableStatsExtension : DefModExtension
    {
    }

    /// <summary>
    /// 原版 <see cref="StatWorker.ShouldShowFor"/> 對建築分類的屬性有一條
    /// <c>!stat.showOnNonWorkTables &amp;&amp; !thingDef.IsWorkTable → 不顯示</c>；
    /// 對掛了 <see cref="WorkTableStatsExtension"/> 的 def 只放寬這一條，其他隱藏理由照舊。
    /// </summary>
    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.ShouldShowFor))]
    public static class Patch_StatWorker_ShowWorkTableStats
    {
        public static void Postfix(StatRequest req, ref bool __result, StatDef ___stat)
        {
            if (__result || ___stat == null || ___stat.showOnNonWorkTables || ___stat.alwaysHide) { return; }
            if (___stat.category != StatCategoryDefOf.Building) { return; }
            if (!(req.Def is ThingDef td) || td.IsWorkTable || !td.HasModExtension<WorkTableStatsExtension>()) { return; }
            if (!___stat.showIfUndefined && !req.StatBases.StatListContains(___stat)) { return; }
            __result = true;
        }
    }
}
