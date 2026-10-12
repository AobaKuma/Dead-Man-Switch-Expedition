using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 地板下鋼樑（設計稿 §3-9）。畫在 <see cref="AltitudeLayer.BelowTerrain"/>、renderQueue 低於地形：
    /// 有地板處被蓋住，虛空格（Space 地形不渲染）處透出，呈現船體骨架伸出小行星/平台的效果。
    ///
    /// 採用 1.6 的整圖 <see cref="MapDrawLayer"/>（非 SectionLayer）：長樑不必依 section 切段，
    /// 也不佔用 MapMeshFlagDef 的 64 個位元。<see cref="MapDrawLayer.relevantChangeTypes"/> 保持 0
    /// ——原版 MapDrawer 不會清除 globalDirtyFlags，掛旗標會導致每幀重建——改由
    /// <see cref="SetDirty"/> 直接標記，<see cref="MapDrawer.DrawMapMesh"/> 會在下一幀重建。
    /// </summary>
    public class MapDrawLayer_StructuralBeams : MapDrawLayer
    {
        /// <summary>樑段長度上限（格）；長樑切成數段以免貼圖被過度拉伸。</summary>
        private const float DrawScale = 2f;

        private const float SegmentLength = 1f * DrawScale;

        // 純視覺位移：向畫面下方（南方）移動兩格，不改變樁位置或連結距離。
        private static readonly Vector3 DrawOffset = new Vector3(0f, 0f, -3f);

        private const float EndAltitudeOffset = 0.003f;

        private const float CenterAltitudeOffset = 0.006f;

        public MapDrawLayer_StructuralBeams(Map map) : base(map)
        {
        }

        public static void SetDirty(Map map)
        {
            if (map?.mapDrawer?.GlobalLayers == null)
            {
                // 地圖尚未完成初始化；首次 RegenerateEverythingNow 會自行建立
                return;
            }
            MapDrawLayer_StructuralBeams layer = map.mapDrawer.GetGlobalLayer<MapDrawLayer_StructuralBeams>();
            if (layer != null)
            {
                layer.Dirty = true;
            }
        }

        public override void Regenerate()
        {
            Dirty = false;
            ClearSubMeshes(MeshParts.All);
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(Map);
            if (comp == null)
            {
                return;
            }
            float alt = AltitudeLayer.BelowTerrain.AltitudeFor();
            List<PileNetwork> networks = comp.Networks;
            for (int n = 0; n < networks.Count; n++)
            {
                PileNetwork net = networks[n];
                for (int i = 0; i < net.links.Count; i++)
                {
                    PrintBeam(net.links[i].a, net.links[i].b, alt);
                }
                for (int i = 0; i < net.piles.Count; i++)
                {
                    CompStructuralPile p = net.piles[i];
                    float size = p.Props.beamCenterDrawSize * DrawScale;
                    Printer_Plane.PrintPlane(this, (p.parent.TrueCenter() + DrawOffset).WithY(alt + CenterAltitudeOffset), new Vector2(size, size), p.Props.BeamCenterMat);
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        private void PrintBeam(CompStructuralPile a, CompStructuralPile b, float alt)
        {
            Vector3 from = (a.parent.TrueCenter() + DrawOffset).WithY(alt);
            Vector3 to = (b.parent.TrueCenter() + DrawOffset).WithY(alt);
            Vector3 delta = to - from;
            float length = delta.MagnitudeHorizontal();
            if (length < 0.01f)
            {
                return;
            }
            Vector3 dir = delta / length;
            // AngleFlat：北 = 0、順時針；與 PrintPlane 的旋轉方向一致，貼圖縱軸對齊樑的方向
            float angle = delta.AngleFlat();
            CompProperties_StructuralPile props = a.Props;

            int segments = Mathf.Max(1, Mathf.CeilToInt(length / SegmentLength));
            float segLength = length / segments;
            Vector2 segSize = new Vector2(props.beamWidth * DrawScale, segLength);
            for (int i = 0; i < segments; i++)
            {
                Vector3 mid = from + dir * (segLength * (i + 0.5f));
                Printer_Plane.PrintPlane(this, mid, segSize, props.BeamSectionMat, angle);
            }

            // 端蓋：畫在樑穿出兩端樁佔地邊緣處
            Vector2 endSize = new Vector2(props.beamWidth * DrawScale, DrawScale);
            Printer_Plane.PrintPlane(this, EdgePoint(a.parent, from, dir).WithY(alt + EndAltitudeOffset), endSize, props.BeamEndMat, angle);
            Printer_Plane.PrintPlane(this, EdgePoint(b.parent, to, -dir).WithY(alt + EndAltitudeOffset), endSize, b.Props.BeamEndMat, angle + 180f);
        }

        /// <summary>從樁中心沿 dir 方向射出、離開其佔地矩形的位置。</summary>
        private static Vector3 EdgePoint(Thing pile, Vector3 center, Vector3 dir)
        {
            float halfX = pile.def.size.x * 0.5f;
            float halfZ = pile.def.size.z * 0.5f;
            float tx = Mathf.Abs(dir.x) > 0.0001f ? halfX / Mathf.Abs(dir.x) : float.MaxValue;
            float tz = Mathf.Abs(dir.z) > 0.0001f ? halfZ / Mathf.Abs(dir.z) : float.MaxValue;
            return center + dir * Mathf.Min(tx, tz);
        }
    }
}
