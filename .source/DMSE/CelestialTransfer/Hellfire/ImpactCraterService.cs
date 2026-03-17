using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DMSE
{
    // ----------------------------
    // 2) 統一服務介面
    // ----------------------------
    public interface IImpactCraterService
    {
        IReadOnlyList<ImpactCraterRecord> GetRecords();
        bool AddOrUpdate(ImpactCraterRecord record);
        bool Remove(string planetSeedString, string craterName);
        int ApplyToWorld(PlanetLayer layer, string worldSeedString, TileMutatorDef mutatorDef);
        int ApplyToCurrentWorld(TileMutatorDef mutatorDef);
    }
    // ----------------------------
    // 3) Utility 實作
    // ----------------------------
    public sealed class ImpactCraterService : IImpactCraterService
    {
        private readonly PlayerConfigSettings settings;

        public ImpactCraterService(PlayerConfigSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (this.settings.records == null)
                this.settings.records = new List<ImpactCraterRecord>();
        }

        public IReadOnlyList<ImpactCraterRecord> GetRecords()
            => settings.records;

        public bool AddOrUpdate(ImpactCraterRecord record)
        {
            if (record == null || !record.IsValid())
                return false;

            int idx = settings.records.FindIndex(r =>
                string.Equals(r.planetSeedString, record.planetSeedString, StringComparison.Ordinal) &&
                string.Equals(r.craterName, record.craterName, StringComparison.Ordinal));

            if (idx >= 0) settings.records[idx] = record;
            else settings.records.Add(record);

            settings.Write(); // 立即寫入 player config
            return true;
        }

        public bool Remove(string planetSeedString, string craterName)
        {
            int idx = settings.records.FindIndex(r =>
                string.Equals(r.planetSeedString, planetSeedString, StringComparison.Ordinal) &&
                string.Equals(r.craterName, craterName, StringComparison.Ordinal));

            if (idx < 0) return false;
            settings.records.RemoveAt(idx);
            settings.Write();
            return true;
        }

        public int ApplyToCurrentWorld(TileMutatorDef mutatorDef)
        {
            if (Find.World == null || Find.WorldGrid == null) return 0;
            var layer = Find.WorldGrid.Surface;
            var seed = Find.World.info?.seedString;
            if (layer == null || seed.NullOrEmpty()) return 0;

            int changed = ApplyToWorld(layer, seed, mutatorDef);

            Find.World.renderer?.SetAllLayersDirty();
            return changed;
        }

        public int ApplyToWorld(PlanetLayer layer, string worldSeedString, TileMutatorDef mutatorDef)
        {
            if (layer == null || worldSeedString.NullOrEmpty() || mutatorDef == null)
                return 0;

            int changedCount = 0;
            var targets = settings.records.Where(r =>
                r != null &&
                r.enabled &&
                r.IsValid() &&
                string.Equals(r.planetSeedString, worldSeedString, StringComparison.Ordinal));

            foreach (var rec in targets)
            {
                PlanetTile center = FindClosestTileByLongLat(layer, rec.longitude, rec.latitude);
                if (!center.Valid) continue;

                changedCount += ApplyMutatorRadius(layer, center, rec.radiusInTiles, mutatorDef);
            }

            return changedCount;
        }

        // ---- LongLat -> 最近 tile（遊戲本身未提供直接反查 API）
        private PlanetTile FindClosestTileByLongLat(PlanetLayer layer, float targetLon, float targetLat)
        {
            int bestId = -1;
            float bestAngle = float.MaxValue;

            // 直接掃描 tileID，和 PlanetLayer.LongLatOf(int tileID) 配合
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

            if (bestId < 0) return PlanetTile.Invalid;
            return new PlanetTile(bestId, layer);
        }

        private int ApplyMutatorRadius(PlanetLayer layer, PlanetTile center, int radius, TileMutatorDef def)
        {
            int applied = 0;

            if (radius <= 0)
            {
                if (TryAddMutator(center.Tile, def)) applied++;
                return applied;
            }

            var visited = new HashSet<PlanetTile>();
            var q = new Queue<(PlanetTile tile, int dist)>();
            var tmpNeighbors = new List<PlanetTile>(8);

            visited.Add(center);
            q.Enqueue((center, 0));

            while (q.Count > 0)
            {
                var (cur, dist) = q.Dequeue();
                if (TryAddMutator(cur.Tile, def)) applied++;

                if (dist >= radius) continue;

                tmpNeighbors.Clear();
                layer.GetTileNeighbors(cur, tmpNeighbors);

                for (int i = 0; i < tmpNeighbors.Count; i++)
                {
                    PlanetTile nb = tmpNeighbors[i];
                    if (visited.Add(nb))
                        q.Enqueue((nb, dist + 1));
                }
            }

            return applied;
        }

        private static bool TryAddMutator(Tile tile, TileMutatorDef def)
        {
            if (tile == null || def == null) return false;
            if (tile.Mutators != null && tile.Mutators.Contains(def)) return false;
            tile.AddMutator(def);
            return true;
        }

        // 大圓角距離（degree）
        private static float GreatCircleAngleDeg(float lon1Deg, float lat1Deg, float lon2Deg, float lat2Deg)
        {
            float dLonDeg = GenGeo.AngleDifferenceBetween(lon1Deg, lon2Deg);

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

        public void AddRecord(ImpactCraterRecord record)
        {
            AddOrUpdate(record); // 內含 settings.Write()
            Log.Message($"Added/Updated crater record: {record.planetSeedString} - {record.craterName}");
        }
    }
}