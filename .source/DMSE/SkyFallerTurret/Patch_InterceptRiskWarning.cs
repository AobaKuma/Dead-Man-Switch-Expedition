using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 玩家載具起飛前（扣燃料之前）的攔截風險確認：目的地有防空雷達（空投艙／穿梭機落地時會被攔截），
    /// 或航線穿越防空封鎖圈（飛行中會被 <see cref="WorldComponent_BlockadeInterception"/> 接戰）。
    /// 確認後以同樣參數重新呼叫原本的動作；取消則什麼都不發生。
    /// </summary>
    public static class InterceptRiskUtility
    {
        /// <summary>確認窗口重新呼叫起飛時為 true，讓 prefix 放行。</summary>
        private static bool confirmed;

        /// <summary>
        /// 目的地會攔截 <paramref name="traveller"/> 空投的防禦方：
        /// 已有地圖時看地圖上的搜索雷達（與 <see cref="Patch_MakeDropPodAt"/> 同一判定）；
        /// 尚未生成地圖時看據點的世界防空元件（導彈陣地、野戰發射場生成地圖時會帶雷達）。無則 null。
        /// </summary>
        public static Faction DefenderAt(PlanetTile tile, Faction traveller)
        {
            if (!tile.Valid || traveller == null) { return null; }
            MapParent parent = Find.WorldObjects.MapParentAt(tile);
            if (parent == null) { return null; }
            if (parent.HasMap)
            {
                return parent.Map.GetComponent<MapComponent_BVRCombat>()?.ResolveDefender(traveller);
            }
            WorldObjectComp_AirDefense defense = parent.GetComponent<WorldObjectComp_AirDefense>();
            return defense != null && defense.Available && parent.Faction.HostileTo(traveller) ? parent.Faction : null;
        }

        /// <summary>
        /// 沒有風險（或已確認）就回傳 true 照常進行；有風險就彈出確認窗口並回傳 false。
        /// <paramref name="checkDestination"/>：落地時會經過地圖攔截的載具（空投艙、穿梭機）才檢查目的地雷達。
        /// </summary>
        public static bool ConfirmOrDefer(PlanetTile from, PlanetTile to, Faction traveller, bool checkDestination,
            Action relaunch, Action cancel = null)
        {
            if (confirmed) { return true; }
            List<string> risks = new List<string>();
            AirspaceRestriction route = AirspaceRestrictionUtility.CheckRoute(traveller, from, to);
            if (route.Restricted)
            {
                risks.Add("DMSE.BVR.InterceptRiskRoute".Translate(route.zone.faction.NameColored, route.zone.site.LabelCap,
                    route.zone.radius.ToString("0.#")));
            }
            Faction defender = checkDestination ? DefenderAt(to, traveller) : null;
            if (defender != null)
            {
                risks.Add("DMSE.BVR.InterceptRiskDestination".Translate(Find.WorldObjects.MapParentAt(to).LabelCap, defender.NameColored));
            }
            if (risks.Count == 0) { return true; }

            Find.WorldTargeter.StopTargeting();
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                string.Join("\n\n", risks) + "\n\n" + "DMSE.BVR.InterceptRiskFooter".Translate(),
                () =>
                {
                    confirmed = true;
                    try { relaunch(); }
                    finally { confirmed = false; }
                },
                cancel ?? delegate { }, destructive: true));
            return false;
        }
    }

    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.TryLaunch))]
    public static class Patch_InterceptRisk_TryLaunch
    {
        [HarmonyPrefix]
        public static bool Prefix(CompLaunchable __instance, PlanetTile destinationTile, TransportersArrivalAction arrivalAction, bool __runOriginal)
        {
            if (!__runOriginal || __instance.parent == null || !__instance.parent.Spawned) { return __runOriginal; }
            return InterceptRiskUtility.ConfirmOrDefer(__instance.parent.Map.Tile, destinationTile,
                __instance.parent.Faction ?? Faction.OfPlayer, checkDestination: true,
                () => __instance.TryLaunch(destinationTile, arrivalAction));
        }
    }

    [HarmonyPatch(typeof(CaravanShuttleUtility), nameof(CaravanShuttleUtility.LaunchShuttle))]
    public static class Patch_InterceptRisk_CaravanShuttle
    {
        [HarmonyPrefix]
        public static bool Prefix(Caravan caravan, PlanetTile destinationTile, TransportersArrivalAction arrivalAction, bool __runOriginal)
        {
            if (!__runOriginal || caravan == null) { return __runOriginal; }
            return InterceptRiskUtility.ConfirmOrDefer(caravan.Tile, destinationTile, caravan.Faction ?? Faction.OfPlayer, checkDestination: true,
                () => { if (!caravan.Destroyed) { CaravanShuttleUtility.LaunchShuttle(caravan, destinationTile, arrivalAction); } });
        }
    }
}
