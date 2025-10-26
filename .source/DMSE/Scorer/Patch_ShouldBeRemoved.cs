using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Tilemaps;
using Verse;
using Verse.Sound;

namespace DMSE
{
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.AnyPawnBlockingMapRemoval) ,MethodType.Getter)]
    public class Patch_ShouldBeRemoved
    {
        [HarmonyPostfix]
        public static void postfix(Map ___map, ref bool __result)
        {
            if (GameComponent_MissileEngage.Comp.times.ContainsKey(___map))
            { 
                __result = true;
            } 
        }
    } 
}