using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 當導彈被裝填進發射器（CompRefuelable 消耗導彈物品）時，把該導彈的裝配設定複製到
    /// 發射器上的 CompMissileConfig，使設定能延續到發射與落點效果。
    /// </summary>
    [HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.Refuel), new[] { typeof(List<Thing>) })]
    public static class Patch_Refuel_CarryConfig
    {
        [HarmonyPrefix]
        public static void Prefix(CompRefuelable __instance, List<Thing> fuelThings)
        {
            CompMissileConfig dst = __instance.parent.TryGetComp<CompMissileConfig>();
            if (dst == null || fuelThings.NullOrEmpty()) { return; }

            // 由尾端起找（CompRefuelable.Refuel 從尾端 Pop 消耗），對應實際被裝填的那一枚。
            for (int i = fuelThings.Count - 1; i >= 0; i--)
            {
                CompMissileConfig src = fuelThings[i].TryGetComp<CompMissileConfig>();
                if (src != null && src.config != null)
                {
                    // 同步 config 與 pending，使接收端（發射器）不會被誤判為「需要裝配」。
                    dst.config = src.config.Clone();
                    dst.pending = src.config.Clone();
                    dst.delivered.Clear();
                    return;
                }
            }
        }
    }
}
