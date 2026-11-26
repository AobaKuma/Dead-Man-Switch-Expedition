using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// DMSE 內容的 Def。框架（轉移船、平台拆除、攔截與發射特效）用的 Def 在 <see cref="DMSE_FrameworkDefOf"/>，
    /// 搬進 FFF 時整個併進 FFF_DefOf（docs/轉移船與BVR移植FFF_可行性評估.md §7.4）。
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
