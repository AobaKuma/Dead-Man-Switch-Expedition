using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public static class ImpactCraterUtility
    {
        public static IImpactCraterService Service { get; private set; }

        public static void Initialize(PlayerConfigSettings settings)
        {
            Service = new ImpactCraterService(settings);
        }

        /// <summary>
        /// WorldGenStep entry: apply configured craters by seed.
        /// </summary>
        public static int ApplyConfiguredCraters(string seedString, PlanetLayer layer, string mutatorDefName)
        {
            if (Service == null || layer == null || seedString.NullOrEmpty()) return 0;

            TileMutatorDef mutatorDef = DefDatabase<TileMutatorDef>.GetNamedSilentFail(mutatorDefName);
            if (mutatorDef == null)
            {
                Log.Warning($"[ImpactCrater] TileMutatorDef not found: {mutatorDefName}");
                return 0;
            }

            return Service.ApplyToWorld(layer, seedString, mutatorDef);
        }

        /// <summary>
        /// Runtime: apply an impact crater mutator to a specific world tile.
        /// </summary>
        public static void ApplyImpactCraterAtTile(string shipName, int level, PlanetTile planetTile)
        {
            if (!planetTile.Valid) return;
            if (planetTile.LayerDef.isSpace) return;

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
                    radiusInTiles = level,
                    enabled = true
                });
            }

            BiomeDef lavaBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("LavaField");
            if (lavaBiome == null)
            {
                Log.Warning("[DMSE] BiomeDef 'LavaField' not found, skipping biome conversion.");
                return;
            }

            const int craterRadius = 10;

            // 依撞擊等級調整坑深（可再微調）
            float craterDepth = Mathf.Clamp(120f + level * 100f, 120f, 450f);
            float rimHeight = craterDepth * 5f;

            // 對應 RimWorld 世界地形常見海拔範圍
            const float minElevation = -500f;
            const float maxElevation = 5000f;

            List<PlanetTile> craterTiles = new List<PlanetTile>();

            planetTile.Layer.Filler.FloodFill(planetTile, x => true, (tile, dist) =>
            {
                float radial = Find.WorldGrid.ApproxDistanceInTiles(planetTile, tile);
                if (radial > craterRadius) return false;

                craterTiles.Add(tile);

                float n = Mathf.Clamp01(radial / craterRadius); // 0~1
                float bowl = -craterDepth * Mathf.Pow(1f - n, 2f);
                float rim = rimHeight * Mathf.Exp(-Mathf.Pow((n - 0.9f) / 0.08f, 2f));
                float delta = bowl + rim;

                Tile worldTile = tile.Tile;
                float newElevation = Mathf.Clamp(worldTile.elevation + delta, minElevation, maxElevation);
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
                    worldTile.PrimaryBiome = BiomeDefOf.Lake;
                    if (!worldTile.Mutators.Contains(TileMutatorDefOf.Lakeshore))
                        worldTile.AddMutator(TileMutatorDefOf.Lakeshore);
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
                        tileData.mutatorsNullable.RemoveAll(m => m != null && m.Worker is TileMutatorWorker_River);
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
                if (dist >= 20f && dist <= 30f)
                {
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
    }
}