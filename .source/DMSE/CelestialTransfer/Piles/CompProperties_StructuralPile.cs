using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 結構樁設定（設計稿 §3-1）。
    /// </summary>
    public class CompProperties_StructuralPile : CompProperties
    {
        /// <summary>以中心格為準的覆蓋半徑（距離算法見 <see cref="Distance"/>）：方形時 6 → 13×13。</summary>
        public int coverageRadius = 6;

        /// <summary>
        /// 可連結的最大中心距離（距離算法見 <see cref="Distance"/>）；兩端不同時取較小者。
        /// 0 = 不提供連結範圍：只能建在提供範圍的樁的範圍內，也只能連到這類樁。
        /// </summary>
        public int linkRange = 13;

        /// <summary>覆蓋範圍與連結距離改用圓形（四捨五入的歐氏距離）；預設為方形（Chebyshev）。</summary>
        public bool circularRange = false;

        /// <summary>
        /// 非空時，只有基底地形（地板下的地形）在此清單內的格子算入本樁的船體範圍，樁本身也只能建在這些地形上。
        /// </summary>
        public List<TerrainDef> allowedTerrains;

        /// <summary>
        /// 基底地形在此清單內的格子不算入本樁的船體範圍，樁本身也不能建在這些地形上。
        /// 可與 <see cref="allowedTerrains"/> 並用：格子須在白名單內（有設定時）且不在黑名單內。
        /// </summary>
        public List<TerrainDef> excludedTerrains;

        /// <summary>只能建在太空地圖（小行星、軌道）上。</summary>
        public bool spaceMapOnly;

        /// <summary>每根樁最多連結數（含施工中）；0 = 不限。</summary>
        public int maxLinks = 4;

        /// <summary>燃料/質量權重；標準樁 = 1。</summary>
        public float massWeight = 1f;

        /// <summary>鋼樑每格長度的材料。</summary>
        public List<ThingDefCountClass> beamCostPerCell = new List<ThingDefCountClass>();

        /// <summary>鋼樑每格長度的施工工作量。</summary>
        public float beamWorkPerCell = 120f;

        public float beamDismantleWorkFactor = 0.5f;

        /// <summary>拆除鋼樑（或拆除樁時順帶拆掉鋼樑）返還材料的比例。</summary>
        public float beamDismantleRefund = 0.5f;

        // ── 地板下鋼樑（設計稿 §3-9）。Section 貼圖的樑沿貼圖縱軸（北南向）延伸。
        public string beamSectionTexPath = "Things/Building/StructuralBeam/Section";

        public string beamEndTexPath = "Things/Building/StructuralBeam/End";

        public string beamCenterTexPath = "Things/Building/StructuralBeam/Center";

        public float beamWidth = 1f;

        public float beamCenterDrawSize = 3f;

        /// <summary>
        /// 鋼樑材質的 renderQueue。地形為 2000 + renderPrecedence，低於 2000 才會被地板蓋住、只在虛空處透出。
        /// </summary>
        public int beamRenderQueue = 1990;

        [Unsaved(false)]
        private Material beamSectionMat;

        [Unsaved(false)]
        private Material beamEndMat;

        [Unsaved(false)]
        private Material beamCenterMat;

        public CompProperties_StructuralPile()
        {
            compClass = typeof(CompStructuralPile);
        }

        public Material BeamSectionMat => beamSectionMat ?? (beamSectionMat = MakeBeamMat(beamSectionTexPath, 0));

        public Material BeamEndMat => beamEndMat ?? (beamEndMat = MakeBeamMat(beamEndTexPath, 1));

        public Material BeamCenterMat => beamCenterMat ?? (beamCenterMat = MakeBeamMat(beamCenterTexPath, 2));

        public bool ProvidesLinkRange => linkRange > 0;

        public bool HasTerrainFilter => !allowedTerrains.NullOrEmpty() || !excludedTerrains.NullOrEmpty();

        /// <summary>本樁是否把此格算入船體（只看地形篩選；虛空另由 <see cref="PileNetwork.IsSolidCell"/> 判斷）。</summary>
        public bool AcceptsTerrainAt(Map map, IntVec3 c)
        {
            if (!HasTerrainFilter)
            {
                return true;
            }
            TerrainDef terrain = map.terrainGrid.BaseTerrainAt(c);
            if (!allowedTerrains.NullOrEmpty() && !allowedTerrains.Contains(terrain))
            {
                return false;
            }
            return excludedTerrains.NullOrEmpty() || !excludedTerrains.Contains(terrain);
        }

        /// <summary>
        /// 覆蓋與連結共用的格距：方形為 Chebyshev，圓形為四捨五入的歐氏距離
        /// （≤ r 即 dx² + dz² ≤ r² + r，四個端點不會只突出單一格）。圓形距離恆 ≥ Chebyshev。
        /// </summary>
        public int Distance(IntVec3 a, IntVec3 b)
        {
            int dx = a.x - b.x;
            int dz = a.z - b.z;
            if (!circularRange)
            {
                return Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
            }
            return Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dz * dz));
        }

        /// <summary>以 center 為中心格的覆蓋範圍（已裁切到地圖內）。</summary>
        public IEnumerable<IntVec3> CoverageCells(IntVec3 center, Map map)
        {
            return CellsWithin(center, coverageRadius, map);
        }

        /// <summary>以 center 為中心格的連結範圍（已裁切到地圖內），僅供顯示。</summary>
        public IEnumerable<IntVec3> LinkRangeCells(IntVec3 center, Map map)
        {
            return CellsWithin(center, linkRange, map);
        }

        private IEnumerable<IntVec3> CellsWithin(IntVec3 center, int radius, Map map)
        {
            foreach (IntVec3 c in CellRect.CenteredOn(center, radius).ClipInsideMap(map))
            {
                if (Distance(center, c) <= radius)
                {
                    yield return c;
                }
            }
        }

        private Material MakeBeamMat(string texPath, int queueOffset)
        {
            Texture2D tex = ContentFinder<Texture2D>.Get(texPath);
            MaterialRequest req = new MaterialRequest(tex, ShaderDatabase.Transparent)
            {
                // 同一佇列內的繪製順序不保證，因此以佇列區分：樑段 → 端蓋 → 節點
                renderQueue = beamRenderQueue + queueOffset
            };
            return MaterialPool.MatFrom(req);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }
            if (coverageRadius < 1)
            {
                yield return "coverageRadius must be at least 1.";
            }
            if (linkRange < 0)
            {
                yield return "linkRange must not be negative.";
            }
            if (beamWorkPerCell <= 0f)
            {
                yield return "beamWorkPerCell must be positive.";
            }
        }
    }
}
