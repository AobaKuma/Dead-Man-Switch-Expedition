using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 海上平台的邊框與側面貼圖（掛在平台的 TerrainDef 上），由 <see cref="SectionLayer_SeaPlatformProps"/> 繪製
    /// （從 DepthRim 的 HullPlatformPropsExtension 簡化：沒有浮在水面的乾舷、沒有海床）。
    /// 路徑的意義和原版 <c>spaceEdgeGraphicData</c> 相同：邊框畫在平台格上，側面（高兩格）畫在平台南邊的高空格上。
    /// </summary>
    public class SeaPlatformPropsExtension : DefModExtension
    {
        public string oShapeTexPath;
        public string uShapeTexPath;
        public string cornerInnerTexPath;
        public string cornerOuterTexPath;
        public string flatTexPath;

        /// <summary>側面：一段的左端、右端、單獨一格，以及中段的幾種變化。</summary>
        public string sideLeftTexPath;
        public string sideRightTexPath;
        public string sideSingleTexPath;
        public List<string> sideLoopTexPaths = new List<string>();

        [Unsaved(false)]
        private Dictionary<string, Material> materials;

        public Material MaterialFor(string texPath)
        {
            if (texPath.NullOrEmpty())
            {
                return null;
            }
            materials ??= new Dictionary<string, Material>();
            if (!materials.TryGetValue(texPath, out Material mat))
            {
                mat = MaterialPool.MatFrom(texPath, ShaderDatabase.Transparent);
                materials[texPath] = mat;
            }
            return mat;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            foreach (string p in new[] { oShapeTexPath, uShapeTexPath, cornerInnerTexPath, cornerOuterTexPath, flatTexPath, sideLeftTexPath, sideRightTexPath, sideSingleTexPath })
            {
                if (p.NullOrEmpty())
                {
                    yield return "a sea platform texture path is empty";
                }
            }
            if (sideLoopTexPaths.NullOrEmpty())
            {
                yield return "sideLoopTexPaths is empty";
            }
        }
    }

    /// <summary>
    /// 海上平台的邊框與下方側面（比照 Odyssey 的 <c>SectionLayer_SubstructureProps</c>）。
    /// 邊框只畫在平台格上，側面只畫進高空格（第二格不是高空時只畫上半），材質用原版 Transparent。
    /// 掛了 <see cref="SeaPlatformPropsExtension"/> 的地形在任何地圖都會畫，但側面只畫進不畫的地形（dontRender）。
    /// </summary>
    public class SectionLayer_SeaPlatformProps : SectionLayer
    {
        [Flags]
        private enum Dirs
        {
            None = 0,
            North = 1,
            East = 2,
            South = 4,
            West = 8
        }

        private enum Border
        {
            OShape,
            UShape,
            CornerOuter,
            Flat
        }

        private static readonly Vector2[] UVs =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };

        // 和原版 SectionLayer_TerrainEdges 相同的旋轉
        private static readonly Dictionary<Dirs, (Border, Rot4)[]> EdgePieces = new Dictionary<Dirs, (Border, Rot4)[]>
        {
            { Dirs.North, new[] { (Border.Flat, Rot4.South) } },
            { Dirs.East, new[] { (Border.Flat, Rot4.West) } },
            { Dirs.South, new[] { (Border.Flat, Rot4.North) } },
            { Dirs.West, new[] { (Border.Flat, Rot4.East) } },
            { Dirs.North | Dirs.East, new[] { (Border.CornerOuter, Rot4.West) } },
            { Dirs.East | Dirs.South, new[] { (Border.CornerOuter, Rot4.North) } },
            { Dirs.South | Dirs.West, new[] { (Border.CornerOuter, Rot4.East) } },
            { Dirs.North | Dirs.West, new[] { (Border.CornerOuter, Rot4.South) } },
            { Dirs.North | Dirs.South, new[] { (Border.Flat, Rot4.South), (Border.Flat, Rot4.North) } },
            { Dirs.East | Dirs.West, new[] { (Border.Flat, Rot4.West), (Border.Flat, Rot4.East) } },
            { Dirs.North | Dirs.East | Dirs.South, new[] { (Border.UShape, Rot4.West) } },
            { Dirs.East | Dirs.South | Dirs.West, new[] { (Border.UShape, Rot4.North) } },
            { Dirs.North | Dirs.South | Dirs.West, new[] { (Border.UShape, Rot4.East) } },
            { Dirs.North | Dirs.East | Dirs.West, new[] { (Border.UShape, Rot4.South) } },
            { Dirs.North | Dirs.East | Dirs.South | Dirs.West, new[] { (Border.OShape, Rot4.North) } },
        };

        public SectionLayer_SeaPlatformProps(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible => DebugViewSettings.drawTerrain;

        private static SeaPlatformPropsExtension PropsAt(IntVec3 c, Map map)
        {
            return c.InBounds(map) ? map.terrainGrid.BaseTerrainAt(c)?.GetModExtension<SeaPlatformPropsExtension>() : null;
        }

        private static bool IsOpen(IntVec3 c, Map map)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            return t == null || t.dontRender;
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            float borderAltitude = AltitudeLayer.TerrainScatter.AltitudeFor();
            float sideAltitude = AltitudeLayer.TerrainEdges.AltitudeFor();
            foreach (IntVec3 c in section.CellRect)
            {
                SeaPlatformPropsExtension props = PropsAt(c, map);
                if (props == null)
                {
                    continue;
                }
                Dirs edges = Dirs.None;
                for (int i = 0; i < 4; i++)
                {
                    if (PropsAt(c + GenAdj.CardinalDirections[i], map) == null)
                    {
                        edges |= (Dirs)(1 << i);
                    }
                }
                Dirs corners = Dirs.None; // 斜角：1 = 西南、2 = 西北、4 = 東北、8 = 東南
                for (int i = 0; i < 4; i++)
                {
                    if (PropsAt(c + GenAdj.DiagonalDirections[i], map) == null)
                    {
                        corners |= (Dirs)(1 << i);
                    }
                }
                DrawBorders(props, c, edges, corners, borderAltitude);
                if (edges.HasFlag(Dirs.South))
                {
                    DrawSide(props, c, map, sideAltitude);
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        private void DrawBorders(SeaPlatformPropsExtension props, IntVec3 c, Dirs edges, Dirs corners, float altitude)
        {
            if (EdgePieces.TryGetValue(edges, out (Border, Rot4)[] pieces))
            {
                foreach ((Border border, Rot4 rot) in pieces)
                {
                    AddQuad(props.MaterialFor(BorderPath(props, border)), c.x, c.z, 1f, 0f, 1f, altitude, rot);
                }
            }
            if (corners.HasFlag((Dirs)2) && !edges.HasFlag(Dirs.North) && !edges.HasFlag(Dirs.West))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.East);
            }
            if (corners.HasFlag((Dirs)4) && !edges.HasFlag(Dirs.North) && !edges.HasFlag(Dirs.East))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.South);
            }
            if (corners.HasFlag((Dirs)8) && !edges.HasFlag(Dirs.South) && !edges.HasFlag(Dirs.East))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.West);
            }
            if (corners.HasFlag((Dirs)1) && !edges.HasFlag(Dirs.South) && !edges.HasFlag(Dirs.West))
            {
                AddQuad(props.MaterialFor(props.cornerInnerTexPath), c.x, c.z, 1f, 0f, 1f, altitude, Rot4.North);
            }
        }

        private static string BorderPath(SeaPlatformPropsExtension props, Border border)
        {
            switch (border)
            {
                case Border.OShape: return props.oShapeTexPath;
                case Border.UShape: return props.uShapeTexPath;
                case Border.CornerOuter: return props.cornerOuterTexPath;
                default: return props.flatTexPath;
            }
        }

        /// <summary>平台格的南邊緣往下畫側面：南邊那格要是不畫的地形（高空）；第二格不是時只畫上半。</summary>
        private void DrawSide(SeaPlatformPropsExtension props, IntVec3 c, Map map, float altitude)
        {
            IntVec3 below = c + IntVec3.South;
            if (!IsOpen(below, map))
            {
                return;
            }
            float cells = IsOpen(below + IntVec3.South, map) ? 2f : 1f;
            bool west = PropsAt(c + IntVec3.West, map) != null;
            bool east = PropsAt(c + IntVec3.East, map) != null;
            string path;
            if (west && east)
            {
                path = props.sideLoopTexPaths[Mathf.Abs(c.GetHashCode()) % props.sideLoopTexPaths.Count];
            }
            else if (east)
            {
                path = props.sideLeftTexPath;
            }
            else if (west)
            {
                path = props.sideRightTexPath;
            }
            else
            {
                path = props.sideSingleTexPath;
            }
            // 貼圖高兩格、頂端貼著平台：畫兩格時用整張，只畫一格時只取上半
            AddQuad(props.MaterialFor(path), c.x, c.z - cells, cells, 1f - cells / 2f, 1f, altitude, Rot4.North);
        }

        private void AddQuad(Material mat, float x, float z, float height, float vMin, float vMax, float altitude, Rot4 rotation)
        {
            if (mat == null)
            {
                return;
            }
            LayerSubMesh sm = GetSubMesh(mat);
            int count = sm.verts.Count;
            int shift = Mathf.Abs(4 - rotation.AsInt);
            for (int i = 0; i < 4; i++)
            {
                sm.verts.Add(new Vector3(x + UVs[i].x, altitude, z + UVs[i].y * height));
                Vector2 uv = UVs[(shift + i) % 4];
                sm.uvs.Add(new Vector3(uv.x, Mathf.Lerp(vMin, vMax, uv.y), 0f));
            }
            sm.tris.Add(count);
            sm.tris.Add(count + 1);
            sm.tris.Add(count + 2);
            sm.tris.Add(count);
            sm.tris.Add(count + 2);
            sm.tris.Add(count + 3);
        }
    }
}
