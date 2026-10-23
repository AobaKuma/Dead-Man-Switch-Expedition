using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    // ──────────────────────────────────────────────────────────────────────────
    // 防空封鎖的航行管制（docs/NPC防空警戒五階段_執行計畫.md §6.2）。
    // 取代舊的「導彈陣地 5 格固定封鎖」：只有第五階段且具執行能力的據點才封鎖，
    // 依外交關係判定通行資格，並檢查整段航線。預覽可看、提交前（扣燃料之前）再查一次。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 原版穿梭機與運輸艙：已在封鎖圈內者不能起飛。CanLaunch 是回傳 AcceptanceReport 的方法；
    /// 穿梭機的 CompShuttle.CanLaunch 只由這裡的結尾呼叫，所以這個 postfix 已涵蓋穿梭機。
    /// 車隊攜帶的穿梭機（<see cref="CaravanShuttleUtility"/>）也走這裡，用 <see cref="Thing.Tile"/> 取車隊位置。
    /// </summary>
    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.CanLaunch))]
    public static class Patch_Airspace_LaunchableCanLaunch
    {
        [HarmonyPostfix]
        public static void Postfix(CompLaunchable __instance, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || __instance.parent == null) { return; }
            PlanetTile tile = __instance.parent.Tile;
            if (!tile.Valid) { return; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckPoint(__instance.parent.Faction ?? Faction.OfPlayer, tile);
            if (restriction.Restricted) { __result = restriction.Reason; }
        }
    }

    /// <summary>選定目的地時：航線穿越封鎖圈就不可提交（含起降點與過境）。</summary>
    [HarmonyPatch(typeof(CompLaunchable), "ChoseWorldTarget", new[]
    {
        typeof(GlobalTargetInfo), typeof(PlanetTile), typeof(IEnumerable<IThingHolder>), typeof(int),
        typeof(Action<PlanetTile, TransportersArrivalAction>), typeof(CompLaunchable), typeof(float?)
    })]
    public static class Patch_Airspace_ChoseWorldTarget
    {
        [HarmonyPrefix]
        public static bool Prefix(GlobalTargetInfo target, PlanetTile tile, CompLaunchable launchable, ref bool __result)
        {
            if (!target.IsValid) { return true; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckRoute(launchable?.parent?.Faction ?? Faction.OfPlayer, tile, target.Tile);
            if (!restriction.Restricted) { return true; }
            AirspaceRestrictionUtility.Reject(restriction);
            __result = false;
            return false;
        }
    }

    /// <summary>選點預覽：滑鼠標籤顯示航線受封鎖與封鎖者。</summary>
    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.TargetingLabelGetter))]
    public static class Patch_Airspace_TargetingLabel
    {
        [HarmonyPostfix]
        public static void Postfix(GlobalTargetInfo target, PlanetTile tile, CompLaunchable launchable, ref TaggedString __result)
        {
            if (!target.IsValid) { return; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckRoute(launchable?.parent?.Faction ?? Faction.OfPlayer, tile, target.Tile);
            if (!restriction.Restricted) { return; }
            GUI.color = ColorLibrary.RedReadable;
            __result = restriction.Reason;
        }
    }

    /// <summary>真正起飛（扣燃料、轉移乘員）前再查一次；封鎖狀態改變就取消提交並完整保留資源。</summary>
    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.TryLaunch))]
    public static class Patch_Airspace_TryLaunch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(CompLaunchable __instance, PlanetTile destinationTile)
        {
            Thing parent = __instance.parent;
            if (parent == null || !parent.Spawned) { return true; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckRoute(parent.Faction ?? Faction.OfPlayer, parent.Map.Tile, destinationTile);
            if (!restriction.Restricted) { return true; }
            AirspaceRestrictionUtility.Reject(restriction);
            return false;
        }
    }

    /// <summary>車隊穿梭機起飛前最終檢查（扣燃料之前）。</summary>
    [HarmonyPatch(typeof(CaravanShuttleUtility), nameof(CaravanShuttleUtility.LaunchShuttle))]
    public static class Patch_Airspace_CaravanShuttle
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(Caravan caravan, PlanetTile destinationTile)
        {
            if (caravan == null) { return true; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckRoute(caravan.Faction ?? Faction.OfPlayer, caravan.Tile, destinationTile);
            if (!restriction.Restricted) { return true; }
            AirspaceRestrictionUtility.Reject(restriction);
            return false;
        }
    }

    /// <summary>
    /// 重力船：<c>StartChoosingDestination_NewTemp(launching)</c> 同時服務航程預覽與起飛選點。
    /// 預覽（launching=false）一律允許；起飛時起點在封鎖圈內就拒絕。
    /// </summary>
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class Patch_Airspace_GravshipChoose
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(CompPilotConsole __instance, bool launching)
        {
            if (!launching) { return true; }
            Map map = __instance.parent?.Map;
            if (map == null) { return true; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckPoint(Faction.OfPlayer, map.Tile);
            if (!restriction.Restricted) { return true; }
            AirspaceRestrictionUtility.Reject(restriction);
            return false;
        }
    }

    /// <summary>
    /// 重力船選定目的地後、扣燃料與起飛之前（原版在 CheckConfirmSettle 的確認動作裡才扣燃料）：
    /// 航線受封鎖就拒絕並回到選點（呼叫原本的取消動作）。
    /// </summary>
    [HarmonyPatch(typeof(SettlementProximityGoodwillUtility), nameof(SettlementProximityGoodwillUtility.CheckConfirmSettle))]
    public static class Patch_Airspace_GravshipConfirm
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(PlanetTile tile, Action cancelAction, Building_GravEngine gravEngine)
        {
            Map map = gravEngine?.Map;
            if (map == null) { return true; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckRoute(gravEngine.Faction ?? Faction.OfPlayer, map.Tile, tile);
            if (!restriction.Restricted) { return true; }
            AirspaceRestrictionUtility.Reject(restriction);
            cancelAction?.Invoke();
            return false;
        }
    }

    /// <summary>
    /// 系統生成的 NPC 空投：落點在不允許該陣營通行的封鎖圈內時，這個空投入場方式不可用，
    /// 襲擊改用其他入場方式（例如步行進入）。強制指定入場方式的任務不經過這裡（相容例外，見計畫 §6.2）。
    /// </summary>
    [HarmonyPatch(typeof(PawnsArrivalModeWorker), nameof(PawnsArrivalModeWorker.CanUseWith))]
    public static class Patch_Airspace_ArrivalMode
    {
        [HarmonyPostfix]
        public static void Postfix(PawnsArrivalModeWorker __instance, IncidentParms parms, ref bool __result)
        {
            if (!__result || parms == null || !(parms.target is Map map) || !IsAirborne(__instance)) { return; }
            Faction faction = parms.faction;
            if (faction == null || faction.IsPlayer) { return; }
            if (AirspaceRestrictionUtility.CheckPoint(faction, map.Tile).Restricted) { __result = false; }
        }

        private static bool IsAirborne(PawnsArrivalModeWorker worker)
            => worker is PawnsArrivalModeWorker_CenterDrop || worker is PawnsArrivalModeWorker_ClusterDrop
               || worker is PawnsArrivalModeWorker_EdgeDrop || worker is PawnsArrivalModeWorker_EdgeDropGroups
               || worker is PawnsArrivalModeWorker_RandomDrop || worker is PawnsArrivalModeWorker_SpecificLocationDrop;
    }
}
