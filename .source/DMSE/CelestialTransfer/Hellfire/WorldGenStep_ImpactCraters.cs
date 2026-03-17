using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class WorldGenStep_ImpactCrater : WorldGenStep
    {
        public override int SeedPart => 918273645;

        public override void GenerateFresh(string seed, PlanetLayer layer)
        {
            GenerateInternal(seed, layer);
        }
        public override void GenerateWithoutWorldData(string seed, PlanetLayer layer)
        {
            GenerateInternal(seed, layer);
        }
        public override void GenerateFromScribe(string seed, PlanetLayer layer)
        {
            GenerateInternal(seed, layer);
        }
        private static void GenerateInternal(string seed, PlanetLayer layer)
        {
            if (layer == null || seed.NullOrEmpty()) return;
            if (ImpactCraterUtility.Service == null) return;

            var records = ImpactCraterUtility.Service.GetRecords();
            if (records == null || records.Count == 0) return;

            Log.Message($"[ImpactCrater] seed={seed}, records={records.Count}");

            for (int i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                if (rec == null || !rec.enabled || !rec.IsValid()) continue;
                if (!string.Equals(rec.planetSeedString, seed, StringComparison.Ordinal)) continue;

                ApplyCraterTerrainFromRecord(layer, seed, rec);
            }
        }

        private static PlanetTile FindClosestTileByLongLat(PlanetLayer layer, float targetLon, float targetLat)
        {
            int bestId = -1;
            float bestAngle = float.MaxValue;

            for (int i = 0; i < layer.TilesCount; i++)
            {
                Vector2 ll = layer.LongLatOf(i);
                float angle = GreatCircleAngleDeg(targetLon, targetLat, ll.x, ll.y);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    bestId = i;
                }
            }

            return bestId >= 0 ? new PlanetTile(bestId, layer) : PlanetTile.Invalid;
        }

        private static float GreatCircleAngleDeg(float lon1Deg, float lat1Deg, float lon2Deg, float lat2Deg)
        {
            float dLonDeg = Mathf.DeltaAngle(lon1Deg, lon2Deg);
            float lat1 = lat1Deg * Mathf.Deg2Rad;
            float lat2 = lat2Deg * Mathf.Deg2Rad;
            float dLat = (lat2Deg - lat1Deg) * Mathf.Deg2Rad;
            float dLon = dLonDeg * Mathf.Deg2Rad;

            float a = Mathf.Sin(dLat * 0.5f) * Mathf.Sin(dLat * 0.5f)
                    + Mathf.Cos(lat1) * Mathf.Cos(lat2) * Mathf.Sin(dLon * 0.5f) * Mathf.Sin(dLon * 0.5f);
            a = Mathf.Clamp01(a);
            float c = 2f * Mathf.Asin(Mathf.Sqrt(a));
            return c * Mathf.Rad2Deg;
        }

        private static void ApplyCraterTerrainFromRecord(PlanetLayer layer, string worldSeed, ImpactCraterRecord rec)
        {
            if (!layer.IsRootSurface) return;

            PlanetTile center = FindClosestTileByLongLat(layer, rec.longitude, rec.latitude);
            if (!center.Valid) return;

            // 與 ApplyImpactCraterAtTile 一致
            int craterRadius = Mathf.Max(1, rec.radiusInTiles);
            float craterDepth = Mathf.Clamp(120f + rec.radiusInTiles * 100f, 120f, 450f);
            float rimHeight = craterDepth * 5f;

            float centerBaseElevation = center.Tile.elevation;
            float transportStrength = Mathf.Clamp01(rec.radiusInTiles / 40f);

            const float minElevation = -500f;
            const float maxElevation = 5000f;

            var visited = new HashSet<PlanetTile>();
            var q = new Queue<(PlanetTile tile, int dist)>();
            var neighbors = new List<PlanetTile>(8);

            visited.Add(center);
            q.Enqueue((center, 0));

            while (q.Count > 0)
            {
                var (cur, dist) = q.Dequeue();

                // 與 runtime 一致：用幾何距離決定是否在坑內
                float radial = Find.WorldGrid.ApproxDistanceInTiles(center, cur);
                if (radial <= craterRadius)
                {
                    Tile t = cur.Tile;

                    float n = Mathf.Clamp01(radial / craterRadius); // 0..1
                    float originalElevation = t.elevation;

                    float bowl = -craterDepth * Mathf.Pow(1f - n, 2f);
                    float rim = rimHeight * Mathf.Exp(-Mathf.Pow((n - 0.9f) / 0.08f, 2f));
                    float delta = bowl + rim;

                    // 外拋搬運：中心剝蝕、外圈堆積（把既有地形往外推）
                    float relief = Mathf.Max(0f, originalElevation - centerBaseElevation);
                    float outwardTransport = 0f;

                    // 內圈：移除高地
                    if (n < 0.7f)
                    {
                        outwardTransport -= relief * (1f - n / 0.7f) * 0.55f * transportStrength;
                    }
                    // 外圈：沉積抬升
                    else
                    {
                        outwardTransport += relief * ((n - 0.7f) / 0.3f) * 0.35f * transportStrength;
                    }

                    // 外緣額外噴濺堆積
                    if (n >= 0.75f)
                    {
                        outwardTransport += Mathf.Lerp(0f, craterDepth * 0.18f, (n - 0.75f) / 0.25f) * transportStrength;
                    }

                    float newElevation = Mathf.Clamp(originalElevation + delta + outwardTransport, minElevation, maxElevation);

                    // 核心區強制壓平：確保中心可移平原有山脈
                    float coreRadius = Mathf.Max(2f, craterRadius * 0.2f);
                    if (radial <= coreRadius)
                    {
                        // 越靠中心壓得越平，中心目標高度約 6（一定是 Flat）
                        float core01 = 1f - Mathf.Clamp01(radial / coreRadius);
                        float targetFlatElevation = Mathf.Lerp(20f, 6f, core01);
                        newElevation = Mathf.Min(newElevation, targetFlatElevation);
                    }

                    // 河流侵蝕：有河流(含鄰近)時，削低高處、回填低處
                    float riverErosion = ImpactCraterUtility.GetRiverErosionFactor(layer, cur); // 0..1
                    if (riverErosion > 0f)
                    {
                        float cut = Mathf.Lerp(0f, 80f, riverErosion) * (0.3f + 0.7f * n);      // 越靠外圈越容易被切蝕
                        float fill = Mathf.Lerp(0f, 30f, riverErosion) * (1f - n);               // 中心有些沉積
                        newElevation = Mathf.Clamp(newElevation - cut + fill, minElevation, maxElevation);
                    }

                    t.elevation = newElevation;

                    // 與 ApplyImpactCraterAtTile 的 hilliness 規則一致
                    t.hilliness = ImpactCraterUtility.RecalculateHillinessFromElevation(newElevation);

                    if (newElevation <= 0f)
                    {
                        t.PrimaryBiome = BiomeDefOf.Lake;
                        ImpactCraterUtility.TryAddLakeshoreMutator(t);
                    }
                    else
                    {
                        // 外環（高於海平面）依氣候改變地塊
                        float outerRingStart = craterRadius * 0.72f;
                        if (radial >= outerRingStart)
                        {
                            BiomeDef climateBiome =ImpactCraterUtility.GetClimateBiomeForTile(t);
                            if (climateBiome != null)
                            {
                                t.PrimaryBiome = climateBiome;
                            }
                        }
                    }
                }

                // 擴散：保持足夠範圍讓 radial<=10 的 tile 都可被遍歷到
                if (dist >= craterRadius + 4) continue;

                neighbors.Clear();
                layer.GetTileNeighbors(cur, neighbors);
                for (int i = 0; i < neighbors.Count; i++)
                {
                    PlanetTile nb = neighbors[i];
                    if (visited.Add(nb))
                        q.Enqueue((nb, dist + 1));
                }
            }
        }
    }
}