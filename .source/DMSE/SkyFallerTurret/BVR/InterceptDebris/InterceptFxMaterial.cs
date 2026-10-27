using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 讓攔截特效畫在戰爭迷霧之上（docs/攔截殘骸特效_實作與技術美術計畫.md §9 P6，使用者 2026-10-26 決定）。
    /// 掛在 FleckDef 上：啟動時把它的材質 render queue 設成「迷霧 + <see cref="renderQueueOffset"/>」。
    ///
    /// 實測（rw17，驗收 I5）：Transparent 2900、Mote 3000、光照遮罩 3100、MoteGlow 3151、迷霧 3175、地圖邊緣遮罩 4000。
    /// 所以 3176–3999 會蓋過迷霧、仍被地圖外的黑色遮罩裁掉。只能用在專屬貼圖上：MaterialPool 依貼圖＋shader 共用材質，
    /// 改原版貼圖的材質會讓原版 fleck 也穿透迷霧。
    /// </summary>
    public class InterceptFxMaterial : DefModExtension
    {
        /// <summary>高於迷霧的層數；同一 queue 內的前後由 altitudeLayerIncOffset 決定。</summary>
        public int renderQueueOffset = 1;
    }

    [StaticConstructorOnStartup]
    public static class InterceptFxMaterials
    {
        /// <summary>迷霧的 render queue（實測 3175）。</summary>
        public static readonly int FogQueue = MatBases.FogOfWar.renderQueue;

        /// <summary>原版地圖邊緣遮罩的 render queue（實測 4000）；我們的特效必須低於它，地圖外才會被遮住。</summary>
        public static readonly int ClipQueue = MapEdgeClipDrawer.ClipMat.renderQueue;

        private static readonly Dictionary<(Material, int, bool), Material> clones = new Dictionary<(Material, int, bool), Material>();

        static InterceptFxMaterials()
        {
            foreach (FleckDef def in DefDatabase<FleckDef>.AllDefsListForReading)
            {
                InterceptFxMaterial ext = def.GetModExtension<InterceptFxMaterial>();
                if (ext == null) { continue; }
                int queue = QueueFor(ext.renderQueueOffset);
                if (def.graphicData != null) { def.graphicData.Graphic.MatSingle.renderQueue = queue; }
                if (def.randomGraphics != null)
                {
                    foreach (GraphicData g in def.randomGraphics) { g.Graphic.MatSingle.renderQueue = queue; }
                }
            }
        }

        public static int QueueFor(int offset) => Mathf.Min(FogQueue + Mathf.Max(1, offset), ClipQueue - 1);

        /// <summary>
        /// 主殘骸用：複製一份材質並調高 render queue（快取）。斷裂彈體畫的是導彈本身的貼圖，原材質導彈還在用，所以一定要複製。
        /// <paramref name="transparent"/>：改用 Transparent shader，顏色的透明度才會半透明（遠距殘骸淡入；Cutout 只有全有或全無）。
        /// </summary>
        public static Material AboveFog(Material source, int offset, bool transparent = false)
        {
            if (source == null) { return null; }
            if (!clones.TryGetValue((source, offset, transparent), out Material clone))
            {
                clone = new Material(source);
                // 換 shader 會把 render queue 重設成 shader 的預設，所以先換再設。
                if (transparent) { clone.shader = ShaderDatabase.Transparent; }
                clone.renderQueue = QueueFor(offset);
                clones[(source, offset, transparent)] = clone;
            }
            return clone;
        }
    }
}
