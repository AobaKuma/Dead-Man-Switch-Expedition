using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 已完工連結構成的連通分量；一個網路＝一艘可移動的船（設計稿 §3-3、§3-6）。
    /// 由 <see cref="MapComponent_PileNetwork"/> 重建，連結或樁變動後整個物件會被丟棄。
    /// </summary>
    public class PileNetwork
    {
        public struct Link
        {
            public CompStructuralPile a;

            public CompStructuralPile b;

            public Link(CompStructuralPile a, CompStructuralPile b)
            {
                this.a = a;
                this.b = b;
            }
        }

        public readonly Map map;

        public readonly List<CompStructuralPile> piles = new List<CompStructuralPile>();

        /// <summary>已完工連結（去重），鋼樑渲染直接使用。</summary>
        public readonly List<Link> links = new List<Link>();

        /// <summary>Σ massWeight（需求 3）。</summary>
        public float massWeight;

        /// <summary>覆蓋聯集（含爭議格、含虛空）。</summary>
        public readonly HashSet<IntVec3> covered = new HashSet<IntVec3>();

        /// <summary>本網路中帶地形篩選的樁設定（去重）；索引即 <see cref="filteredCoverMask"/> 的位元。</summary>
        public readonly List<CompProperties_StructuralPile> filteredCoverProps = new List<CompProperties_StructuralPile>();

        /// <summary>
        /// 只被帶地形篩選的樁覆蓋的格子 → 覆蓋它的設定位元遮罩。不在此表內的覆蓋格至少有一根無篩選的樁覆蓋，地形一律接受。
        /// </summary>
        public readonly Dictionary<IntVec3, int> filteredCoverMask = new Dictionary<IntVec3, int>();

        /// <summary>與其他網路覆蓋重疊的格子：本網路起飛時會一併帶走。</summary>
        public readonly HashSet<IntVec3> contestedCells = new HashSet<IntVec3>();

        /// <summary>不在本網路、但位於本網路某根樁 linkRange 內的結構樁（設計稿 §3-3）。</summary>
        public readonly List<CompStructuralPile> nearbyUnlinkedPiles = new List<CompStructuralPile>();

        public readonly List<IntVec3> nearbyUnlinkedPileCells = new List<IntVec3>();

        // ── footprint 懶計算（地形變動不即時追蹤，過期後被查詢時重算）
        private HashSet<IntVec3> footprint;

        private readonly List<IntVec3> footprintCells = new List<IntVec3>();

        private readonly List<IntVec3> contestedFootprintCells = new List<IntVec3>();

        private readonly List<Thing> tornApartThings = new List<Thing>();

        private readonly List<IntVec3> tornApartCells = new List<IntVec3>();

        private int footprintTick = -1;

        // ── 地形連通（TopologyLab 增量演算法）：候選格的連通塊，覆蓋範圍不變時沿用，只餵地形變動
        private GridTopology topology;

        private CellRect topologyRect;

        private readonly List<int> topoAdds = new List<int>();

        private readonly List<int> topoRemoves = new List<int>();

        private readonly HashSet<int> anchorComponents = new HashSet<int>();

        // ── 推進/燃料勘查（短時間快取；ITab 每幀繪製時不必每幀重算）
        private const int SurveyCacheTicks = 60;

        private TransferShipSurvey survey;

        private int surveyTick = -1;

        public int lastOverlayFrame = -1;

        public int lastShipOverlayFrame = -1;

        public PileNetwork(Map map)
        {
            this.map = map;
        }

        /// <summary>
        /// 船體範圍：覆蓋聯集 ∩ 非虛空格 ∩ 至少一根覆蓋它的樁接受其地形，
        /// 且以共邊相連的方式與某根樁的佔地格地形連通。
        /// </summary>
        public HashSet<IntVec3> Footprint
        {
            get
            {
                EnsureFootprint();
                return footprint;
            }
        }

        public List<IntVec3> FootprintCells
        {
            get
            {
                EnsureFootprint();
                return footprintCells;
            }
        }

        /// <summary>爭議格中實際會被帶走的部分（非虛空）。</summary>
        public List<IntVec3> ContestedFootprintCells
        {
            get
            {
                EnsureFootprint();
                return contestedFootprintCells;
            }
        }

        /// <summary>未被 footprint 完整包含、分離時將被摧毀的多格建築/藍圖/框架（設計稿 §3-5）。</summary>
        public List<Thing> TornApartThings
        {
            get
            {
                EnsureFootprint();
                return tornApartThings;
            }
        }

        public List<IntVec3> TornApartCells
        {
            get
            {
                EnsureFootprint();
                return tornApartCells;
            }
        }

        public void InvalidateFootprint()
        {
            footprint = null;
            survey = null;
        }

        public TransferShipSurvey Survey
        {
            get
            {
                int now = Find.TickManager.TicksGame;
                if (survey == null || now - surveyTick > SurveyCacheTicks || now < surveyTick)
                {
                    survey = TransferShipSurvey.Build(this);
                    surveyTick = now;
                }
                return survey;
            }
        }

        public void InvalidateSurvey()
        {
            survey = null;
        }

        /// <summary>物件的佔地是否完整位於 cells 內。</summary>
        public static bool FullyInside(Thing t, HashSet<IntVec3> cells)
        {
            foreach (IntVec3 c in t.OccupiedRect())
            {
                if (!cells.Contains(c))
                {
                    return false;
                }
            }
            return true;
        }

        public bool Contains(CompStructuralPile pile)
        {
            return piles.Contains(pile);
        }

        private void EnsureFootprint()
        {
            int cacheTicks = DMSE_DefOf.DMSE_TransferFlightTuning?.footprintCacheTicks ?? 250;
            int now = Find.TickManager.TicksGame;
            if (footprint != null && now - footprintTick <= cacheTicks)
            {
                return;
            }
            RecalculateFootprint();
            footprintTick = now;
        }

        public void RecalculateFootprint()
        {
            bool rebuild = footprint == null;
            if (rebuild)
            {
                footprint = new HashSet<IntVec3>();
            }
            // 地形沒變時連通分量也不會變，footprint 原樣保留；只有多格建築可能增減，割裂判定照常重算
            if (SyncTopology() || rebuild)
            {
                RebuildFootprintFromTopology();
            }
            RecalculateTornApart();
        }

        /// <summary>候選格：非虛空，且至少一根覆蓋它的樁接受其地形。</summary>
        private bool IsCandidate(IntVec3 c)
        {
            return IsSolidCell(map, c) && AcceptsTerrainAt(c);
        }

        /// <summary>
        /// 把候選格的變動餵給 <see cref="GridTopology"/>（拿掉的一批、放上的一批，各只做一輪修復）。
        /// 第一次呼叫時以覆蓋範圍的外接矩形建立拓撲。回傳候選格是否有變動。
        /// </summary>
        private bool SyncTopology()
        {
            topoAdds.Clear();
            topoRemoves.Clear();
            if (topology == null)
            {
                if (covered.Count == 0)
                {
                    return false;
                }
                int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
                foreach (IntVec3 c in covered)
                {
                    if (c.x < minX) minX = c.x;
                    if (c.z < minZ) minZ = c.z;
                    if (c.x > maxX) maxX = c.x;
                    if (c.z > maxZ) maxZ = c.z;
                }
                topologyRect = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                topology = new GridTopology(topologyRect.Width, topologyRect.Height);
                foreach (IntVec3 c in covered)
                {
                    if (IsCandidate(c))
                    {
                        topoAdds.Add(topology.Index(c.x - topologyRect.minX, c.z - topologyRect.minZ));
                    }
                }
                topology.AddBatch(topoAdds);
                return true;
            }

            foreach (IntVec3 c in covered)
            {
                int x = c.x - topologyRect.minX, y = c.z - topologyRect.minZ;
                bool want = IsCandidate(c);
                if (want != topology.Present(x, y))
                {
                    (want ? topoAdds : topoRemoves).Add(topology.Index(x, y));
                }
            }
            if (topoRemoves.Count > 0)
            {
                topology.RemoveBatch(topoRemoves);
            }
            if (topoAdds.Count > 0)
            {
                topology.AddBatch(topoAdds);
            }
            return topoAdds.Count > 0 || topoRemoves.Count > 0;
        }

        /// <summary>船體 = 與任一根樁的佔地格同屬一個連通塊的候選格（樁腳下斷開的碎塊不算）。</summary>
        private void RebuildFootprintFromTopology()
        {
            footprint.Clear();
            footprintCells.Clear();
            contestedFootprintCells.Clear();
            if (topology == null)
            {
                return;
            }

            anchorComponents.Clear();
            for (int i = 0; i < piles.Count; i++)
            {
                Thing pile = piles[i].parent;
                if (!pile.Spawned)
                {
                    continue;
                }
                foreach (IntVec3 c in pile.OccupiedRect())
                {
                    if (topologyRect.Contains(c))
                    {
                        int comp = topology.ComponentOf(c.x - topologyRect.minX, c.z - topologyRect.minZ);
                        if (comp != 0)
                        {
                            anchorComponents.Add(comp);
                        }
                    }
                }
            }
            if (anchorComponents.Count == 0)
            {
                return;
            }

            foreach (IntVec3 c in covered)
            {
                int comp = topology.ComponentOf(c.x - topologyRect.minX, c.z - topologyRect.minZ);
                if (comp == 0 || !anchorComponents.Contains(comp))
                {
                    continue;
                }
                footprint.Add(c);
                footprintCells.Add(c);
                if (contestedCells.Contains(c))
                {
                    contestedFootprintCells.Add(c);
                }
            }
        }

        /// <summary>覆蓋此格的樁中，是否至少有一根接受其地形。</summary>
        private bool AcceptsTerrainAt(IntVec3 c)
        {
            if (!filteredCoverMask.TryGetValue(c, out int mask))
            {
                return true;
            }
            for (int i = 0; i < filteredCoverProps.Count; i++)
            {
                if ((mask & (1 << i)) != 0 && filteredCoverProps[i].AcceptsTerrainAt(map, c))
                {
                    return true;
                }
            }
            return false;
        }

        private void RecalculateTornApart()
        {
            tornApartThings.Clear();
            tornApartCells.Clear();
            HashSet<Thing> checkedThings = new HashSet<Thing>();
            for (int i = 0; i < footprintCells.Count; i++)
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(footprintCells[i]);
                for (int j = 0; j < things.Count; j++)
                {
                    Thing t = things[j];
                    if (!IsMultiCellStructure(t) || !checkedThings.Add(t))
                    {
                        continue;
                    }
                    CellRect rect = t.OccupiedRect();
                    bool fullyInside = true;
                    foreach (IntVec3 c in rect)
                    {
                        if (!footprint.Contains(c))
                        {
                            fullyInside = false;
                            break;
                        }
                    }
                    if (!fullyInside)
                    {
                        tornApartThings.Add(t);
                        tornApartCells.AddRange(rect.ClipInsideMap(map).Cells);
                    }
                }
            }
        }

        private static bool IsMultiCellStructure(Thing t)
        {
            if (t.def.size.x * t.def.size.z <= 1)
            {
                return false;
            }
            return t.def.category == ThingCategory.Building || t is Blueprint || t is Frame;
        }

        /// <summary>設計稿 §3-4：有地基，或地形不是此圖層的虛空（預設 Space，見 <see cref="TransferEnvironment.VoidTerrain(Map)"/>）。</summary>
        public static bool IsSolidCell(Map map, IntVec3 c)
        {
            if (map.terrainGrid.FoundationAt(c) != null)
            {
                return true;
            }
            return map.terrainGrid.TerrainAt(c) != TransferEnvironment.VoidTerrain(map);
        }
    }
}
