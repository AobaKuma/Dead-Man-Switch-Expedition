using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>除錯動作：在當前地圖生成來自敵人的導彈打擊（測試導彈落點與彈頭/酬載結算）。</summary>
    public static class DebugActions_Missile
    {
        [DebugAction("DMSE", "Enemy missile strike (incoming, world)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyMissileStrikeIncoming()
        {
            Map map = Find.CurrentMap;
            if (map == null) { return; }

            Faction attacker = FindHostileFaction();
            PlanetTile dest = map.Tile;
            PlanetTile source = OffsetTile(dest, 6);

            IncomingMissileUtility.Launch(source, dest, BuildEnemyConfig(), attacker);
            Messages.Message("DMSE: incoming missile launched" + (attacker != null ? " from " + attacker.Name : ""),
                MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("DMSE", "Enemy missile salvo", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyMissileBarrage()
        {
            Map map = Find.CurrentMap;
            if (map == null) { return; }
            MapComponent_BVRCombat comp = map.GetComponent<MapComponent_BVRCombat>();
            if (comp == null) { return; }

            const int count = 8;
            Faction attacker = FindHostileFaction();
            Faction defender = comp.ResolveDefender(attacker);
            ThingDef incomingDef = DefDatabase<ThingDef>.GetNamedSilentFail(IncomingMissileUtility.DefaultIncomingDef);

            // 有防禦方：直接生成一個含 8 枚導彈的單一波次。
            if (defender != null && comp.RegisterSalvo(incomingDef, count, BuildEnemyConfig(), attacker, defender))
            {
                Messages.Message("DMSE: missile salvo registered as single wave (" + count + ")"
                    + (attacker != null ? " from " + attacker.Name : ""), MessageTypeDefOf.ThreatBig, false);
                return;
            }
int c = Random.Range(8,20);
            // 無運作中的防禦方搜索雷達：直接落地，無法攔截。
            for (int i = 0; i < c  && incomingDef != null; i++)
            {
                IntVec3 cell = DropCellFinder.RandomDropSpot(map);
                if (SkyfallerMaker.SpawnSkyfaller(incomingDef, cell, map) is MissileIncoming mi)
                {
                    mi.config = BuildEnemyConfig();
                    mi.attacker = attacker;
                }
            }
            Messages.Message("DMSE: missile salvo x" + count + " (no active defender radar — impacts directly)",
                MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("DMSE", "Enemy missile strike (single)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyMissileStrikeSingle()
        {
            DebugTools.curTool = new DebugTool("Enemy missile strike: pick target", delegate
            {
                Map map = Find.CurrentMap;
                if (map == null) { return; }
                IntVec3 cell = UI.MouseCell();
                if (cell.InBounds(map))
                {
                    SpawnStrike(map, cell);
                }
            });
        }

        [DebugAction("DMSE", "Enemy missile strike (volley x5)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyMissileStrikeVolley()
        {
            DebugTools.curTool = new DebugTool("Enemy missile volley: pick impact area", delegate
            {
                Map map = Find.CurrentMap;
                if (map == null) { return; }
                IntVec3 center = UI.MouseCell();
                if (!center.InBounds(map)) { return; }
                for (int i = 0; i < 5; i++)
                {
                    IntVec3 c = CellFinder.RandomClosewalkCellNear(center, map, 8);
                    SpawnStrike(map, c);
                }
            });
        }

        private static void SpawnStrike(Map map, IntVec3 cell)
        {
            MissileConfig cfg = BuildEnemyConfig();
            ThingDef def = (cfg != null && cfg.body != null ? cfg.body.incomingSkyfaller : null)
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("DMSE_Incoming_CruiseMissile");
            if (def == null)
            {
                Messages.Message("DMSE: no incoming-missile def found.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            Skyfaller faller = SkyfallerMaker.SpawnSkyfaller(def, cell, map);
            if (faller is MissileIncoming incoming)
            {
                incoming.config = cfg;
            }
        }

        /// <summary>建立預設的「敵方」導彈設定：第一個彈體 + 相容的第一個彈頭（若有）。</summary>
        private static MissileConfig BuildEnemyConfig()
        {
            MissileBodyDef body = DefDatabase<MissileBodyDef>.AllDefsListForReading.FirstOrDefault();
            if (body == null) { return null; }

            MissileConfig cfg = new MissileConfig(body);
            MissilePartDef warhead = DefDatabase<MissilePartDef>.AllDefsListForReading
                .FirstOrDefault(p => p.category == MissilePartCategory.Warhead && p.CompatibleWith(body));
            if (warhead != null)
            {
                cfg.SetPart(MissilePartCategory.Warhead, warhead);
            }
            return cfg;
        }

        /// <summary>取得一個與玩家敵對的陣營（找不到就退回任一非玩家陣營）。</summary>
        private static Faction FindHostileFaction()
        {
            Faction player = Faction.OfPlayer;
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;
            Faction hostile = all
                .Where(f => f != player && !f.defeated && !f.def.hidden && f.HostileTo(player))
                .RandomElementWithFallback(null);
            if (hostile != null) { return hostile; }
            return all.FirstOrDefault(f => f != player && !f.def.hidden);
        }

        /// <summary>從起始 tile 沿隨機相鄰 tile 走若干步，作為來襲導彈的發射來源（製造可見航跡）。</summary>
        private static PlanetTile OffsetTile(PlanetTile origin, int steps)
        {
            PlanetTile t = origin;
            WorldGrid grid = Find.WorldGrid;
            List<PlanetTile> neighbors = new List<PlanetTile>();
            for (int i = 0; i < steps; i++)
            {
                neighbors.Clear();
                grid.GetTileNeighbors(t, neighbors);
                if (neighbors.Count == 0) { break; }
                t = neighbors.RandomElement();
            }
            return t;
        }
    }
}
