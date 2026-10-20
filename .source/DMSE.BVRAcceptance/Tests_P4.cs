using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// P4 world-layer expansion: datalink / area defense, generic settlement air defense,
    /// hidden undetected tracks, anti-radiation degradation of an abstract node and drop-pod ghost tracks.
    /// </summary>
    public static class Tests_P4
    {
        /// <summary>D1: a home radar intercepts a track that is not heading to the home tile (area defense + general solver).</summary>
        public static IEnumerable<Wait> AreaDefense(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            SpawnLoadedLauncher(10, 2, 1);
            yield return WaitPowered(); yield return new WaitTicks(2);
            // A destination a few tiles from home guarantees the track crosses the home envelope
            // without being the home tile itself.
            PlanetTile target = TileAtDistance(3f, 4f);
            PlanetTile source = TileAtDistance(18f, 21f);
            t.Require(target.Valid && source.Valid, "a source and a destination away from home exist");

            var missile = IncomingMissileUtility.Launch(source, target, CruiseConfig(), Enemy, CruiseIncoming);
            Track(missile);
            var wait = new WaitUntil(() => missile.Destroyed || missile.State.interceptor != null, 6000);
            yield return wait;
            t.Require(!missile.Destroyed, "the track survives long enough to be engaged");
            t.Note($"lockNode={missile.State.lockNode?.Label ?? "null"} launchNode={missile.State.interceptor?.launchNode?.Label ?? "null"} "
                + $"home={Home.Parent?.Label ?? "null"} detected={missile.State.detectedByFaction?.Name ?? "null"}");
            t.Check(missile.Destination == target, "the track keeps its own destination");
            t.Check(missile.State.interceptor != null, "a track crossing an envelope is intercepted");
            MapParent launchNode = missile.State.interceptor?.launchNode;
            t.Check(launchNode != null && launchNode.Tile != missile.Destination,
                "the intercept came from a node the track was not heading to (area defense)");
            t.Check(launchNode != null && launchNode.Faction != null && launchNode.Faction.HostileTo(Enemy),
                "the intercepting node is hostile to the attacker");
        }

        /// <summary>D2: a plain enemy settlement gets a synthetic air defense by tech level, and degrading it takes the radar down.</summary>
        public static IEnumerable<Wait> SettlementDefense(TestContext t)
        {
            var settlement = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            settlement.Tile = TileAtDistance(9f, 11f);
            settlement.SetFaction(Enemy);
            Find.WorldObjects.Add(settlement);
            yield return null;

            IBVRDefense defense = WorldComponent_SettlementDefense.Current?.DefenseFor(settlement);
            t.Require(defense != null, "a plain enemy settlement gets a synthetic defense");
            t.Check(defense.Available && defense.Channels >= 1 && defense.Stock > 0, "the synthetic defense is available with stock");
            t.Check(defense.DetectionRange(BVRTargetProps.Default) > 0f, "the synthetic defense has a detection range");

            int before = defense.Stock;
            defense.Degrade(Find.TickManager.TicksGame);
            t.Check(defense.Stock == Mathf.Max(0, before - 2), "an anti-radiation strike takes two interceptors");
            t.Check(!defense.RadarOnline && defense.DetectionRange(BVRTargetProps.Default) == 0f,
                "the strike suppresses the radar for a day");
            t.Check(!defense.Ready(Find.TickManager.TicksGame), "a suppressed node cannot launch");

            Find.WorldObjects.Remove(settlement);
        }

        /// <summary>D3: an undetected hostile track is hidden from the player until a radar sees it.</summary>
        public static IEnumerable<Wait> HiddenTracks(TestContext t)
        {
            var missile = IncomingMissileUtility.Launch(TileAtDistance(19f, 22f), Home.Tile, CruiseConfig(), Enemy, CruiseIncoming);
            Track(missile);
            yield return null;
            missile.State.detectedByFaction = null;
            missile.State.playerNotified = false;
            t.Check(!missile.VisibleToPlayer, "an undetected hostile track is hidden");
            missile.State.detectedByFaction = Faction.OfPlayer;
            t.Check(missile.VisibleToPlayer, "a detected track becomes visible");
            missile.Destroy();
        }

        /// <summary>D4: a drop-pod ghost track interpolates from the ring edge, and the anti-radiation strike degrades a real abstract node.</summary>
        public static IEnumerable<Wait> GhostTrackAndAbstractNode(TestContext t)
        {
            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_DropPodTrack");
            t.Require(def != null, "the drop-pod ghost def exists");
            var ghost = (WorldObject_DropPodTrack)WorldObjectMaker.MakeWorldObject(def);
            ghost.Tile = Home.Tile;
            ghost.SetFaction(Enemy);
            ghost.spawnTick = Find.TickManager.TicksGame;
            ghost.arrivalTick = ghost.spawnTick + 600;
            ghost.startPos = new Vector3(100, 0, 0);
            ghost.endPos = new Vector3(0, 0, 100);
            ghost.targetTile = Home.Tile;
            Find.WorldObjects.Add(ghost);
            yield return null;

            t.Check(ghost.TicksToArrival <= 600 && ghost.TicksToArrival > 500, "the ghost uses its own arrival tick");
            Vector3 atStart = ghost.PositionAt(0);
            t.Check(Vector3.Distance(atStart.normalized, ghost.startPos.normalized)
                    < Vector3.Distance(atStart.normalized, ghost.endPos.normalized),
                "the ghost starts near the ring edge");
            Vector3 atEnd = ghost.PositionAt(600);
            t.Check(Vector3.Distance(atEnd.normalized, ghost.endPos.normalized)
                    < Vector3.Distance(atEnd.normalized, ghost.startPos.normalized),
                "the ghost ends near the map tile");
            ghost.Destroy();

            var site = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Site);
            site.Tile = TileAtDistance(7f, 9f);
            site.SetFaction(Enemy);
            Find.WorldObjects.Add(site);
            yield return null;
            var comp = new WorldObjectComp_AirDefense
            {
                parent = site,
                props = new CompProperties_WorldAirDefense { interceptorStock = 8, restockPerDay = 0, launchCooldownTicks = 0 },
            };
            t.Require(comp.Available, "an ungenerated enemy site is an abstract node");
            int before = comp.Stock;
            comp.Degrade(Find.TickManager.TicksGame);
            t.Check(comp.Stock == before - 2 && !comp.RadarOnline, "degrading an abstract node removes stock and the radar");
            Find.WorldObjects.Remove(site);
        }
    }
}
