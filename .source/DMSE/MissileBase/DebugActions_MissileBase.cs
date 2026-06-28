using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// DevAction：在射程內生成 NPC 導彈陣地、觸發即時射擊等偵錯工具。
    /// </summary>
    public static class DebugActions_MissileBase
    {
        // ──────────────── 生成站點 ────────────────

        [DebugAction("DMSE", "Spawn NPC Missile Base (near player)",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnMissileBaseNearPlayer()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            WorldObjectDef baseDef = DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_MissileBase");
            if (baseDef == null)
            {
                Log.Error("[DMSE] WorldObjectDef 'DMSE_MissileBase' 找不到。");
                return;
            }

            // FindNearbyTile 返回 int（tile 索引），-1 表示失敗，與 FFF 模式一致
            int targetTileId = FindNearbyTileId(map.Tile, minDist: 5, maxDist: 15);
            if (targetTileId < 0)
            {
                Messages.Message("DMSE: 找不到合適 tile，無法生成導彈陣地。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            Site site = (Site)WorldObjectMaker.MakeWorldObject(baseDef);
            site.Tile = targetTileId;  // int 隱式轉換為 PlanetTile

            Faction faction = FindHostileFaction();
            if (faction != null) site.SetFaction(faction);

            // 附加 SitePart
            SitePartDef partDef = DefDatabase<SitePartDef>.GetNamedSilentFail("DMSE_MissileBaseSitePart");
            if (partDef != null)
            {
                SitePartParams parms = new SitePartParams { points = 2000f };
                site.AddPart(new SitePart(site, partDef, parms));
            }

            Find.WorldObjects.Add(site);

            Messages.Message(
                $"DMSE: NPC 導彈陣地已在 tile {targetTileId} 生成" +
                (faction != null ? $"（{faction.Name}）" : ""),
                MessageTypeDefOf.PositiveEvent, false);
        }

        // ──────────────── 即時射擊 ────────────────

        [DebugAction("DMSE", "Missile Base: Fire at player NOW (nearest base)",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TriggerImmediateMissileStrike()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            WorldObject nearestBase = Find.WorldObjects.AllWorldObjects
                .Where(wo => wo.GetComponent<WorldObjectComp_MissileBase>() != null)
                .OrderBy(wo => Find.WorldGrid.ApproxDistanceInTiles(map.Tile, wo.Tile))
                .FirstOrDefault();

            if (nearestBase == null)
            {
                Messages.Message("DMSE: 找不到任何 NPC 導彈陣地。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            WorldObjectComp_MissileBase comp = nearestBase.GetComponent<WorldObjectComp_MissileBase>();
            MissileConfig cfg = comp?.BuildFireConfig();
            if (cfg == null)
            {
                Messages.Message("DMSE: 無法建立 MissileConfig（缺少 MissileBodyDef？）。",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            IncomingMissileUtility.Launch(nearestBase.Tile, map.Tile, cfg, nearestBase.Faction);
            Messages.Message(
                $"DMSE: 來自 tile {nearestBase.Tile} 的導彈已發射。",
                MessageTypeDefOf.ThreatBig, false);
        }

        // ──────────────── 列出所有站點 ────────────────

        [DebugAction("DMSE", "Missile Base: List all bases",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.Playing)]
        private static void ListAllMissileBases()
        {
            var bases = Find.WorldObjects.AllWorldObjects
                .Where(wo => wo.GetComponent<WorldObjectComp_MissileBase>() != null)
                .ToList();

            if (bases.Count == 0)
            {
                Log.Message("[DMSE MissileBase] 目前沒有任何 NPC 導彈陣地。");
                return;
            }

            foreach (WorldObject wo in bases)
            {
                WorldObjectComp_MissileBase comp = wo.GetComponent<WorldObjectComp_MissileBase>();
                bool hasMap = wo is MapParent mp && mp.HasMap;
                Log.Message($"[DMSE MissileBase] tile={wo.Tile} faction={wo.Faction?.Name ?? "null"} " +
                            $"mapGenerated={hasMap}");
            }
        }

        // ──────────────── 私有工具 ────────────────

        /// <summary>從 <paramref name="origin"/> 出發隨機步進，尋找可建設且空置的 tile，返回 tile 索引，-1 表示失敗。</summary>
        private static int FindNearbyTileId(PlanetTile origin, int minDist, int maxDist)
        {
            WorldGrid grid = Find.WorldGrid;
            List<PlanetTile> neighbors = new List<PlanetTile>();

            for (int attempt = 0; attempt < 300; attempt++)
            {
                PlanetTile tile = origin;
                int steps = Rand.RangeInclusive(minDist, maxDist);

                for (int i = 0; i < steps; i++)
                {
                    neighbors.Clear();
                    grid.GetTileNeighbors(tile, neighbors);
                    if (neighbors.Count == 0) break;
                    tile = neighbors.RandomElement();
                }

                Tile t = grid[tile];
                if (!t.PrimaryBiome.canBuildBase) continue;
                if (t.hilliness == Hilliness.Impassable) continue;
                if (Find.WorldObjects.AnyWorldObjectAt(tile)) continue;
                if (Find.WorldObjects.AnySettlementBaseAtOrAdjacent(tile)) continue;

                // tile 隱式轉換為 int
                return tile;
            }
            return -1;
        }

        private static Faction FindHostileFaction()
        {
            return Find.FactionManager.AllFactionsListForReading
                .FirstOrDefault(f => !f.IsPlayer && !f.defeated && !f.def.hidden
                                     && f.HostileTo(Faction.OfPlayer));
        }
    }
}
