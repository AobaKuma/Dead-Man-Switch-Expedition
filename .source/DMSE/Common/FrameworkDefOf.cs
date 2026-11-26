using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 要搬進 FFF 的框架程式用到的 Def（從 <see cref="DMSE_DefOf"/> 拆出）。搬移時整個併進 FFF_DefOf，
    /// defName 依改名表從 DMSE_ 改成 FFF_（docs/轉移船與BVR移植FFF_可行性評估.md §7.4）。
    /// </summary>
    [StaticConstructorOnStartup]
    [DefOf]
    public static class DMSE_FrameworkDefOf
    {
        // ── 轉移飛行 ──
        public static TransferFlightTuningDef DMSE_TransferFlightTuning;
        public static JobDef DMSE_BuildPileBeam;
        public static JobDef DMSE_InitiateTransfer;
        public static WorldObjectDef DMSE_TransferShip;
        public static MapGeneratorDef DMSE_ShipVoid;
        public static GameConditionDef DMSE_TransferGrace;

        // ── 太空平台拆除 ──
        public static DesignationDef DMSE_RemovePlatform;
        public static JobDef DMSE_RemovePlatformJob;
        public static TerrainAffordanceDef DMSE_Platform;

        // ── 攔截空爆特效 ──
        public static FleckDef DMSE_InterceptFlash;
        public static FleckDef DMSE_InterceptFireball;
        public static FleckDef DMSE_InterceptFireGlow;
        public static FleckDef DMSE_InterceptSparkStar;
        public static FleckDef DMSE_InterceptSmokeBall;
        public static FleckDef DMSE_InterceptDebrisMinor;
        public static FleckDef DMSE_DebrisTrailPuff;
        public static ThingDef DMSE_InterceptDebrisChunk;
        public static ThingDef DMSE_Filth_MetalScrap;

        // ── 導彈發射特效（docs/導彈發射特效_重製計畫.md） ──
        public static FleckDef DMSE_LaunchSmokeLight;
        public static FleckDef DMSE_LaunchSmokeMid;
        public static FleckDef DMSE_LaunchSmokeDark;
        public static FleckDef DMSE_LaunchGroundCloud;
    }
}
