using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    //[HarmonyPatch(typeof(CompGravshipFacility), nameof(CompGravshipFacility.CanBeActive), MethodType.Getter)]
    //public static class Patch_CompGravshipFacility_CanBeActive
    //{
    //    [HarmonyPostfix]
    //    public static void Postfix(CompGravshipFacility __instance, ref bool __result)
    //    {
    //        if (!__result) return;

    //        if (!(__instance.Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster)) return;

    //        Map map = __instance.parent?.Map;
    //        if (map == null) return;

    //        MapComponent_Ship shipComp = map.GetComponent<MapComponent_Ship>();
    //        if (shipComp == null || shipComp.status != OrbitalTransferState.Working)
    //        {
    //            __result = false;
    //        }
    //    }
    //}
}
