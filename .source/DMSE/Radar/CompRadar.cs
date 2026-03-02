using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompRadar : ThingComp
    {
        public CompProperties_Radar Props => (CompProperties_Radar)props;

        private List<WorldObject_Transfer> detectedTargets = new List<WorldObject_Transfer>();
        private List<WorldObject_Transfer> lockedTargets = new List<WorldObject_Transfer>();

        // Radar coverage calculation cache
        private Dictionary<WorldObject_Transfer, float> targetCoverageCache = new Dictionary<WorldObject_Transfer, float>();

        public List<WorldObject_Transfer> DetectedTargets => detectedTargets;
        public List<WorldObject_Transfer> LockedTargets => lockedTargets;

        private CompPowerTrader powerComp;
        
        /// <summary>
        /// Calculate radar coverage for a target using guidance law
        /// </summary>
        private float CalculateTargetCoverage(WorldObject_Transfer target)
        {
            if (target == null || parent.Map == null)
                return 0f;

            int distanceTiles = Find.WorldGrid.TraversalDistanceBetween(parent.Map.Tile, target.Tile);
            
            // Calculate radar cross section based on distance and coverage strength
            float h = Props.antiStealthLevel + Props.irradiationPower; // Effective cross section
            float w = Mathf.Max(0, h - distanceTiles); // Coverage strength decreases with distance
            
            return RadarUtility.CalculateRadarCrossSection(h, w);
        }

        public override void CompTick()
        {
            base.CompTick();

            if (!parent.Spawned || parent.Map == null) return;
            if (powerComp == null || !powerComp.PowerOn) return;

            Map currentMap = parent.Map;

            // Cleanup destroyed targets
            detectedTargets.RemoveAll(t => t == null || t.Destroyed);
            lockedTargets.RemoveAll(t => t == null || t.Destroyed);
            
            // Clean up cache for destroyed targets
            List<WorldObject_Transfer> keysToRemove = new List<WorldObject_Transfer>();
            foreach (var kvp in targetCoverageCache)
            {
                if (kvp.Key == null || kvp.Key.Destroyed)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            foreach (var key in keysToRemove)
            {
                targetCoverageCache.Remove(key);
            }

            // Every 30 ticks, search for incoming Skyfallers
            if (Find.TickManager.TicksGame % 30 == 0)
            {
                SearchForTargets(currentMap);
            }

            // Update visibility of detected targets
            UpdateTargetVisibility();

            // Update locked target visibility
            UpdateLockedTargetVisibility();
        }

        private void SearchForTargets(Map map)
        {
            // Get all world objects and filter by distance
            int currentTile = map.Tile;
            detectedTargets.Clear();
            targetCoverageCache.Clear();

            // Iterate all world transfer objects already being tracked
            foreach (WorldObject_Transfer transfer in Patch_Visible.WO)
            {
                if (transfer != null && !transfer.Destroyed)
                {
                    // Check distance from current tile within searchTileRadius
                    int distanceTiles = Find.WorldGrid.TraversalDistanceBetween(currentTile, transfer.Tile);
                    if (distanceTiles <= Props.searchTileRadius)
                    {
                        // Check stealth level vs anti-stealth level
                        // If antiStealthLevel >= stealth level, it's detectable
                        int stealthLevel = GetSkyfallerStealthLevel(transfer);

                        if (Props.antiStealthLevel >= stealthLevel)
                        {
                            if (!detectedTargets.Contains(transfer))
                            {
                                detectedTargets.Add(transfer);
                            }

                            // Cache the coverage value
                            float coverage = CalculateTargetCoverage(transfer);
                            targetCoverageCache[transfer] = coverage;
                        }
                    }
                }
            }
        }

        private int GetSkyfallerStealthLevel(WorldObject_Transfer transfer)
        {
            // Get stealth level from the transfer's worldObject
            // For now, returning 0 as default (no stealth)
            // This can be extended based on actual Skyfaller definition
            return 0;
        }

        private void UpdateTargetVisibility()
        {
            // Targets detected by radar are hidden from normal worldmap display
            foreach (WorldObject_Transfer target in detectedTargets)
            {
                if (target != null && !target.Destroyed)
                {
                    // Make target invisible on the world map while radar tracking
                    if (!Patch_Visible.WO.Contains(target))
                    {
                        Patch_Visible.WO.Add(target);
                    }
                }
            }

            // Remove targets that are no longer detected
            Patch_Visible.WO.RemoveAll(t => t == null || t.Destroyed || !detectedTargets.Contains(t));
        }

        private void UpdateLockedTargetVisibility()
        {
            // Fire control radar can lock up to maxTargetLock targets
            lockedTargets.RemoveAll(t => t == null || t.Destroyed);

            // Keep only the maximum allowed locked targets
            while (lockedTargets.Count > Props.maxTargetLock)
            {
                lockedTargets.RemoveAt(lockedTargets.Count - 1);
            }
        }

        public void LockTarget(WorldObject_Transfer target)
        {
            if (target != null && !target.Destroyed && detectedTargets.Contains(target))
            {
                if (!lockedTargets.Contains(target) && lockedTargets.Count < Props.maxTargetLock)
                {
                    lockedTargets.Add(target);
                }
            }
        }

        public void UnlockTarget(WorldObject_Transfer target)
        {
            if (lockedTargets.Contains(target))
            {
                lockedTargets.Remove(target);
            }
        }

        public bool IsTargetInAimRange(WorldObject_Transfer target)
        {
            if (parent.Map == null || target == null)
                return false;

            int distanceTiles = Find.WorldGrid.TraversalDistanceBetween(parent.Map.Tile, target.Tile);
            return distanceTiles <= Props.aimTileRadius;
        }

        public bool IsTargetInCloseDefenseRange(IntVec3 targetPos)
        {
            // Close defense range is based on proximity, typically for ground targets
            // This would be for nearby ground-based interception
            return targetPos.InBounds(parent.Map) && 
                   parent.Position.DistanceTo(targetPos) <= Props.aimTileRadius * 4;
        }

        /// <summary>
        /// Get cached coverage value for a target
        /// </summary>
        public float GetTargetCoverage(WorldObject_Transfer target)
        {
            if (target == null)
                return 0f;

            if (targetCoverageCache.TryGetValue(target, out float coverage))
                return coverage;

            return CalculateTargetCoverage(target);
        }
    }

    public class CompProperties_Radar : CompProperties
    {
        public CompProperties_Radar()
        {
            compClass = typeof(CompRadar);
        }

        // Search phase parameters
        public int searchTileRadius = 10; // 搜索雷達在大地圖上的搜索半徑。
        
        // Fire control targeting phase parameters
        public int aimTileRadius = 10; // 火控雷達使用，可進入瞄準狀態的半徑。
        
        // Anti-stealth parameters
        public int antiStealthLevel = 0; // 反隱形等級，與Skyfaller的隱身等級對沖。
        public int irradiationPower = 5; // 照射強度，與Skyfaller的隱身等級對沖，當照射強度大於隱身等級時，會顯示被照射的Skyfaller。

        // Aiming parameters
        public float baseAimTime = 60f; // 基礎瞄準時間，單位為秒。
        public int maxTargetLock = 3; // 最大鎖定目標數量，可同時引導防空導彈攔截的目標數量。
    }
}
