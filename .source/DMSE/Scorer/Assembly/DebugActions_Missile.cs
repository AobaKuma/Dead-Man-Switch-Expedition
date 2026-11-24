using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 開發工具：對當前地圖發動敵方導彈打擊。彈體與枚數由選單選擇，部件用彈體預設值
    /// （<see cref="MissileConfig.NewWithDefaults"/>）。
    /// </summary>
    public static class DebugActions_Missile
    {
        private const string Category = "DMSE Missile";
        private static readonly int[] Counts = { 1, 3, 8 };

        /// <summary>世界航跡：從約 6–10 格外發射，走完整的世界偵測、攔截與地圖交接流程。</summary>
        [DebugAction(Category, "World strike on this map...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WorldStrike()
        {
            Map map = Find.CurrentMap;
            ChooseBodyAndCount(delegate (MissileBodyDef body, int count)
            {
                Faction attacker = DebugMissileUtility.HostileFaction();
                PlanetTile source = DebugMissileUtility.TileAtDistance(map.Tile, 6f, 10f);
                if (!source.Valid)
                {
                    Messages.Message("DMSE: no launch tile 6–10 tiles away.", MessageTypeDefOf.RejectInput, false);
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    IncomingMissileUtility.Launch(source, map.Tile, MissileConfig.NewWithDefaults(body), attacker, target: map.Parent);
                }
                Messages.Message($"DMSE: {count}× {body.label} launched from {source} by {attacker?.Name ?? "no faction"}.",
                    new GlobalTargetInfo(source), MessageTypeDefOf.ThreatBig, false);
            });
        }

        /// <summary>地圖波次：直接登記為一個波次（略過世界層）；沒有防禦方雷達時直接落地。</summary>
        [DebugAction(Category, "Map salvo wave...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MapSalvo()
        {
            Map map = Find.CurrentMap;
            ChooseBodyAndCount(delegate (MissileBodyDef body, int count)
            {
                MapComponent_BVRCombat comp = map.GetComponent<MapComponent_BVRCombat>();
                Faction attacker = DebugMissileUtility.HostileFaction();
                Faction defender = comp?.ResolveDefender(attacker);
                if (defender != null && comp.RegisterSalvo(body.incomingSkyfaller, count, MissileConfig.NewWithDefaults(body), attacker, defender))
                {
                    Messages.Message($"DMSE: {count}× {body.label} registered as one wave against {defender.Name}.", MessageTypeDefOf.ThreatBig, false);
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    SpawnAt(map, DropCellFinder.RandomDropSpot(map), body, attacker);
                }
                Messages.Message($"DMSE: {count}× {body.label} falling (no active defender radar).", MessageTypeDefOf.NeutralEvent, false);
            });
        }

        /// <summary>指定落點：在點選的格子生成來襲導彈（測試彈頭與酬載結算）。</summary>
        [DebugAction(Category, "Missile impact at cell...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ImpactAtCell()
        {
            ChooseBody(delegate (MissileBodyDef body)
            {
                Faction attacker = DebugMissileUtility.HostileFaction();
                DebugTools.curTool = new DebugTool("Impact: " + body.label, delegate
                {
                    Map map = Find.CurrentMap;
                    IntVec3 cell = UI.MouseCell();
                    if (map != null && cell.InBounds(map)) { SpawnAt(map, cell, body, attacker); }
                });
            });
        }

        // ---- 分導（docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §16）----

        /// <summary>民兵三型＋熱核＋星光慣性＋指定載荷（預設 MIRV）。</summary>
        private static MissileConfig MirvConfig(string payload = "DMSE_AdvBallistic_Payload_MIRV")
        {
            MissileBodyDef body = DefDatabase<MissileBodyDef>.GetNamedSilentFail("DMSE_MissileBody_AdvancedBallistic");
            if (body == null) { return null; }
            MissileConfig config = new MissileConfig(body);
            foreach (string name in new[] { "DMSE_Ballistic_Warhead_Thermonuclear", "DMSE_AdvBallistic_Guidance_Stellar", payload })
            {
                MissilePartDef part = DefDatabase<MissilePartDef>.GetNamedSilentFail(name);
                if (part != null) { config.SetPart(part.category, part); }
            }
            return config;
        }

        /// <summary>敵方 MIRV 打本地圖：30–45 格外發射，讓匯流排在抵達前（約 27 格）分導。</summary>
        private static readonly string[] MirvPayloads =
            { "DMSE_AdvBallistic_Payload_MIRV", "DMSE_AdvBallistic_Payload_MIRVDecoy", "DMSE_AdvBallistic_Payload_Decoys" };

        private static void ChooseMirvPayload(System.Action<string> action)
        {
            Find.WindowStack.Add(new FloatMenu(MirvPayloads
                .Select(name => new FloatMenuOption(name, () => action(name))).ToList()));
        }

        [DebugAction(Category, "World MIRV strike on this map...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WorldMirvStrike() => ChooseMirvPayload(WorldMirvStrike);

        private static void WorldMirvStrike(string payload)
        {
            Map map = Find.CurrentMap;
            MissileConfig config = MirvConfig(payload);
            Faction attacker = DebugMissileUtility.HostileFaction();
            PlanetTile source = DebugMissileUtility.TileAtDistance(map.Tile, 30f, 45f);
            if (config == null || !source.Valid)
            {
                Messages.Message("DMSE: no MIRV config or no launch tile 30–45 tiles away.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            IncomingMissileUtility.Launch(source, map.Tile, config, attacker, target: map.Parent);
            Messages.Message($"DMSE: MIRV launched from {source} by {attacker?.Name ?? "no faction"}.",
                new GlobalTargetInfo(source), MessageTypeDefOf.ThreatBig, false);
        }

        /// <summary>玩家 MIRV：從本地圖打最近、距離至少 30 格的敵對據點（不經發射井，沒有離場彈）。</summary>
        [DebugAction(Category, "Player MIRV at nearest hostile settlement...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PlayerMirvStrike() => ChooseMirvPayload(PlayerMirvStrike);

        private static void PlayerMirvStrike(string payload)
        {
            Map map = Find.CurrentMap;
            MissileConfig config = MirvConfig(payload);
            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_WorldObject_BallisticMissile");
            Settlement target = Find.WorldObjects.Settlements
                .Where(s => s.Faction != null && s.Faction.HostileTo(Faction.OfPlayer) && s.Tile.Layer == map.Tile.Layer)
                .Select(s => (s, d: MissileWorldUtility.RangeDistance(map.Tile, s.Tile)))
                .Where(p => p.d >= 30f && (config == null || config.Range <= 0 || p.d <= config.Range))
                .OrderBy(p => p.d).Select(p => p.s).FirstOrDefault();
            if (config == null || def == null || target == null)
            {
                Messages.Message("DMSE: no MIRV config, world object def, or hostile settlement 30+ tiles away in range.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(def);
            wo.skyfallerIncoming = config.body.incomingSkyfaller;
            wo.SetFaction(Faction.OfPlayer);
            wo.Tile = map.Tile;
            wo.destinationTile = target.Tile;
            wo.targetParent = target;
            wo.config = config;
            Find.WorldObjects.Add(wo);
            Messages.Message($"DMSE: player MIRV launched at {target.Label} ({MissileWorldUtility.RangeDistance(map.Tile, target.Tile):0} tiles).",
                new GlobalTargetInfo(wo), MessageTypeDefOf.NeutralEvent, false);
        }

        private static void SpawnAt(Map map, IntVec3 cell, MissileBodyDef body, Faction attacker)
        {
            if (MissileProjectileMaker.SpawnIncoming(body.incomingSkyfaller, cell, map) is IIncomingMissile incoming)
            {
                incoming.Config = MissileConfig.NewWithDefaults(body);
                incoming.Attacker = attacker;
            }
        }

        private static IEnumerable<MissileBodyDef> StrikeBodies => DefDatabase<MissileBodyDef>.AllDefsListForReading
            .Where(b => b.incomingSkyfaller != null).OrderBy(b => b.label);

        private static void ChooseBody(System.Action<MissileBodyDef> action)
        {
            var options = StrikeBodies.Select(b => new FloatMenuOption($"{b.LabelCap} ({b.defName})", () => action(b))).ToList();
            if (options.Count == 0)
            {
                Messages.Message("DMSE: no missile body with an incoming skyfaller.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void ChooseBodyAndCount(System.Action<MissileBodyDef, int> action)
        {
            ChooseBody(body => Find.WindowStack.Add(new FloatMenu(Counts
                .Select(n => new FloatMenuOption($"{n}×", () => action(body, n))).ToList())));
        }
    }

    /// <summary>開發工具共用：敵對陣營、距離內的空 tile。</summary>
    public static class DebugMissileUtility
    {
        /// <summary>與玩家敵對的陣營（找不到就退回任一非玩家可見陣營）。</summary>
        public static Faction HostileFaction()
        {
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;
            return all.Where(f => !f.IsPlayer && !f.defeated && !f.def.hidden && f.HostileTo(Faction.OfPlayer)).RandomElementWithFallback(null)
                ?? all.FirstOrDefault(f => !f.IsPlayer && !f.def.hidden);
        }

        /// <summary>與 <paramref name="origin"/> 同圖層、距離在範圍內的隨機空 tile；<paramref name="buildable"/> 時要求可建據點。</summary>
        public static PlanetTile TileAtDistance(PlanetTile origin, float min, float max, bool buildable = false)
        {
            var candidates = new List<PlanetTile>();
            int count = origin.Layer.TilesCount;
            for (int i = 0; i < count; i++)
            {
                PlanetTile t = new PlanetTile(i, origin.Layer);
                float d = MissileWorldUtility.RangeDistance(origin, t);
                if (d < min || d > max || Find.WorldObjects.AnyWorldObjectAt(t)) { continue; }
                if (buildable)
                {
                    Tile info = Find.WorldGrid[t];
                    if (!info.PrimaryBiome.canBuildBase || info.hilliness == Hilliness.Impassable
                        || Find.WorldObjects.AnySettlementBaseAtOrAdjacent(t)) { continue; }
                }
                candidates.Add(t);
            }
            return candidates.Count > 0 ? candidates.RandomElement() : PlanetTile.Invalid;
        }
    }
}
