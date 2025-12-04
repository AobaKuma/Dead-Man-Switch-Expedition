using DMSE.SkyFallerTurret;
using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DMSE
{
    [HarmonyPatch(typeof(QuestPart_Bossgroup), "MakeLord")]
    public class Patch_QuestPart_Bossgroup
    {
        [HarmonyPostfix]
        public static void postfix(QuestPart_Bossgroup __instance, Lord __result)
        {
            Log.Message(__result);
            foreach (var item in __instance.bosses)
            {
                Log.Message(item.Label);
            }
            if (__instance.Map is Map map && map.GetComponent<MapComponent_InterceptSkyfaller>() is MapComponent_InterceptSkyfaller 
                comp && comp.Pods.Find(p => p.pods.Exists(p2 => IsOk(p2.pod,__instance.bosses))) is DroppodData data) 
            {
                Log.Message(__result);
                data.lord = __result;
            }
        }
        // 判定空投内部是否有符合的pawn
        public static bool IsOk(ThingOwner<Skyfaller> faller, List<Pawn> p) 
        {
            if (!faller.Any) 
            {
                return false;
            }
            var innerThing = faller.InnerListForReading.First().innerContainer.First();
            if (innerThing is Pawn pawn && p.Contains(pawn))
            {
                return true;
            }
            if (innerThing is ActiveTransporter transporter && transporter.Contents.SingleContainedThing
                is Pawn pawn2 && p.Contains(pawn2) )
            {
                return true;
            }
            return false;
        }
    } 
}