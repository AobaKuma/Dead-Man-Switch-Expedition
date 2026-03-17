using HarmonyLib;
using RimWorld;
using System;
using Verse;

namespace DMSE
{
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class Patch_CompPilotConsole_StartChoosingDestination
    {
        // 防止遞迴攔截：僅在我們主動再呼叫原方法時，放行一次
        [ThreadStatic]
        private static bool _reentering;

        [HarmonyPrefix]
        public static bool Prefix(CompPilotConsole __instance, bool launching)
        {
            // 若是由我們主動再進來的一層，放行並清旗標
            if (_reentering)
            {
                _reentering = false;
                return true; // 執行原始 StartChoosingDestination_NewTemp
            }

            // 若不需要自訂流程，讓原方法執行
            if (!FlightModeLauncher.CanUse(__instance, launching))
            {
                return true;
            }

            // 使用客製流程（會在內部必要時再呼叫原方法）
            FlightModeLauncher.ChooseAndStart(__instance, OnStandardChosen);
            return false; // 攔截原始方法
        }

        // 標準模式回調：開放下一次呼叫原方法
        private static void OnStandardChosen(CompPilotConsole console)
        {
            _reentering = true;
            console.StartChoosingDestination();
        }
    }
}