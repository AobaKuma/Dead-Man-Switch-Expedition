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

            string currentCampaignId = ImpactCraterService.CurrentCampaignId; // 請改成你的 GUID 來源

            for (int i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                if (rec == null || !rec.enabled) continue;

                if (rec.planetSeedString.NullOrEmpty()) continue;
                if (rec.craterName.NullOrEmpty()) continue;
                if (rec.latitude < -90f || rec.latitude > 90f) continue;
                if (rec.longitude < -180f || rec.longitude > 180f) continue;
                if (rec.radiusInTiles < 0) continue;
                if (!string.Equals(rec.planetSeedString, seed, StringComparison.Ordinal)) continue;

                // 沒有 campaignId 的舊資料 -> climate 版
                bool isCurrentCampaign =
                    !currentCampaignId.NullOrEmpty() &&
                    !rec.campaignId.NullOrEmpty() &&
                    string.Equals(rec.campaignId, currentCampaignId, StringComparison.Ordinal);

                ApplyCraterTerrainFromRecord(layer, seed, rec, isCurrentCampaign);
            }

            RepairNullBiomes(layer, seed);
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

        private static void ApplyCraterTerrainFromRecord(PlanetLayer layer, string worldSeed, ImpactCraterRecord rec, bool isCurrentCampaign)
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

                    BiomeDef lavaBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("LavaField");
                    BiomeDef impactCraterBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("DMSE_ImpactCraterBiome");

                    // climate 版中心輻射範圍（可調）
                    float climateCoreRadius = Mathf.Max(2f, craterRadius * 0.45f);

                    // 穩定隨機種子（確保同 seed 世界結果一致）
                    int mutSeedBase = Gen.HashCombineInt(
                        GenText.StableStringHash(worldSeed),
                        GenText.StableStringHash(rec.craterName ?? "DMSE_Crater"));

                    if (newElevation <= 0f)
                    {
                        t.PrimaryBiome = BiomeDefOf.Lake;
                        ImpactCraterUtility.TryAddLakeshoreMutator(t);
                    }
                    else
                    {
                        if (isCurrentCampaign)
                        {
                            // 同 campaign：岩漿池版本
                            if (lavaBiome != null)
                                t.PrimaryBiome = lavaBiome;
                        }
                        else
                        {
                            // 非同 campaign：氣候版本
                            BiomeDef climateBiome = ImpactCraterUtility.GetClimateBiomeForTile(t);
                            if (climateBiome != null)
                                t.PrimaryBiome = climateBiome;

                            // 中心輻射生成 DMSE_ImpactCraterBiome
                            if (impactCraterBiome != null && radial <= climateCoreRadius)
                            {
                                // 中心100%，往外遞減
                                float chance = Mathf.InverseLerp(climateCoreRadius, 0f, radial);
                                int tileSeed = Gen.HashCombineInt(mutSeedBase, cur.tileId);
                                float roll = Rand.ValueSeeded(tileSeed);

                                if (roll <= chance)
                                {
                                    t.PrimaryBiome = impactCraterBiome;
                                }
                            }
                        }
                    }

                    if (t.PrimaryBiome == null)
                        t.PrimaryBiome = BiomeDefOf.TemperateForest;
                }

                neighbors.Clear();
                layer.GetTileNeighbors(cur, neighbors);
                for (int i = 0; i < neighbors.Count; i++)
                {
                    PlanetTile nb = neighbors[i];
                    if (!visited.Add(nb)) continue;

                    // 用幾何距離限制擴散，避免 dist 截斷造成六邊形/多邊形邊界
                    float nbRadial = Find.WorldGrid.ApproxDistanceInTiles(center, nb);
                    if (nbRadial <= craterRadius + 2f)
                    {
                        q.Enqueue((nb, dist + 1));
                    }
                }
            }
        }

        private static void RepairNullBiomes(PlanetLayer layer, string seed)
        {
            if (layer == null || !layer.IsRootSurface) return;

            BiomeDef fallback = BiomeDefOf.TemperateForest;
            if (fallback == null || fallback.baseWeatherCommonalities == null || fallback.baseWeatherCommonalities.Count == 0)
            {
                foreach (var b in DefDatabase<BiomeDef>.AllDefsListForReading)
                {
                    if (b != null && b.baseWeatherCommonalities != null && b.baseWeatherCommonalities.Count > 0)
                    {
                        fallback = b;
                        break;
                    }
                }
            }

            if (fallback == null) return;

            for (int i = 0; i < layer.TilesCount; i++)
            {
                PlanetTile pt = new PlanetTile(i, layer);
                Tile t = pt.Tile;
                if (t != null && t.PrimaryBiome == null)
                {
                    t.PrimaryBiome = fallback;
                    Log.Warning("[DMSE] Repaired null biome at tile " + i + " (seed=" + seed + ")");
                }
            }
        }
    }
}