using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>標記可由 <see cref="SectionLayer_PlatformHull"/> 補斜角的船體牆。</summary>
    public class AngledHullExtension : DefModExtension
    {
    }

    /// <summary>
    /// 平台船體的斜角結構補全，照原版 SectionLayer_GravshipHull 的做法：在空的缺口格上，
    /// 以原版的斜角船體貼圖（2×2 格）蓋過相鄰兩段船體的端點。原版只認 ThingDefOf.GravshipHull，所以另寫一層。
    /// - 斜接（兩段船體只以斜角相接）：兩側缺口格都補，內外兩面都成為斜牆。
    /// - 直角 L 形轉角：比照原版只在基礎結構外補，這裡改為只在平台外（虛空）的缺口格補，房間內角維持直角。
    /// 只影響繪製：房間與氣密以正交相鄰判定，斜角相接的船體本來就是封閉的。
    /// </summary>
    public class SectionLayer_PlatformHull : SectionLayer
    {
        private enum CornerType
        {
            None,
            Corner_NW,
            Corner_NE,
            Corner_SW,
            Corner_SE,
            Diagonal_NW,
            Diagonal_NE,
            Diagonal_SW,
            Diagonal_SE
        }

        private static readonly Vector2[] UVs =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };

        private const float CornerScale = 2f;

        private const string TexPathPrefix = "Things/Building/Linked/GravshipHull/";

        private static readonly string[] TexPaths =
        {
            null,
            TexPathPrefix + "AngledGravshipHull_northwest",
            TexPathPrefix + "AngledGravshipHull_northeast",
            TexPathPrefix + "AngledGravshipHull_southwest",
            TexPathPrefix + "AngledGravshipHull_southeast",
            TexPathPrefix + "AngledGravshipHull_Partial_northwest",
            TexPathPrefix + "AngledGravshipHull_Partial_northeast",
            TexPathPrefix + "AngledGravshipHull_Partial_southwest",
            TexPathPrefix + "AngledGravshipHull_Partial_southeast"
        };

        private static Material[] mats;

        private static readonly float CornerAltitude = AltitudeLayer.BuildingOnTop.AltitudeFor();

        // 與原版相同的順序：N, E, S, W, NW, NE, SE, SW
        private static readonly IntVec3[] Directions =
        {
            IntVec3.North,
            IntVec3.East,
            IntVec3.South,
            IntVec3.West,
            IntVec3.North + IntVec3.West,
            IntVec3.North + IntVec3.East,
            IntVec3.South + IntVec3.East,
            IntVec3.South + IntVec3.West
        };

        private static readonly int[] ColorSourceOrder = { 0, 2, 1, 3 };

        private static readonly bool[] tmpChecks = new bool[Directions.Length];

        public override bool Visible => ModsConfig.OdysseyActive;

        public SectionLayer_PlatformHull(Section section)
            : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Roofs;
        }

        public override void Regenerate()
        {
            if (!ModsConfig.OdysseyActive)
            {
                return;
            }
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            foreach (IntVec3 cell in section.CellRect)
            {
                if (ShouldDrawCornerPiece(cell, map, out CornerType cornerType, out Color color))
                {
                    AddQuad(GetMaterial(cornerType), cell + GetOffset(cornerType), color, IsCornerIndoorMasked(cell, cornerType, map));
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        private static Material GetMaterial(CornerType cornerType)
        {
            if (mats == null)
            {
                mats = new Material[TexPaths.Length];
                for (int i = 1; i < TexPaths.Length; i++)
                {
                    mats[i] = MaterialPool.MatFrom(TexPaths[i], ShaderDatabase.CutoutOverlay);
                }
            }
            return mats[(int)cornerType];
        }

        // 2×2 貼圖的左下角相對缺口格的位置
        private static IntVec3 GetOffset(CornerType cornerType)
        {
            switch (cornerType)
            {
                case CornerType.Corner_NW:
                case CornerType.Diagonal_NW:
                    return new IntVec3(-1, 0, 0);
                case CornerType.Corner_SE:
                case CornerType.Diagonal_SE:
                    return new IntVec3(0, 0, -1);
                case CornerType.Corner_SW:
                case CornerType.Diagonal_SW:
                    return new IntVec3(-1, 0, -1);
                default:
                    return IntVec3.Zero;
            }
        }

        private static bool IsHull(IntVec3 c, Map map)
        {
            Building edifice = c.GetEdificeSafe(map);
            return edifice != null && edifice.def.HasModExtension<AngledHullExtension>();
        }

        private static bool IsPlatform(IntVec3 c, Map map)
        {
            return map.terrainGrid.BaseTerrainAt(c)?.affordances?.Contains(DMSE_DefOf.DMSE_Platform) ?? false;
        }

        private static bool ShouldDrawCornerPiece(IntVec3 pos, Map map, out CornerType cornerType, out Color color)
        {
            cornerType = CornerType.None;
            color = Color.white;
            if (pos.GetEdifice(map) != null)
            {
                return false;
            }
            for (int i = 0; i < Directions.Length; i++)
            {
                tmpChecks[i] = IsHull(pos + Directions[i], map);
            }
            if (tmpChecks[0] && tmpChecks[3] && !tmpChecks[2] && !tmpChecks[1])
            {
                cornerType = tmpChecks[4] ? CornerType.Corner_NW : CornerType.Diagonal_NW;
            }
            else if (tmpChecks[0] && tmpChecks[1] && !tmpChecks[2] && !tmpChecks[3])
            {
                cornerType = tmpChecks[5] ? CornerType.Corner_NE : CornerType.Diagonal_NE;
            }
            else if (tmpChecks[2] && tmpChecks[1] && !tmpChecks[0] && !tmpChecks[3])
            {
                cornerType = tmpChecks[6] ? CornerType.Corner_SE : CornerType.Diagonal_SE;
            }
            else if (tmpChecks[2] && tmpChecks[3] && !tmpChecks[0] && !tmpChecks[1])
            {
                cornerType = tmpChecks[7] ? CornerType.Corner_SW : CornerType.Diagonal_SW;
            }
            if (cornerType == CornerType.None)
            {
                return false;
            }
            if (cornerType < CornerType.Diagonal_NW && IsPlatform(pos, map))
            {
                return false;
            }
            // 顏色取自相鄰船體（材料染色），順序同原版：先南北、再東西
            foreach (int i in ColorSourceOrder)
            {
                if (tmpChecks[i])
                {
                    color = (pos + Directions[i]).GetEdificeSafe(map).DrawColor;
                    break;
                }
            }
            return true;
        }

        private static bool IsCornerIndoorMasked(IntVec3 c, CornerType cornerType, Map map)
        {
            switch (cornerType)
            {
                case CornerType.Corner_NE:
                case CornerType.Diagonal_NE:
                    return (c + IntVec3.North).Roofed(map) || (c + IntVec3.East).Roofed(map);
                case CornerType.Corner_NW:
                case CornerType.Diagonal_NW:
                    return (c + IntVec3.North).Roofed(map) || (c + IntVec3.West).Roofed(map);
                case CornerType.Corner_SE:
                case CornerType.Diagonal_SE:
                    return (c + IntVec3.South).Roofed(map) || (c + IntVec3.East).Roofed(map);
                case CornerType.Corner_SW:
                case CornerType.Diagonal_SW:
                    return (c + IntVec3.South).Roofed(map) || (c + IntVec3.West).Roofed(map);
                default:
                    return false;
            }
        }

        private void AddQuad(Material mat, IntVec3 c, Color color, bool addIndoorMask)
        {
            AddQuad(GetSubMesh(mat), c.ToVector3(), color);
            if (addIndoorMask)
            {
                Material indoorMat = MaterialPool.MatFrom(mat.mainTexture as Texture2D, ShaderDatabase.IndoorMaskMasked, mat.color);
                AddQuad(GetSubMesh(indoorMat), c.ToVector3(), color);
            }
        }

        private static void AddQuad(LayerSubMesh sm, Vector3 c, Color color)
        {
            int count = sm.verts.Count;
            for (int i = 0; i < 4; i++)
            {
                sm.verts.Add(new Vector3(c.x + UVs[i].x * CornerScale, CornerAltitude, c.z + UVs[i].y * CornerScale));
                sm.uvs.Add(UVs[i]);
                sm.colors.Add(color);
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
