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

            for (int i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                if (rec == null || !rec.enabled || !rec.IsValid()) continue;
                if (!string.Equals(rec.planetSeedString, seed, StringComparison.Ordinal)) continue;

                ApplyCraterBiomeOverlay(layer, seed, rec);
            }
        }

        private static void ApplyCraterBiomeOverlay(PlanetLayer layer, string worldSeed, ImpactCraterRecord rec)
        {
            // 你可以改成從 record 自訂
            BiomeDef coreBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("Wasteland");
            BiomeDef rimBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("Desert");
            BiomeDef ejectaBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("AridShrubland");

            if (coreBiome == null || rimBiome == null || ejectaBiome == null)
            {
                Log.Warning("[ImpactCrater] Missing biome defs for crater overlay.");
                return;
            }

            PlanetTile center = FindClosestTileByLongLat(layer, rec.longitude, rec.latitude);
            if (!center.Valid) return;

            // 避免把海洋改成陸地遺跡（可按需求移除）
            Tile centerTile = center.Tile;
            if (centerTile.PrimaryBiome == BiomeDefOf.Ocean || centerTile.PrimaryBiome == BiomeDefOf.Lake)
                return;

            int baseRadius = Mathf.Max(4, rec.radiusInTiles > 0 ? rec.radiusInTiles : 10);
            int coreRadius = Mathf.Max(1, Mathf.RoundToInt(baseRadius * 0.40f));
            int rimRadius = Mathf.Max(coreRadius + 1, Mathf.RoundToInt(baseRadius * 0.78f));
            int rimWidth = Mathf.Max(1, Mathf.RoundToInt(baseRadius * 0.18f));
            int ejectaRadius = Mathf.Max(rimRadius + 1, Mathf.RoundToInt(baseRadius * 1.55f));

            int noiseSeedBase = Gen.HashCombineInt(GenText.StableStringHash(worldSeed), GenText.StableStringHash(rec.craterName));

            var q = new Queue<(PlanetTile tile, int dist)>();
            var visited = new HashSet<PlanetTile>();
            var neighbors = new List<PlanetTile>(8);

            visited.Add(center);
            q.Enqueue((center, 0));

            while (q.Count > 0)
            {
                var (cur, dist) = q.Dequeue();
                if (dist > ejectaRadius) continue;

                Tile t = cur.Tile;
                if (t.PrimaryBiome != BiomeDefOf.Ocean && t.PrimaryBiome != BiomeDefOf.Lake)
                {
                    int tileSeed = Gen.HashCombineInt(noiseSeedBase, cur.tileId);
                    float n = Rand.ValueSeeded(tileSeed);                 // 0..1
                    float jitter = (n - 0.5f) * (baseRadius * 0.22f);    // 邊界抖動
                    float d = dist + jitter;

                    // 1) 坑底（明顯中心）
                    if (d <= coreRadius)
                    {
                        t.PrimaryBiome = coreBiome;
                    }
                    // 2) 環形坑壁（高可見度）
                    else
                    {
                        float ringDelta = Mathf.Abs(d - rimRadius);
                        if (ringDelta <= rimWidth)
                        {
                            // 靠近 ring 中線機率更高，形成連續坑壁
                            float ringFactor = 1f - (ringDelta / rimWidth); // 0..1
                            float placeChance = Mathf.Lerp(0.55f, 0.92f, ringFactor);
                            if (n < placeChance)
                                t.PrimaryBiome = rimBiome;
                        }
                        // 3) 外圍噴濺（稀疏斑塊）
                        else if (d <= ejectaRadius)
                        {
                            float t01 = Mathf.InverseLerp(rimRadius, ejectaRadius, d); // 越外越接近1
                            float chance = Mathf.Lerp(0.35f, 0.06f, t01);
                            if (n < chance)
                                t.PrimaryBiome = ejectaBiome;
                        }
                    }
                }

                if (dist >= ejectaRadius) continue;
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
    }
}