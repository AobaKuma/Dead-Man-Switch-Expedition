using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.AnyPawnBlockingMapRemoval) ,MethodType.Getter)]
    public class Patch_ShouldBeRemoved
    {
        [HarmonyPostfix]
        public static void postfix(Map ___map, ref bool __result)
        {
            if(__result) return;
            if (GameComponent_MissileEngage.Comp.times.ContainsKey(___map))
            { 
                __result = true;
            } 
        }
    } 
}