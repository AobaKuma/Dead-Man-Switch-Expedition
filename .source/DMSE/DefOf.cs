using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// DMSE 內容的 Def。框架用的 Def 已在 2026-11 隨轉移船與飛彈體系搬進 FFF（Fortified.FFF_AerospaceDefOf、Fortified.FFF_MissileDefOf）。
    /// </summary>
    [StaticConstructorOnStartup]
    [DefOf]
    public static class DMSE_DefOf
    {
        public static FactionDef DMS_Army;
        public static ThingDef DMSE_VacuumPump;
        public static ThingDef DMSE_NuclearThruster;
        public static WorldObjectDef DMSE_TransferGravShip;
        public static GravshipComponentTypeDef DMSE_FusionCore;
        public static GravshipComponentTypeDef DMSE_TransferThruster;
        public static WorldObjectDef DMSE_ImpactGravship;

        public static BiomeDef DMSE_ImpactCraterBiome;
    }
}
