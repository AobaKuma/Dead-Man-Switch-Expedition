using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    [StaticConstructorOnStartup]
    public static class ImpactCraterUtility
    {
        public static IImpactCraterService Service { get; private set; }

        public static void Initialize(PlayerConfigSettings settings)
        {
            Service = new ImpactCraterService(settings);
        }

        /// <summary>
        /// Runtime: apply an impact crater mutator to a specific world tile.
        /// </summary>
        public static void ApplyImpactCraterAtTile(string shipName, int level, PlanetTile planetTile)
        {
            if (!planetTile.Valid) return;
            if (planetTile.LayerDef.isSpace) return;
            if (Service == null)
            {
                Log.Warning("[DMSE] ImpactCraterUtility not initialized.");
                return;
            }
            // 記錄當前座標
            if (Service is ImpactCraterService craterService)
            {
                Vector2 lonhLat = Find.WorldGrid.LongLatOf(planetTile);
                craterService.AddRecord(new ImpactCraterRecord
                {
                    planetSeedString = Find.World?.info?.seedString ?? string.Empty,
                    craterName = "DMSE_Crater".Translate(shipName),
                    longitude = lonhLat.x,
                    latitude = lonhLat.y,
                    radiusInTiles = 10 + level * 10,
                    enabled = true
                });
            }

            BiomeDef lavaBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("LavaField");
            if (lavaBiome == null)
            {
                Log.Warning("[DMSE] BiomeDef 'LavaField' not found, skipping biome conversion.");
                return;
            }

            int craterRadius = 10;
            craterRadius += level * 10; // 依撞擊等級調整坑半徑（可再微調）

            // 依撞擊等級調整坑深（可再微調）
            float craterDepth = Mathf.Clamp(120f + level * 100f, 120f, 450f);
            float rimHeight = craterDepth * 5f;

            // 對應 RimWorld 世界地形常見海拔範圍
            const float minElevation = -500f;
            const float maxElevation = 5000f;

            List<PlanetTile> craterTiles = new List<PlanetTile>();
            float centerBaseElevation = planetTile.Tile.elevation;

            planetTile.Layer.Filler.FloodFill(
                planetTile,
                x => Find.WorldGrid.ApproxDistanceInTiles(planetTile, x) <= craterRadius + 2f,
                (tile, dist) =>
                {
                    float radial = Find.WorldGrid.ApproxDistanceInTiles(planetTile, tile);
                    if (radial > craterRadius) return false;

                    craterTiles.Add(tile);

                    float n = Mathf.Clamp01(radial / craterRadius); // 0~1
                    Tile worldTile = tile.Tile;
                    float originalElevation = worldTile.elevation;

                    float bowl = -craterDepth * Mathf.Pow(1f - n, 2f);
                    float rim = rimHeight * Mathf.Exp(-Mathf.Pow((n - 0.9f) / 0.08f, 2f));
                    float delta = bowl + rim;

                    // 外拋搬運：中心剝蝕、外圈堆積
                    float transportStrength = Mathf.Clamp01(level / 5f);
                    float relief = Mathf.Max(0f, originalElevation - centerBaseElevation); // 只搬運高地量
                    float outwardTransport = 0f;

                    // 內圈（0~0.7）：把原地形向外「挖走」
                    if (n < 0.7f)
                    {
                        outwardTransport -= relief * (1f - n / 0.7f) * 0.55f * transportStrength;
                    }
                    // 外圈（0.7~1.0）：把地形「堆到外圍」
                    else
                    {
                        outwardTransport += relief * ((n - 0.7f) / 0.3f) * 0.35f * transportStrength;
                    }

                    // 外圍再加一圈噴發堆積，增加「被推向外圍」視覺
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

                    worldTile.elevation = newElevation;

                    // 依新海拔重算地形等級（hilliness）
                    if (newElevation <= 0f)
                    {
                        worldTile.hilliness = Hilliness.Flat;
                    }
                    else if (newElevation > 1000f)
                    {
                        worldTile.hilliness = Hilliness.Impassable;
                    }
                    else if (newElevation > 500f)
                    {
                        worldTile.hilliness = Hilliness.Mountainous;
                    }
                    else if (newElevation > 250f)
                    {
                        worldTile.hilliness = Hilliness.LargeHills;
                    }
                    else if (newElevation > 15f)
                    {
                        worldTile.hilliness = Hilliness.SmallHills;
                    }
                    else
                    {
                        worldTile.hilliness = Hilliness.Flat;
                    }

                    if (newElevation <= 0f)
                    {
                        TryAddLakeshoreMutator(worldTile);
                    }
                    else
                    {
                        worldTile.PrimaryBiome = lavaBiome;
                    }

                    return false;
                });

            if (planetTile.Layer.IsRootSurface)
            {
                HashSet<PlanetTile> craterSet = new HashSet<PlanetTile>(craterTiles);

                // 1) 清空坑內道路/河流
                foreach (PlanetTile t in craterSet)
                {
                    Tile tileData = t.Tile;

                    SurfaceTile st = tileData as SurfaceTile;
                    if (st != null)
                    {
                        st.potentialRoads = null;
                        st.potentialRivers = null;
                        st.riverDist = 0;
                    }

                    // 移除河流相關 mutator，避免 TileMutatorWorker_River.GetLabel() 讀取 Rivers[0] 時 NRE
                    if (tileData.mutatorsNullable != null)
                    {
                        tileData.mutatorsNullable = null; // 坑內地形突變太多，直接清空 mutators，避免殘留不適用的 mutator 造成 NRE
                    }
                    NormalizeSurfaceTileLinksAndMutators(tileData);
                }

                // 2) 清除外圈指向坑內的連結，避免殘留
                List<PlanetTile> neighbors = new List<PlanetTile>();
                foreach (PlanetTile t in craterSet)
                {
                    Find.WorldGrid.GetTileNeighbors(t, neighbors);
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        PlanetTile n = neighbors[i];
                        if (craterSet.Contains(n)) continue;

                        SurfaceTile neighborTile = n.Tile as SurfaceTile;
                        if (neighborTile == null) continue;

                        neighborTile.potentialRoads?.RemoveAll(link => craterSet.Contains(link.neighbor));
                        if (neighborTile.potentialRoads != null && neighborTile.potentialRoads.Count == 0)
                            neighborTile.potentialRoads = null;

                        neighborTile.potentialRivers?.RemoveAll(link => craterSet.Contains(link.neighbor));
                        if (neighborTile.potentialRivers != null && neighborTile.potentialRivers.Count == 0)
                            neighborTile.potentialRivers = null;

                        NormalizeSurfaceTileLinksAndMutators(neighborTile);
                    }
                    neighbors.Clear();
                }
            }

            // 摧毀撞擊點 20~30 格環帶內的 Settlement
            List<Settlement> toDestroy = new List<Settlement>();
            List<Settlement> settlements = Find.WorldObjects.Settlements;

            for (int i = 0; i < settlements.Count; i++)
            {
                Settlement settlement = settlements[i];
                if (settlement == null || settlement.Destroyed) continue;
                if (settlement.Tile.Layer != planetTile.Layer) continue; // 同層才計算

                float dist = Find.WorldGrid.ApproxDistanceInTiles(planetTile, settlement.Tile);
                if (dist <= 200f)
                {
                    if (Rand.Chance(dist / 200)) continue;
                    toDestroy.Add(settlement);
                }
            }

            for (int i = 0; i < toDestroy.Count; i++)
            {
                Settlement settlement = toDestroy[i];

                WorldObjectDef destroyedDef = settlement.Tile.LayerDef.DestroyedSettlementWorldObjectDef;
                if (destroyedDef != null)
                {
                    DestroyedSettlement ds = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(destroyedDef);
                    ds.Tile = settlement.Tile;
                    ds.SetFaction(settlement.Faction);
                    Find.WorldObjects.Add(ds);
                }

                settlement.Destroy();
            }

            Find.World.renderer.GetLayer<WorldDrawLayer_Terrain>(planetTile.Layer).RegenerateNow();
            Find.World.renderer.GetLayer<WorldDrawLayer_Hills>(planetTile.Layer).RegenerateNow();
            Find.World.renderer.GetLayer<WorldDrawLayer_Roads>(planetTile.Layer).RegenerateNow();
            Find.World.renderer.GetLayer<WorldDrawLayer_Rivers>(planetTile.Layer).RegenerateNow();
            Find.WorldPathGrid.RecalculateLayerPerceivedPathCosts(planetTile.Layer);
            Find.WorldReachability.ClearCache();
        }

        private static void NormalizeSurfaceTileLinksAndMutators(Tile tileData)
        {
            SurfaceTile st = tileData as SurfaceTile;
            if (st == null) return;

            if (st.potentialRoads != null)
            {
                st.potentialRoads.RemoveAll(l => !l.neighbor.Valid || l.road == null);
                if (st.potentialRoads.Count == 0) st.potentialRoads = null;
            }

            if (st.potentialRivers != null)
            {
                st.potentialRivers.RemoveAll(l => !l.neighbor.Valid || l.river == null);
                if (st.potentialRivers.Count == 0) st.potentialRivers = null;
            }

            // 沒有河流連結時，移除河流 mutator，避免 WITab/Inspect 取 river 資訊時 NRE
            if ((st.potentialRivers == null || st.potentialRivers.Count == 0) && tileData.mutatorsNullable != null)
            {
                tileData.mutatorsNullable.RemoveAll(m => m != null && m.Worker is TileMutatorWorker_River);
            }
        }

        public static void TryAddLakeshoreMutator(Tile tile)
        {
            if (tile == null) return;

            TileMutatorDef lakeshore = TileMutatorDefOf.Lakeshore;
            if (lakeshore == null) return;

            if (tile.Mutators != null)
            {
                if (tile.Mutators.Contains(lakeshore)) return;

                for (int i = 0; i < tile.Mutators.Count; i++)
                {
                    TileMutatorDef m = tile.Mutators[i];
                    if (m == null || m.categories == null) continue;

                    // 已有任一 Coast 類別 mutator（如 CoastalIsland）就不要再加 Lakeshore
                    if (m.categories.Contains("Coast"))
                        return;
                }
            }

            tile.AddMutator(lakeshore);
        }
        public static float GetRiverErosionFactor(PlanetLayer layer, PlanetTile tile)
        {
            int score = 0;
            int total = 0;

            SurfaceTile self = tile.Tile as SurfaceTile;
            if (self != null)
            {
                total += 3;
                if (self.potentialRivers != null && self.potentialRivers.Count > 0)
                    score += 3;
            }

            List<PlanetTile> ns = new List<PlanetTile>(8);
            layer.GetTileNeighbors(tile, ns);
            for (int i = 0; i < ns.Count; i++)
            {
                total += 1;
                SurfaceTile st = ns[i].Tile as SurfaceTile;
                if (st != null && st.potentialRivers != null && st.potentialRivers.Count > 0)
                    score += 1;
            }

            if (total <= 0) return 0f;
            return Mathf.Clamp01((float)score / total);
        }

        public static Hilliness RecalculateHillinessFromElevation(float elevation)
        {
            if (elevation <= 0f) return Hilliness.Flat;
            if (elevation > 1000f) return Hilliness.Impassable;
            if (elevation > 500f) return Hilliness.Mountainous;
            if (elevation > 250f) return Hilliness.LargeHills;
            if (elevation > 15f) return Hilliness.SmallHills;
            return Hilliness.Flat;
        }

        public static BiomeDef GetClimateBiomeForTile(Tile t)
        {
            float temp = t.temperature;
            float rain = t.rainfall;

            // 寒冷帶
            if (temp < -5f)
                return DefDatabase<BiomeDef>.GetNamedSilentFail("Tundra");

            if (temp < 8f)
                return rain > 700f
                    ? DefDatabase<BiomeDef>.GetNamedSilentFail("BorealForest")
                    : DefDatabase<BiomeDef>.GetNamedSilentFail("Tundra");

            // 溫帶
            if (temp < 20f)
            {
                if (rain > 1400f) return DefDatabase<BiomeDef>.GetNamedSilentFail("TemperateSwamp");
                if (rain > 700f) return DefDatabase<BiomeDef>.GetNamedSilentFail("TemperateForest");
                return DefDatabase<BiomeDef>.GetNamedSilentFail("Grasslands");
            }

            // 熱帶/乾熱帶
            if (temp < 30f)
            {
                if (rain > 1600f) return DefDatabase<BiomeDef>.GetNamedSilentFail("TropicalRainforest");
                if (rain > 900f) return DefDatabase<BiomeDef>.GetNamedSilentFail("TropicalSwamp");
                return DefDatabase<BiomeDef>.GetNamedSilentFail("AridShrubland");
            }

            // 極熱
            return rain < 300f
                ? DefDatabase<BiomeDef>.GetNamedSilentFail("ExtremeDesert")
                : DefDatabase<BiomeDef>.GetNamedSilentFail("Desert");
        }
    }
}