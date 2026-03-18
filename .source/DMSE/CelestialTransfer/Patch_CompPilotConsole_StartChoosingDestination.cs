using HarmonyLib;
using RimWorld;
using System;
using Verse;

namespace DMSE
{
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class Patch_CompPilotConsole_StartChoosingDestination
    {
        [ThreadStatic]
        private static bool _reentering;

        [HarmonyPrefix]
        public static bool Prefix(CompPilotConsole __instance, bool launching)
        {
            if (_reentering)
            {
                _reentering = false;
                return true; 
            }
            if (!FlightModeLauncher.CanUse(__instance, launching))
            {
                return true;
            }
            FlightModeLauncher.ChooseAndStart(__instance, OnStandardChosen);
            return false;
        }
        private static void OnStandardChosen(CompPilotConsole console)
        {
            _reentering = true;
            console.StartChoosingDestination();
        }
    }
}