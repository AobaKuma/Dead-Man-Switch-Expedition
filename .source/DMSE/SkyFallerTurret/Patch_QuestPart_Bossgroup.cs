using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
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
            if (__instance.Map is Map map
                && map.GetComponent<MapComponent_BVRCombat>() is MapComponent_BVRCombat comp
                && comp.Waves.Find(w => w.targets.Exists(t => IsOk(t.skyfaller, __instance.bosses))) is BVRWave wave)
            {
                wave.lord = __result;
            }
        }

        // 判定目標內部是否有符合的 boss pawn。
        public static bool IsOk(ThingOwner<Skyfaller> faller, List<Pawn> p)
        {
            if (faller == null || !faller.Any)
            {
                return false;
            }
            Thing innerThing = faller.InnerListForReading.First().innerContainer.First();
            if (innerThing is Pawn pawn && p.Contains(pawn))
            {
                return true;
            }
            if (innerThing is ActiveTransporter transporter
                && transporter.Contents.SingleContainedThing is Pawn pawn2 && p.Contains(pawn2))
            {
                return true;
            }
            return false;
        }
    }
}
