using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 太空平台邊緣貼圖的接縫修正。原版 SectionLayer_TerrainEdges 以完整 0~1 UV 逐格貼邊緣貼圖，
    /// 原版貼圖（asset bundle）不會在邊界混色；mod 的 PNG 由 ModContentLoader 載入時維持 Unity 預設的 Repeat 環繞，
    /// 雙線性 / 三線性取樣在 u=0、u=1 會混入另一側的像素——例如 Edge_Left 最左欄透明、最右欄不透明，
    /// 與 Edge_Right 相鄰處就出現一條半透明細縫。這裡把非官方地形的邊緣貼圖改為 Clamp。
    /// 主地板貼圖要跨格平鋪，不處理。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PlatformEdgeTextureFix
    {
        static PlatformEdgeTextureFix()
        {
            foreach (TerrainDef def in DefDatabase<TerrainDef>.AllDefsListForReading)
            {
                TerrainDef.SpaceEdgeGraphicData edge = def.spaceEdgeGraphicData;
                if (edge == null || def.modContentPack == null || def.modContentPack.IsOfficialMod)
                {
                    continue;
                }
                try
                {
                    foreach (SectionLayer_TerrainEdges.EdgeType type in Enum.GetValues(typeof(SectionLayer_TerrainEdges.EdgeType)))
                    {
                        int variants = type == SectionLayer_TerrainEdges.EdgeType.Loop ? (edge.LoopTexPaths?.Count ?? 0) : 1;
                        for (int i = 0; i < variants; i++)
                        {
                            Texture tex = edge.GetMaterial(def, type, i)?.mainTexture;
                            if (tex != null && tex.wrapMode != TextureWrapMode.Clamp)
                            {
                                tex.wrapMode = TextureWrapMode.Clamp;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[DMSE] Could not clamp space edge textures of " + def.defName + ": " + ex.Message);
                }
            }
        }
    }
}
