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
using static UnityEngine.Networking.UnityWebRequest;

namespace DMSE
{
    [HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.HiddenByRules))]
    public class Patch_Hide
    {
        [HarmonyPostfix]
        public static void postfix(WorldObject wo, ref bool __result)
        {
            if (Patch_Visible.WO.Any() && Patch_Visible.WO.Exists(w => w.worldObjec == wo))
            {
                __result = true;
            }
        }
    }
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ExpandingIconColor), MethodType.Getter)]
    public class Patch_ExpandingMaterial
    {
        [HarmonyPostfix]
        public static void postfix(WorldObject __instance, ref Color __result)
        {
            if (Patch_Visible.WO.Any() && Patch_Visible.WO.Exists(w => w.worldObjec == __instance))
            {
                __result.a = 0f;
            }
        }
    }
    public class Patch_Visible
    {

        public static List<WorldObject_Transfer> WO = new List<WorldObject_Transfer>();
    }
    [HarmonyPatch(typeof(WorldSelector), nameof(WorldSelector.Select))]
    public class Patch_Selectable
    {
        [HarmonyPrefix]
        public static bool prefix(WorldObject obj)
        {
            if (Patch_Visible.WO.Any() && Patch_Visible.WO.Exists(w => w.worldObjec == obj))
            {
                Find.WorldSelector.SelectedTile = (obj.Tile);
                return false;
            }
            return true;
        }
    }
}