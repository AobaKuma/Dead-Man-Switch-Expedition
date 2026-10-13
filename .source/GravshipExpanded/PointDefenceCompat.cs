using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using VanillaGravshipExpanded;
using Verse;

namespace DMSE.VGE
{
    /// <summary>
    /// 標記 DMS/DMSE 砲塔已加入 VGE 近防體系（<see cref="CompPointDefence"/>，由 Patch_VGE_PointDefence.xml 掛上）。
    /// 只有帶此標記的砲塔會受下列補丁影響，VGE 自己的近防砲塔維持原樣。
    /// </summary>
    public class ModExtension_PointDefence : DefModExtension
    {
        /// <summary>玩家地圖上有運作中的戰爭電腦（VGE2）時，近防半徑的加成（VGE 原版 14.9 → 19.9 即 +5）。</summary>
        public float warcomputerRadiusBonus = 5f;
    }

    [StaticConstructorOnStartup]
    public static class PointDefenceCompat
    {
        /// <summary>VGE2 的戰爭電腦；未啟用 VGE2 時為 null。</summary>
        private static readonly ThingDef WarcomputerDef = DefDatabase<ThingDef>.GetNamedSilentFail("VGE_Warcomputer");

        static PointDefenceCompat()
        {
            new Harmony("Aoba.DeadManSwitch.Expedition.VGE").PatchAll(typeof(PointDefenceCompat).Assembly);
        }

        public static ModExtension_PointDefence Ext(CompPointDefence comp)
            => comp.parent?.def.GetModExtension<ModExtension_PointDefence>();

        /// <summary>帶標記、但由非玩家陣營持有：改用 VGE 敵方近防的規則。</summary>
        public static bool IsNonPlayerMarked(CompPointDefence comp)
            => !comp.Props.isEnemyPointDefense
               && comp.parent != null
               && comp.parent.Faction != Faction.OfPlayer
               && Ext(comp) != null;

        /// <summary>等同 VGE2 的 WarcomputerHelper.IsWarcomputerPresent，但不引用 VGE2 組件。</summary>
        public static bool IsWarcomputerPresent(Map map)
        {
            if (WarcomputerDef == null || map == null)
            {
                return false;
            }
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(WarcomputerDef))
            {
                CompGravshipFacility facility = b.GetComp<CompGravshipFacility>();
                if (facility != null && facility.CanBeActive)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>VGE 敵方近防的命中公式（CompPointDefence.InterceptChance 的 isEnemyPointDefense 分支）。</summary>
        public static float EnemyInterceptChance(Projectile projectile, CompProperties_PointDefence props)
        {
            float speed = projectile.def.projectile.speed;
            float chance = 0.735f * (float)Math.Exp(-Math.Max(0f, speed - 30f) / props.falloffScale);
            return Mathf.Clamp(chance, 0.05f, 0.98f);
        }
    }

    /// <summary>
    /// VGE2 有戰爭電腦時把所有玩家近防半徑直接設成 19.9；對帶標記的砲塔改為「原半徑 + 加成」，
    /// 避免守門人（24.9）等較大半徑被縮短。排在 VGE2 的 Postfix 之後執行並覆寫其結果。
    /// </summary>
    [HarmonyPatch(typeof(CompPointDefence), nameof(CompPointDefence.InterceptionRadius), MethodType.Getter)]
    [HarmonyPriority(Priority.Last)]
    public static class CompPointDefence_InterceptionRadius_Patch
    {
        public static void Postfix(CompPointDefence __instance, ref float __result)
        {
            ModExtension_PointDefence ext = PointDefenceCompat.Ext(__instance);
            if (ext == null)
            {
                return;
            }
            float radius = __instance.Props.interceptionRadius;
            Thing parent = __instance.parent;
            if (parent.Faction == Faction.OfPlayer && PointDefenceCompat.IsWarcomputerPresent(parent.MapHeld))
            {
                radius += ext.warcomputerRadiusBonus;
            }
            __result = radius;
        }
    }

    /// <summary>
    /// VGE 近防在非敵方模式下直接讀 CompRefuelable；若其他 mod 移除了燃料元件（例如 CE），跳過以免每次 tick 報錯。
    /// </summary>
    [HarmonyPatch(typeof(CompPointDefence), nameof(CompPointDefence.CompTickInterval))]
    public static class CompPointDefence_CompTickInterval_Patch
    {
        public static bool Prefix(CompPointDefence __instance, CompRefuelable ___refuelableComp)
        {
            return __instance.Props.isEnemyPointDefense || ___refuelableComp != null;
        }
    }

    /// <summary>非玩家持有的標記砲塔：使用 VGE 敵方近防的較低命中公式。</summary>
    [HarmonyPatch(typeof(CompPointDefence), "InterceptChance")]
    public static class CompPointDefence_InterceptChance_Patch
    {
        public static bool Prefix(CompPointDefence __instance, Thing target, ref bool __result)
        {
            if (!PointDefenceCompat.IsNonPlayerMarked(__instance))
            {
                return true;
            }
            __result = target is Projectile projectile
                       && Rand.Chance(PointDefenceCompat.EnemyInterceptChance(projectile, __instance.Props));
            return false;
        }
    }

    /// <summary>非玩家持有的標記砲塔：與 VGE 敵方近防一致，不攔截空投艙。</summary>
    [HarmonyPatch(typeof(CompPointDefence), "IsValidTransporter")]
    public static class CompPointDefence_IsValidTransporter_Patch
    {
        public static bool Prefix(CompPointDefence __instance, ref bool __result)
        {
            if (!PointDefenceCompat.IsNonPlayerMarked(__instance))
            {
                return true;
            }
            __result = false;
            return false;
        }
    }
}
