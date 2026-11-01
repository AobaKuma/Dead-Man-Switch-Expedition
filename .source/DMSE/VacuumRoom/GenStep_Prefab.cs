using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace DMSE
{
    public class GenStep_Prefab : GenStep
    {
        public override int SeedPart => 1145;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (this.terrain != null)
            {
                foreach (IntVec3 c in map.AllCells)
                {
                    map.terrainGrid.SetTerrain(c, this.terrain);
                }
            }
            PrefabUtility.SpawnPrefab(this.prefab,map,map.Center - new IntVec3(1,0,1),Rot4.North);
        }

        /// <summary>
        /// 把地圖上沒有地形（topGrid 為 null）的格子補上本 GenStep 的 <see cref="terrain"/>。
        /// 舊版的真空密封設施沒有地形步驟，存檔裡的口袋地圖整張都是 null；原版 TerrainAt 會退回 Soil 所以看不出來，
        /// 但直接讀 topGrid 的模組（ReGrowth Core、Simple FX: Splashes 的 SplashesUtility.RebuildCache）會 NRE。
        /// </summary>
        public static void FillMissingTerrain(Map map)
        {
            List<GenStepDef> steps = map.generatorDef?.genSteps;
            if (steps == null) { return; }
            TerrainDef terrain = steps.Select(s => (s.genStep as GenStep_Prefab)?.terrain).FirstOrDefault(t => t != null);
            if (terrain == null) { return; }
            TerrainDef[] topGrid = map.terrainGrid.topGrid;
            for (int i = 0; i < topGrid.Length; i++)
            {
                if (topGrid[i] == null) { topGrid[i] = terrain; }
            }
        }

        public PrefabDef prefab;

        /// <summary>生成前先把整張地圖鋪成此地形；null 則不動地形。</summary>
        public TerrainDef terrain;
    }

    /// <summary>
    /// 在路徑、區域與其他模組的 FinalizeInit postfix 之前補齊地形，讀檔時也會執行，
    /// 所以已經存在存檔裡的舊真空密封設施一併修好。
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    public static class Patch_Map_FinalizeInit_PrefabTerrain
    {
        [HarmonyPrefix]
        public static void Prefix(Map __instance)
        {
            GenStep_Prefab.FillMissingTerrain(__instance);
        }
    }
}
