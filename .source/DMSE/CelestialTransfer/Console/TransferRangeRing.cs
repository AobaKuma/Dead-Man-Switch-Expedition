using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 世界地圖上的航程圈。取代 <see cref="GenDraw.DrawWorldRadiusRing"/>：原版以邊緣格的重心為軸排序，
    /// 航程接近或超過半個星球時重心落到球心附近，排序失效，畫出橫跨星球的斜線。
    /// 這裡改以起點方向為軸、依方位角排序，任何半徑都畫得出完整的圈；整個圖層都在航程內時不畫。
    /// </summary>
    public static class TransferRangeRing
    {
        private static PlanetTile cachedCenter = PlanetTile.Invalid;
        private static int cachedRadius = -1;
        private static int cachedSeed;
        private static readonly List<PlanetTile> cachedRing = new List<PlanetTile>();
        private static readonly List<KeyValuePair<float, PlanetTile>> tmpSorted = new List<KeyValuePair<float, PlanetTile>>();

        public static void Draw(PlanetTile center, int radius, Material material)
        {
            if (!center.Valid || radius < 0)
            {
                return;
            }
            if (cachedCenter != center || cachedRadius != radius || cachedSeed != Find.World.info.Seed)
            {
                Rebuild(center, radius);
            }
            if (cachedRing.Count >= 5)
            {
                GenDraw.DrawWorldLineStrip(cachedRing, material, 5f * center.LayerDef.lineWidthFactor);
            }
        }

        private static void Rebuild(PlanetTile center, int radius)
        {
            cachedCenter = center;
            cachedRadius = radius;
            cachedSeed = Find.World.info.Seed;
            cachedRing.Clear();
            tmpSorted.Clear();

            WorldGrid grid = Find.WorldGrid;
            Vector3 axis = grid.GetTileCenter(center).normalized;
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(axis, u);

            center.Layer.Filler.FloodFill(center, tile => true, delegate (PlanetTile tile, int dist)
            {
                if (dist > radius + 1)
                {
                    return true;
                }
                if (dist == radius + 1)
                {
                    Vector3 p = grid.GetTileCenter(tile);
                    tmpSorted.Add(new KeyValuePair<float, PlanetTile>(Mathf.Atan2(Vector3.Dot(p, v), Vector3.Dot(p, u)), tile));
                }
                return false;
            });

            tmpSorted.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < tmpSorted.Count; i++)
            {
                cachedRing.Add(tmpSorted[i].Value);
            }
            tmpSorted.Clear();

            // 六角格的邊緣帶有鋸齒，同方位角附近可能前後顛倒；與原版相同，換掉明顯繞路的相鄰兩格。
            int n = cachedRing.Count;
            for (int i = 0; i < n; i++)
            {
                PlanetTile a = cachedRing[i];
                PlanetTile b = cachedRing[(i + 1) % n];
                PlanetTile c = cachedRing[(i + 2) % n];
                if (!grid.IsNeighbor(a, b) && grid.IsNeighbor(b, c) && grid.IsNeighbor(a, c))
                {
                    cachedRing.Swap((i + 1) % n, (i + 2) % n);
                }
            }
        }
    }
}
