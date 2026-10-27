using RimWorld;
using Verse;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DMSE
{
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

        // ── 轉移飛行重構 ──
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
