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
        public static bool IsOk(ThingOwner<Thing> faller, List<Pawn> p)
        {
            if (faller == null || !faller.Any)
            {
                return false;
            }
            // 只有空投艙 skyfaller 有內容物；導彈 Projectile 沒有。
            if (!(faller.InnerListForReading.First() is Skyfaller pod)) { return false; }
            Thing innerThing = pod.innerContainer.FirstOrDefault();
            return BVRDetection.PawnsIn(innerThing).Any(p.Contains);
        }
    }
}
