using HarmonyLib;
using RimWorld.Planet;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    [HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.HiddenByRules))]
    public class Patch_Hide
    {
        [HarmonyPostfix]
        public static void Postfix(WorldObject wo, ref bool __result)
        {
            if (Patch_Visible.WO.Count > 0 && Patch_Visible.WO.Contains(wo))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ExpandingIconColor), MethodType.Getter)]
    public class Patch_ExpandingMaterial
    {
        [HarmonyPostfix]
        public static void Postfix(WorldObject __instance, ref Color __result)
        {
            if (Patch_Visible.WO.Count > 0 && Patch_Visible.WO.Contains(__instance))
            {
                __result.a = 0f;
            }
        }
    }

    public class Patch_Visible
    {
        public static List<WorldObject> WO = new List<WorldObject>();
    }

    [HarmonyPatch(typeof(WorldSelector), nameof(WorldSelector.Select))]
    public class Patch_Selectable
    {
        [HarmonyPrefix]
        public static bool Prefix(WorldObject obj)
        {
            if (Patch_Visible.WO.Count > 0 && Patch_Visible.WO.Contains(obj))
            {
                Find.WorldSelector.SelectedTile = obj.Tile;
                return false;
            }
            return true;
        }
    }
}