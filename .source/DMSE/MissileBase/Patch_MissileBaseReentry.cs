using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 封鎖玩家在再進入冷卻期間進入導彈陣地。<br/>
    /// <br/>
    /// 攔截原版的進入判定 <see cref="CaravanArrivalAction_VisitSite.CanVisit"/>（車隊）與
    /// <see cref="TransportersArrivalAction_VisitSite.CanVisit"/>（運輸艙、穿梭機）：
    /// 當站點的 <see cref="WorldObjectComp_MissileBase.IsReentryBlocked"/> 為真時回傳失敗，
    /// 浮動選單顯示停用選項與剩餘時間；途中的車隊或運輸艙在抵達時也會因 StillValid 失敗而無法進入。<br/>
    /// <br/>
    /// 注：Site 覆寫了 GetFloatMenuOptions，修補 <see cref="WorldObject.GetFloatMenuOptions"/> 擋不住「接近站點」選項。
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MissileBaseReentry
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CaravanArrivalAction_VisitSite), nameof(CaravanArrivalAction_VisitSite.CanVisit));
            yield return AccessTools.Method(typeof(TransportersArrivalAction_VisitSite), nameof(TransportersArrivalAction_VisitSite.CanVisit));
        }

        [HarmonyPostfix]
        public static void Postfix(Site site, ref FloatMenuAcceptanceReport __result)
        {
            if (!__result.Accepted) return;
            WorldObjectComp_MissileBase woc = site?.GetComponent<WorldObjectComp_MissileBase>();
            if (woc == null || !woc.IsReentryBlocked) return;

            string reason = "DMSE.MissileBase.ReentryBlocked".Translate(woc.ReentryBlockedFor.ToStringTicksToPeriod());
            __result = FloatMenuAcceptanceReport.WithFailReasonAndMessage(reason, reason);
        }
    }

    /// <summary>
    /// 原版 <see cref="Site.ShouldRemoveMapNow"/> 在玩家離開後總是連同站點一起刪除；
    /// 導彈陣地只在火控雷達全滅（<see cref="MapComponent_MissileBase.BaseSiteDestroyed"/>）後才刪除，
    /// 否則只移除地圖、保留站點（世界層恢復開火，下次進入時陣地完整重建）。
    /// </summary>
    [HarmonyPatch(typeof(Site), nameof(Site.ShouldRemoveMapNow))]
    public static class Patch_MissileBaseSiteKept
    {
        [HarmonyPostfix]
        public static void Postfix(Site __instance, bool __result, ref bool alsoRemoveWorldObject)
        {
            if (!__result || !alsoRemoveWorldObject) return;
            if (__instance.GetComponent<WorldObjectComp_MissileBase>() == null) return;
            MapComponent_MissileBase comp = __instance.Map?.GetComponent<MapComponent_MissileBase>();
            if (comp != null && !comp.BaseSiteDestroyed) alsoRemoveWorldObject = false;
        }
    }
}
