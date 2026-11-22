using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 讓設了 <see cref="TransferEnvironmentExtension.drawTerrainEdges"/> 的非太空圖層也畫地形崖壁（2026-11-22 從 DepthRim 移來）。
    ///
    /// <para>原版 <see cref="SectionLayer_TerrainEdges.Regenerate"/> 一開頭是
    /// <c>if (Map.Tile.Valid &amp;&amp; !Map.Tile.LayerDef.isSpace) return;</c>，崖壁只給太空地圖用。
    /// 這裡只把那一個 <c>isSpace</c> 欄位讀取換成 <see cref="TransferEnvironment.DrawsTerrainEdges"/>：
    /// 太空照舊，沒設欄位的圖層照舊不畫；崖壁的形狀、貼圖、材質全部沿用原版。</para>
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_TerrainEdges), nameof(SectionLayer_TerrainEdges.Regenerate))]
    public static class Patch_TerrainEdgesOnTransferLayers
    {
        private static readonly FieldInfo IsSpaceField = AccessTools.Field(typeof(PlanetLayerDef), nameof(PlanetLayerDef.isSpace));

        private static readonly MethodInfo Replacement = AccessTools.Method(typeof(TransferEnvironment), nameof(TransferEnvironment.DrawsTerrainEdges));

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool replaced = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!replaced && instruction.LoadsField(IsSpaceField))
                {
                    // 堆疊上原本是 PlanetLayerDef，ldfld isSpace 換成 call DrawsTerrainEdges(def)，結果一樣是 bool
                    replaced = true;
                    yield return new CodeInstruction(OpCodes.Call, Replacement).WithLabels(instruction.labels);
                    continue;
                }
                yield return instruction;
            }
            if (!replaced)
            {
                Log.Error("[DMSE] SectionLayer_TerrainEdges.Regenerate no longer reads PlanetLayerDef.isSpace; drawTerrainEdges on planet layers has no effect.");
            }
        }
    }
}
