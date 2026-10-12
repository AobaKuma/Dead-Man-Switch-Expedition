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

        /// <summary>超視距火砲打擊的世界地圖航跡物件 def。</summary>
        public static WorldObjectDef DMSE_ArtilleryStrike;

        // ── 轉移飛行重構 ──
        public static TransferFlightTuningDef DMSE_TransferFlightTuning;
        public static JobDef DMSE_BuildPileBeam;
        public static JobDef DMSE_InitiateTransfer;
        public static WorldObjectDef DMSE_TransferShip;
        public static MapGeneratorDef DMSE_ShipVoid;

        // ── 太空平台拆除 ──
        public static DesignationDef DMSE_RemovePlatform;
        public static JobDef DMSE_RemovePlatformJob;
        public static TerrainAffordanceDef DMSE_Platform;
    }
}
