using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 穿梭機整合（設計稿 §8）：點火階段禁止從船上起飛；飛往移動中的船的穿梭機/運輸艙追蹤船的目前格子。
    /// 從飛行中的船起飛照常（航程以船的目前 Tile 計算），選單由 SpaceMapParent 提供，不需另外 patch。
    /// </summary>
    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.CanLaunch))]
    public static class Patch_CompLaunchable_CanLaunch_TransferBurn
    {
        [HarmonyPostfix]
        public static void Postfix(CompLaunchable __instance, ref AcceptanceReport __result)
        {
            if (!__result.Accepted)
            {
                return;
            }
            WorldObject_TransferShip ship = __instance.parent?.MapHeld?.Parent as WorldObject_TransferShip;
            if (ship != null && ship.IsBurning)
            {
                __result = "DMSE.Transfer.ShuttleBlockedBurn".Translate();
            }
        }
    }

    /// <summary>
    /// 原版 TransportersArrivalAction_LandInSpecificCell / _VisitSpace 的 StillValid 會比對
    /// mapParent.Tile 與 destinationTile，船移動後判定失效，抵達時的退路也是比對地圖 Tile。
    /// 每次推進前把 destinationTile 同步為船的目前格子（追蹤導航）；落點格屬船體地圖座標，船移動不影響。
    /// 點火階段的降落 v1 允許，避免無限盤旋。
    /// </summary>
    [HarmonyPatch(typeof(TravellingTransporters), "TickInterval")]
    public static class Patch_TravellingTransporters_HomeOnTransferShip
    {
        private static readonly AccessTools.FieldRef<TransportersArrivalAction_LandInSpecificCell, MapParent> LandParent =
            AccessTools.FieldRefAccess<TransportersArrivalAction_LandInSpecificCell, MapParent>("mapParent");

        private static readonly AccessTools.FieldRef<TransportersArrivalAction_VisitSpace, MapParent> VisitParent =
            AccessTools.FieldRefAccess<TransportersArrivalAction_VisitSpace, MapParent>("parent");

        [HarmonyPrefix]
        public static void Prefix(TravellingTransporters __instance)
        {
            WorldObject_TransferShip ship = TargetShip(__instance.arrivalAction);
            if (ship != null && ship.Spawned && ship.Tile.Valid && ship.Tile != __instance.destinationTile)
            {
                __instance.destinationTile = ship.Tile;
            }
        }

        private static WorldObject_TransferShip TargetShip(TransportersArrivalAction action)
        {
            if (action is TransportersArrivalAction_LandInSpecificCell land)
            {
                return LandParent(land) as WorldObject_TransferShip;
            }
            if (action is TransportersArrivalAction_VisitSpace visit)
            {
                return VisitParent(visit) as WorldObject_TransferShip;
            }
            if (action is TransportersArrivalAction_TransportShip transport)
            {
                return transport.mapParent as WorldObject_TransferShip;
            }
            return null;
        }
    }
}
