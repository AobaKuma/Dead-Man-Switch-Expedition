using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI.Group;

namespace DMSE
{
    [HarmonyPatch(typeof(SkyfallerMaker), nameof(SkyfallerMaker.SpawnSkyfaller),
        new Type[] { typeof(ThingDef), typeof(Thing), typeof(IntVec3), typeof(Map) })]
    public class Patch_MakeDropPodAt
    {

        [HarmonyPostfix]
        public static void postfix(Skyfaller __result, Thing innerThing, IntVec3 pos, Map map)
        {
            if (Prefs.DevMode) 
            {
                Log.Message("Generating SkyFaller：" + innerThing.Label);
            }
            if (EarlyWarningUtility.IsHostileDropThing(innerThing))
            {
                if (!EarlyWarningUtility.HasRadarBuildingOnMap(map))
                {
                    if (Prefs.DevMode)
                    {
                        Log.Message("No DMSE_InterceptRadar on map, skip intercept logic.");
                    }
                    return;
                }

                __result.DeSpawn();

                int delayTicks = EarlyWarningUtility.ResolveDelay(map);

                if (Prefs.DevMode)
                {
                    Log.Message("skyFaller delay：" + innerThing.Label + $" delayTicks:{delayTicks}");
                }

                InterceptSkyfallerUtility.Handle(map, __result, pos, delayTicks);
            } 
        }
    }
}