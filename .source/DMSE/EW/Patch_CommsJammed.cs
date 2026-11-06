using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 通訊封鎖（§3.5）：本圖有運作中且非待機的干擾機時通訊台停用，不分陣營、不做分級豁免。
    /// 只 patch 原版屬性；不動 DMS Core／VGE 的通訊程式。
    /// </summary>
    [HarmonyPatch(typeof(Building_CommsConsole), nameof(Building_CommsConsole.CanUseCommsNow), MethodType.Getter)]
    public static class Patch_CommsJammed
    {
        public static void Postfix(Building_CommsConsole __instance, ref bool __result)
        {
            if (__result && __instance.Spawned && MapComponent_EWControl.For(__instance.Map)?.CommsJammed == true)
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// 原版 GetFailureReason 只認識停電與太陽閃焰，其餘 CanUseCommsNow == false 會記 Log.Error，
    /// 所以先回傳干擾的理由。
    /// </summary>
    [HarmonyPatch(typeof(Building_CommsConsole), "GetFailureReason")]
    public static class Patch_CommsJammed_FailureReason
    {
        public static bool Prefix(Building_CommsConsole __instance, ref FloatMenuOption __result)
        {
            if (!__instance.Spawned || MapComponent_EWControl.For(__instance.Map)?.CommsJammed != true) { return true; }
            __result = new FloatMenuOption("CannotUseReason".Translate("DMSE.EW.Jammer.CommsBlockedReason".Translate()), null);
            return false;
        }
    }

    public class Alert_CommsJammed : Alert
    {
        public Alert_CommsJammed()
        {
            defaultLabel = "DMSE.EW.Alert.CommsJammed".Translate();
            defaultExplanation = "DMSE.EW.Alert.CommsJammedDesc".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override AlertReport GetReport()
        {
            foreach (Map map in Find.Maps)
            {
                MapComponent_EWControl control = MapComponent_EWControl.For(map);
                if (control == null || !map.IsPlayerHome || !control.CommsJammed) { continue; }
                if (!map.listerBuildings.allBuildingsColonist.Any(b => b is Building_CommsConsole)) { continue; }
                return AlertReport.CulpritsAre(control.jammers.Where(j => j.BlocksCommsNow).Select(j => (Thing)j.parent).ToList());
            }
            return AlertReport.Inactive;
        }
    }
}
