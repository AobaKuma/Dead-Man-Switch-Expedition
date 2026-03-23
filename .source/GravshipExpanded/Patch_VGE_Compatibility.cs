using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using Verse;
using VanillaGravshipExpanded;

namespace DMSE.VGE
{
    [StaticConstructorOnStartup]
    public static class ModPatch
    {
        static ModPatch()
        {
            var harmony = new Harmony("DMSE.VGE");
            harmony.PatchAll();
        }
    }

    // VGE环境下拦截StartChoosingDestination_NewTemp
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class Patch_VGE_StartChoosingDestination_Interceptor
    {
        [ThreadStatic]
        private static bool _allowPass;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix(CompPilotConsole __instance)
        {
            // 如果是重入调用，放行
            if (_allowPass)
            {
                _allowPass = false;
                return true;
            }

            // 调试日志
            bool canTransfer = FlightUtility.CanTransfer(__instance);
            Log.Message($"[DMSE.VGE] StartChoosingDestination_NewTemp called. CanTransfer={canTransfer}");

            if (__instance.engine != null)
            {
                var facilities = __instance.engine.GetComp<CompAffectedByFacilities>()?.LinkedFacilitiesListForReading;
                if (facilities != null)
                {
                    var fusionCore = facilities.Find(thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0
                        && comp0.Props.componentTypeDef == DMSE_DefOf.DMSE_FusionCore);
                    var thruster = facilities.Find(thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0
                        && comp0.Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster);

                    Log.Message($"[DMSE.VGE] FusionCore={fusionCore != null}, Thruster={thruster != null}");
                }

                if (__instance.parent?.Map != null)
                {
                    var tile = __instance.parent.Map.Tile;
                    Log.Message($"[DMSE.VGE] Tile.Layer={tile.Layer}, Tile.LayerDef={tile.LayerDef?.defName}");
                    Log.Message($"[DMSE.VGE] Tile.LayerDef.isSpace={tile.LayerDef?.isSpace}");
                    Log.Message($"[DMSE.VGE] TotalFuel={__instance.engine.TotalFuel}");
                }
            }

            // 如果不具备转移能力，放行给原版/VGE
            if (!canTransfer)
            {
                return true;
            }

            // 具备转移能力，弹出模式选择
            Find.WindowStack.Add(new Dialog_SelectFlightMode(mode =>
            {
                Log.Message($"[DMSE.VGE] User selected mode: {mode}");

                if (mode == FlightMode.Standard)
                {
                    // 标准起飞：直接放行给 VGE 的正常流程
                    Log.Message("[DMSE.VGE] Standard mode - calling VGE StartChoosingDestination_NewTemp");
                    _allowPass = true;
                    __instance.StartChoosingDestination_NewTemp();
                }
                else
                {
                    // 转移/撞击飞行：清除 VGE state 并由 DMS 接管
                    Log.Message($"[DMSE.VGE] Transfer/Impact mode - clearing VGE state and calling DMSE StartTransferOrImpact with mode {mode}");
                    ClearVGEState();
                    FlightModeLauncher.StartTransferOrImpact(__instance, mode);
                }
            }, __instance)
            {
                // 窗口关闭时也清除 VGE state，防止残留
                closeAction = () => ClearVGEState()
            });

            return false;
        }

        // 清除 VGE 的状态，防止其补丁劫持 DMSE 的流程
        private static void ClearVGEState()
        {
            try
            {
                var stateField = AccessTools.Field(
                    AccessTools.TypeByName("VanillaGravshipExpanded.Dialog_BeginRitual_ShowRitualBeginWindow_Patch"),
                    "state");
                if (stateField != null)
                {
                    stateField.SetValue(null, null);
                    Log.Message("[DMSE.VGE] Cleared VGE state");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DMSE.VGE] Failed to clear VGE state: {ex}");
            }
        }
    }
}
