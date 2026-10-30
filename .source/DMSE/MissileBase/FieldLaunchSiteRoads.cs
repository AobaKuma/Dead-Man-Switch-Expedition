using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 在野戰陣地各結構之間鋪道路：以結構中心的最小生成樹連通全部結構，另外從中心結構拉一條到最近的地圖邊緣。
    /// 路徑以 A* 繞開岩石、建築與結構範圍，偏好沿用已鋪好的路段；只覆蓋自然地面（不動水域、可建造地板）。
    /// 須在結構生成之後、植被生成之前執行（道路地形沒有肥沃度，之後不會長植物）。
    /// </summary>
    /// <summary>
    /// 鋪設野戰陣地道路。排在原版結構、遺跡與地標（order 500–750）之後、植被（900）之前，
    /// 道路才不會被它們的地板覆蓋，也能繞開它們的牆。結構範圍由 <see cref="GenStep_FieldLaunchSite"/> 存入 MapGenerator 變數。
    /// </summary>
    public class GenStep_FieldLaunchSiteRoads : GenStep
    {
        public TerrainDef roadTerrain;
        public int roadWidth = 2;

        /// <summary>另從中心結構鋪一條路到最近的地圖邊緣（補給車隊的進出路線）。</summary>
        public bool roadToMapEdge = true;

        public override int SeedPart => 0x5B13D0A7;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!MapGenerator.TryGetVar(FieldLaunchSiteRoads.RectsVar, out List<CellRect> rects) || rects.NullOrEmpty()) return;
            int painted = FieldLaunchSiteRoads.Build(map, rects, roadTerrain, roadWidth, roadToMapEdge);
            if (Prefs.DevMode)
                Log.Message($"[DMSE FieldLaunchSite] {painted} road cells between {rects.Count} structures.");
        }
    }

    public static class FieldLaunchSiteRoads
    {
        /// <summary>MapGenerator 變數：已放置結構的範圍，[0] 為中心結構。</summary>
        public const string RectsVar = "DMSE_FieldLaunchSiteRects";

        private const int MaxExpansions = 80000;
        private const float WaterCost = 20f;
        private const float RoadCost = 0.4f;

        private static readonly IntVec3[] Dirs =
        {
            IntVec3.North, IntVec3.East, IntVec3.South, IntVec3.West,
            new IntVec3(1, 0, 1), new IntVec3(1, 0, -1), new IntVec3(-1, 0, 1), new IntVec3(-1, 0, -1),
        };

        /// <summary>鋪路並回傳鋪上的格數。<paramref name="rects"/>[0] 視為中心（通往地圖邊緣的那一座）。</summary>
        public static int Build(Map map, List<CellRect> rects, TerrainDef road, int width, bool toMapEdge)
        {
            if (map == null || road == null || rects.NullOrEmpty()) return 0;
            int painted = 0;

            // Prim 最小生成樹決定連接順序：每次把最近的未連結構接上。新路從「已鋪好的整個路網」分岔出去，
            // 而不是從另一座結構的門口出發——否則兩條路只在結構邊上各自碰到它，路網會被結構切成好幾段。
            HashSet<IntVec3> network = new HashSet<IntVec3>();
            List<int> connected = new List<int> { 0 };
            List<int> remaining = Enumerable.Range(1, rects.Count - 1).ToList();
            while (remaining.Count > 0)
            {
                int bestFrom = -1, bestTo = -1;
                float best = float.MaxValue;
                foreach (int a in connected)
                    foreach (int b in remaining)
                    {
                        float d = rects[a].CenterCell.DistanceToSquared(rects[b].CenterCell);
                        if (d < best) { best = d; bestFrom = a; bestTo = b; }
                    }
                remaining.Remove(bestTo);
                connected.Add(bestTo);

                IntVec3 goal = Doorstep(map, rects, rects[bestTo], rects[bestFrom].CenterCell);
                if (!goal.IsValid) continue;
                IEnumerable<IntVec3> starts = network.Count > 0
                    ? network
                    : new[] { Doorstep(map, rects, rects[0], rects[bestTo].CenterCell) }.Where(c => c.IsValid);
                List<IntVec3> path = FindPath(map, rects, road, starts, c => c == goal, goal);
                if (path == null && Prefs.DevMode)
                    Log.Message($"[DMSE FieldLaunchSite] no road route {rects[bestFrom].CenterCell} → {rects[bestTo].CenterCell}.");
                painted += Paint(map, rects, path, road, width, network);
            }

            if (toMapEdge)
            {
                IntVec3 edge = NearestEdgeCell(map, rects[0].CenterCell);
                IEnumerable<IntVec3> starts = network.Count > 0
                    ? network
                    : new[] { Doorstep(map, rects, rects[0], edge) }.Where(c => c.IsValid);
                List<IntVec3> path = FindPath(map, rects, road, starts, c => c.OnEdge(map), edge);
                painted += Paint(map, rects, path, road, width, network);
            }
            return painted;
        }

        /// <summary>結構外圍一圈中可通行、離 <paramref name="toward"/> 最近的格。</summary>
        private static IntVec3 Doorstep(Map map, List<CellRect> rects, CellRect rect, IntVec3 toward)
        {
            return rect.ExpandedBy(1).EdgeCells
                .Where(c => Passable(map, rects, c))
                .OrderBy(c => c.DistanceToSquared(toward))
                .FirstOrFallback(IntVec3.Invalid);
        }

        private static IntVec3 NearestEdgeCell(Map map, IntVec3 c)
        {
            int maxX = map.Size.x - 1, maxZ = map.Size.z - 1;
            int dW = c.x, dE = maxX - c.x, dS = c.z, dN = maxZ - c.z;
            int m = Mathf.Min(Mathf.Min(dW, dE), Mathf.Min(dS, dN));
            if (m == dW) return new IntVec3(0, 0, c.z);
            if (m == dE) return new IntVec3(maxX, 0, c.z);
            if (m == dS) return new IntVec3(c.x, 0, 0);
            return new IntVec3(c.x, 0, maxZ);
        }

        private static bool Passable(Map map, List<CellRect> rects, IntVec3 c)
        {
            if (!c.InBounds(map) || c.GetEdifice(map) != null) return false;
            TerrainDef t = c.GetTerrain(map);
            if (t.passability == Traversability.Impassable) return false;
            for (int i = 0; i < rects.Count; i++)
                if (rects[i].Contains(c)) return false;
            return true;
        }

        /// <summary>多起點 A*：從 <paramref name="starts"/> 任一格出發，找到第一個符合 <paramref name="isGoal"/> 的格。</summary>
        private static List<IntVec3> FindPath(Map map, List<CellRect> rects, TerrainDef road, IEnumerable<IntVec3> starts,
            System.Predicate<IntVec3> isGoal, IntVec3 heuristicTarget)
        {
            CellIndices idx = map.cellIndices;
            Dictionary<int, float> cost = new Dictionary<int, float>();
            Dictionary<int, int> parent = new Dictionary<int, int>();
            PriorityQueue<int, float> open = new PriorityQueue<int, float>();
            foreach (IntVec3 s in starts)
            {
                int si = idx.CellToIndex(s);
                if (cost.ContainsKey(si)) continue;
                cost[si] = 0f;
                open.Enqueue(si, s.DistanceTo(heuristicTarget) * RoadCost);
            }

            int expansions = 0;
            while (open.Count > 0 && expansions++ < MaxExpansions)
            {
                int ci = open.Dequeue();
                IntVec3 c = idx.IndexToCell(ci);
                if (isGoal(c))
                {
                    List<IntVec3> path = new List<IntVec3> { c };
                    while (parent.TryGetValue(ci, out int p)) { ci = p; path.Add(idx.IndexToCell(ci)); }
                    path.Reverse();
                    return path;
                }

                float g = cost[ci];
                for (int d = 0; d < Dirs.Length; d++)
                {
                    IntVec3 n = c + Dirs[d];
                    if (!Passable(map, rects, n)) continue;
                    // 斜走時兩個直角鄰格都要可通行，避免路面從岩角之間穿過。
                    if (d >= 4 && (!Passable(map, rects, new IntVec3(n.x, 0, c.z)) || !Passable(map, rects, new IntVec3(c.x, 0, n.z)))) continue;

                    TerrainDef t = n.GetTerrain(map);
                    float step = (d >= 4 ? 1.41f : 1f) * (t == road ? RoadCost : t.IsWater ? WaterCost : 1f);
                    int ni = idx.CellToIndex(n);
                    float ng = g + step;
                    if (cost.TryGetValue(ni, out float old) && old <= ng) continue;
                    cost[ni] = ng;
                    parent[ni] = ci;
                    open.Enqueue(ni, ng + n.DistanceTo(heuristicTarget) * RoadCost);
                }
            }
            return null;
        }

        /// <summary>沿路徑鋪路面，寬 <paramref name="width"/> 格；只覆蓋可通行、非水域、非可建造地板的自然地面。</summary>
        private static int Paint(Map map, List<CellRect> rects, List<IntVec3> path, TerrainDef road, int width, HashSet<IntVec3> network)
        {
            if (path == null) return 0;
            int painted = 0;
            foreach (IntVec3 c in path)
            {
                network.Add(c);
                for (int dx = 0; dx < width; dx++)
                    for (int dz = 0; dz < width; dz++)
                    {
                        IntVec3 p = c + new IntVec3(dx, 0, dz);
                        if (!Passable(map, rects, p)) continue;
                        TerrainDef t = p.GetTerrain(map);
                        if (t == road || t.IsWater || t.BuildableByPlayer) continue;
                        map.terrainGrid.SetTerrain(p, road);
                        painted++;
                    }
            }
            return painted;
        }
    }
}
