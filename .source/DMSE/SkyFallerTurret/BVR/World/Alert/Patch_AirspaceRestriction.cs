using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    // ──────────────────────────────────────────────────────────────────────────
    // 防空封鎖的航行管制（docs/NPC防空警戒五階段_執行計畫.md §6.2）。
    // 2026-10-31 起封鎖改為主動攔截：航線可以穿越封鎖圈，飛進圈內的載具由
    // WorldComponent_BlockadeInterception 接戰。起飛前只警告（InterceptRiskUtility 的確認窗口），不再拒絕。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>選點預覽：滑鼠標籤顯示航線會穿越封鎖圈（會被攔截）與封鎖者。</summary>
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

    /// <summary>
    /// 重力船選定目的地後、扣燃料與起飛之前（原版在 CheckConfirmSettle 的確認動作裡才扣燃料）：
    /// 航線穿越封鎖圈就先彈出警告，確認後照原流程繼續，取消則回到選點。
    /// </summary>
    [HarmonyPatch(typeof(SettlementProximityGoodwillUtility), nameof(SettlementProximityGoodwillUtility.CheckConfirmSettle))]
    public static class Patch_Airspace_GravshipConfirm
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(PlanetTile tile, Action settleAction, Action cancelAction, Building_GravEngine gravEngine)
        {
            Map map = gravEngine?.Map;
            if (map == null) { return true; }
            return InterceptRiskUtility.ConfirmOrDefer(map.Tile, tile, gravEngine.Faction ?? Faction.OfPlayer, checkDestination: false,
                () => SettlementProximityGoodwillUtility.CheckConfirmSettle(tile, settleAction, cancelAction, gravEngine), cancelAction);
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
