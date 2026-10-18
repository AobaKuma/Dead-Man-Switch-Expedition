using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>World-layer edge cases: airburst, moving targets, CIWS power loss, player speeds, save/load after a reroute.</summary>
    public static class Tests_WorldEdge
    {
        public static IEnumerable<Wait> WorldAirburst(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            SpawnLoadedLauncher(10, 2, 1);   // a single interceptor for the whole raid
            yield return WaitPowered(); yield return new WaitTicks(2);
            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            PlanetTile beside = TileNear(Home.Tile, 0.9f, 1.8f, source);   // next to home, on the source side
            t.Require(beside.Valid, "a free tile next to home exists");

            // a and b fly together to home; other flies with them to a different tile; late follows ~3 tiles behind.
            WorldObject_IncomingMissile a = LaunchEnemyMissile(source);
            WorldObject_IncomingMissile b = LaunchEnemyMissile(source);
            WorldObject_IncomingMissile other = IncomingMissileUtility.Launch(source, beside, CruiseConfig(), Enemy, CruiseIncoming);
            Track(other);
            WorldObject_IncomingMissile primary = null;
            var waitLaunch = new WaitUntil(() =>
                (primary = new[] { a, b }.FirstOrDefault(m => !m.Destroyed && m.State.interceptor != null)) != null, 2000);
            yield return waitLaunch;
            t.Require(primary != null, "one interceptor is launched at the pair");
            WorldObject_IncomingMissile twin = primary == a ? b : a;
            WorldObject_Interceptor interceptor = primary.State.interceptor;
            t.Require(interceptor.interceptTick - Find.TickManager.TicksGame > 500, "enough time to launch the trailing missile");
            yield return new WaitTicks(450);
            WorldObject_IncomingMissile late = LaunchEnemyMissile(source);

            MissilePartDef airburst = DefDatabase<MissilePartDef>.GetNamed("DMSE_Ballistic_Warhead_Airburst");
            interceptor.warhead = airburst;
            interceptor.payloadCapacity = 10f;
            interceptor.hitChance = 1f;
            t.Check(airburst.warheadEffect.ExtraInterceptRolls(10f) == 2, "airburst warhead with payload 10 gives 2 extra rolls");
            t.Check(twin.State.interceptor == null, "the second missile has no interceptor of its own");
            int due = interceptor.interceptTick;
            TickWatch killed = AcceptanceDriver.Watch(() => primary.Destroyed);
            TickWatch twinKilled = AcceptanceDriver.Watch(() => twin.Destroyed);
            var waitKill = new WaitUntil(() => killed.Seen, due - Find.TickManager.TicksGame + 60);
            yield return waitKill;
            t.Require(killed.FirstTick == due, $"the interceptor kills its target at the meeting tick ({killed.FirstTick} vs {due})");

            float radius = BVRWorldTuningDef.Current.airburstRadiusTiles;
            float otherDist = MissileWorldUtility.SurfaceTilesBetween(other.PositionAt(0), interceptor.end);
            float lateDist = MissileWorldUtility.SurfaceTilesBetween(late.PositionAt(0), interceptor.end);
            t.Check(twinKilled.FirstTick == due, "the airburst also kills the missile flying with it");
            t.Check(otherDist <= radius, $"the missile bound elsewhere is inside the burst radius ({otherDist:0.00} ≤ {radius})");
            t.Check(!other.Destroyed, "the burst spares a missile bound for another tile");
            t.Check(lateDist > radius, $"the trailing missile is outside the burst radius ({lateDist:0.00} > {radius})");
            t.Check(!late.Destroyed, "the burst spares a missile outside its radius");
        }

        /// <summary>The target moves while a guided missile is inbound (e.g. a transfer ship): lock, interceptor and re-engagement.</summary>
        public static IEnumerable<Wait> TerminalUpdateReroute(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            WorldObjectComp_AirDefense defense = site.GetComponent<WorldObjectComp_AirDefense>();
            yield return new WaitTicks(20);
            int stockBefore = defense.Stock;
            MissileConfig cfg = CruiseConfig("DMSE_Guidance_EMSignal");
            float startDistance = MissileWorldUtility.RangeDistance(Home.Tile, site.Tile);
            t.Require(cfg.TerminalUpdates == 3, $"cruise body allows 3 terminal updates (got {cfg.TerminalUpdates})");
            t.Require(cfg.Range == 0 || cfg.Range >= startDistance + 6f, $"range {cfg.Range} covers {startDistance:0.0} tiles plus two moves");
            PlanetTile tileA = site.Tile;
            PlanetTile tileB = TileNear(tileA, 1.5f, 2.5f, PlanetTile.Invalid);
            PlanetTile tileC = TileNear(tileB, 1.5f, 2.5f, PlanetTile.Invalid, tileA);
            t.Require(tileB.Valid && tileC.Valid, "free tiles for two moves exist");
            ScorerProjectile_WorldObject wo = LaunchPlayerMissile(tileA, site, cfg);

            // 1. The base moves while its fire control is still locking: the lock restarts on the new route.
            var waitLock = new WaitUntil(() => wo.Destroyed || wo.State.lockUntil >= 0, 3000);
            yield return waitLock;
            t.Require(!wo.Destroyed && wo.State.lockUntil >= 0 && wo.State.interceptor == null, "the base starts locking the missile");
            int oldLockUntil = wo.State.lockUntil;
            site.Tile = tileB;
            var waitReroute = new WaitUntil(() => wo.RouteRevision >= 1, MissileWorldUtility.TerminalCheckIntervalTicks + 30);
            yield return waitReroute;
            t.Require(wo.RouteRevision == 1 && wo.Destination == tileB, "the missile follows the base to its new tile");
            t.Check(wo.TerminalUpdatesRemaining == 2, $"one terminal update used ({wo.TerminalUpdatesRemaining}/3 left)");
            yield return new WaitTicks(20);
            t.Check(wo.State.interceptor == null && wo.State.lockUntil != oldLockUntil,
                $"the old lock is dropped and restarted (was due {oldLockUntil}, now {wo.State.lockUntil})");

            // 2. The base moves again while an interceptor is flying: that interceptor misses at the old point.
            var waitFirst = new WaitUntil(() => wo.Destroyed || wo.State.interceptor != null, 2000);
            yield return waitFirst;
            t.Require(!wo.Destroyed && wo.State.interceptor != null, "the base launches an interceptor on the new route");
            WorldObject_Interceptor first = wo.State.interceptor;
            first.hitChance = 1f;
            t.Check(first.targetDestination == tileB && first.targetRevision == 1, "the interceptor is aimed at the current route");
            site.Tile = tileC;
            var waitReroute2 = new WaitUntil(() => wo.RouteRevision >= 2 || first.Destroyed, MissileWorldUtility.TerminalCheckIntervalTicks + 30);
            yield return waitReroute2;
            t.Require(wo.RouteRevision == 2 && !first.Destroyed, "the missile changes course while the interceptor is still flying");
            TickWatch firstDone = AcceptanceDriver.Watch(() => first.Destroyed);
            var waitFirstDone = new WaitUntil(() => firstDone.Seen, first.interceptTick - Find.TickManager.TicksGame + 60);
            yield return waitFirstDone;
            t.Check(firstDone.FirstTick == first.interceptTick, "the interceptor resolves at its meeting tick");
            t.Check(!wo.Destroyed, "a certain hit still misses because the missile changed course");

            // 3. The base's defense takes over on the latest route.
            var waitSecond = new WaitUntil(() => wo.Destroyed || (wo.State.interceptor != null && wo.State.interceptor != first), 2500);
            yield return waitSecond;
            t.Require(!wo.Destroyed && wo.State.interceptor != null, "the base re-engages on the latest route");
            WorldObject_Interceptor second = wo.State.interceptor;
            t.Check(second.targetDestination == tileC && second.targetRevision == 2, "the new interceptor is aimed at the latest route");
            t.Check(wo.State.interceptAttempts == 2, $"two attempts counted (got {wo.State.interceptAttempts})");
            t.Check(defense.Stock == stockBefore - 2, $"two abstract interceptors used ({stockBefore} → {defense.Stock})");
            second.hitChance = 1f;
            var waitKill = new WaitUntil(() => wo.Destroyed, second.interceptTick - Find.TickManager.TicksGame + 60);
            yield return waitKill;
            t.Check(wo.Destroyed && !site.HasMap, "the second interceptor kills the missile before it reaches the base");
        }

        public static IEnumerable<Wait> TerminalCiwsPowerLoss(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(10, 2, 4);
            Thing ciws = SpawnBuilding(WingBack, 14, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompProperties_TerminalCIWS props = WingBack.GetCompProperties<CompProperties_TerminalCIWS>();
            float oldChance = props.interceptChance, oldDebris = props.debrisChance;
            Override(() => { props.interceptChance = 0f; props.debrisChance = 0f; },
                () => { props.interceptChance = oldChance; props.debrisChance = oldDebris; });

            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);
            var waitArrive = new WaitUntil(() =>
            {
                WorldObject_Interceptor i = wo.Destroyed ? null : wo.State.interceptor;
                if (i != null && i.hitChance > 0f) { i.hitChance = 0f; }   // force every world shot to miss
                return wo.Destroyed;
            }, 5000);
            yield return waitArrive;
            t.Require(HomeBVR.Waves.Count == 1 && HomeBVR.Waves[0].TerminalOnly, "the arrival is held in one terminal-only wave");
            BVRWave wave = HomeBVR.Waves[0];
            int stored = launcher.StoredCount;

            yield return new WaitTicks(60);
            SetFlick(ciws, false);
            yield return new WaitTicks(400);
            t.Check(HomeBVR.Waves.Contains(wave) && wave.TerminalOnly, "with the CIWS off the wave stays terminal-only");
            t.Check(wave.tickToImpact > Find.TickManager.TicksGame, "the missile is still held until its impact tick");
            t.Check(launcher.StoredCount == stored, "SAM does not take over while the CIWS is off");

            props.interceptChance = 1f;
            SetFlick(ciws, true);
            var waitDone = new WaitUntil(() => !HomeBVR.Waves.Contains(wave), wave.tickToImpact - Find.TickManager.TicksGame + 300);
            yield return waitDone;
            t.Check(!waitDone.TimedOut, "the terminal wave resolves");
            yield return new WaitTicks(30);
            t.Check(!Home.listerThings.ThingsOfDef(CruiseIncoming).Any(), "the CIWS resumes once powered and destroys the missile");
            t.Check(launcher.StoredCount == stored, "SAM never fired during the terminal phase");
        }

        /// <summary>Flight time and ETA at the player's speeds (pause, 1×, 2×, 3×) and while switching between world and map.</summary>
        public static IEnumerable<Wait> EtaAtPlayerSpeeds(TestContext t)
        {
            PlanetTile source = TileAtDistance(7f, 8f);
            t.Require(source.Valid, "a tile ~7 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);
            int predicted = Find.TickManager.TicksGame + wo.TicksToArrival;
            TickWatch arrived = AcceptanceDriver.Watch(() => wo.Destroyed);
            t.allowPause = true;

            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            int etaPaused = wo.TicksToArrival;
            yield return new WaitFrames(60);
            t.Check(wo.TicksToArrival == etaPaused, "ETA does not move while paused");

            var phases = new (TimeSpeed speed, bool world, int ticks)[]
            {
                (TimeSpeed.Normal, false, 240), (TimeSpeed.Normal, true, 240), (TimeSpeed.Fast, true, 240), (TimeSpeed.Superfast, false, 120),
            };
            foreach (var phase in phases)
            {
                if (phase.world) { CameraJumper.TryShowWorld(); } else { CameraJumper.TryHideWorld(); }
                Find.TickManager.CurTimeSpeed = phase.speed;
                yield return new WaitTicks(phase.ticks);
                int drift = Find.TickManager.TicksGame + wo.TicksToArrival - predicted;
                t.Check(!wo.Destroyed && Mathf.Abs(drift) <= 2,
                    $"ETA holds at {phase.speed}{(phase.world ? " in world view" : "")} (drift {drift})");
            }
            var waitArrive = new WaitUntil(() => arrived.Seen, predicted - Find.TickManager.TicksGame + 300);
            yield return waitArrive;
            CameraJumper.TryHideWorld();
            t.allowPause = false;
            t.Require(arrived.Seen, "the missile arrives");
            t.Check(Mathf.Abs(arrived.FirstTick - predicted) <= 15, $"arrival {arrived.FirstTick} within 15 ticks of predicted {predicted}");
        }

        // ------------------------------------------------------------------
        //  Save/load after a reroute (replaces the game; runs after L1)
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> SaveLoadAfterReroute(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            yield return new WaitTicks(20);
            PlanetTile tileA = site.Tile;
            PlanetTile tileB = TileNear(tileA, 1.5f, 2.5f, PlanetTile.Invalid);
            t.Require(tileB.Valid, "a free tile next to the base exists");
            ScorerProjectile_WorldObject wo = LaunchPlayerMissile(tileA, site, CruiseConfig("DMSE_Guidance_EMSignal"));
            var waitFirst = new WaitUntil(() => wo.Destroyed || wo.State.interceptor != null, 3000);
            yield return waitFirst;
            t.Require(!wo.Destroyed && wo.State.interceptor != null, "the base launches an interceptor");
            WorldObject_Interceptor first = wo.State.interceptor;
            first.hitChance = 1f;
            site.Tile = tileB;
            var waitReroute = new WaitUntil(() => wo.RouteRevision >= 1 || first.Destroyed, MissileWorldUtility.TerminalCheckIntervalTicks + 30);
            yield return waitReroute;
            t.Require(wo.RouteRevision == 1 && !first.Destroyed, "the missile reroutes while the interceptor is flying");

            int trackId = wo.ID, interceptorId = first.ID, siteId = site.ID, due = first.interceptTick;
            int remaining = wo.TerminalUpdatesRemaining;
            int predictedArrival = Find.TickManager.TicksGame + wo.TicksToArrival;
            int stock = site.GetComponent<WorldObjectComp_AirDefense>().Stock;
            RememberEnemyForReload();
            t.Note($"saving with track #{trackId} rerouted to {tileB}, interceptor #{interceptorId} due {due}, {remaining} updates left");

            Game before = Current.Game;
            GameDataSaveLoader.SaveGame(TestSaveName);
            yield return new WaitFrames(10);
            GameDataSaveLoader.LoadGame(TestSaveName);
            var waitReload = new WaitUntil(() => Current.Game != before && Current.ProgramState == ProgramState.Playing, 999999);
            yield return waitReload;
            yield return new WaitFrames(5);

            ScorerProjectile_WorldObject track = Find.WorldObjects.AllWorldObjects.OfType<ScorerProjectile_WorldObject>()
                .FirstOrDefault(x => x.ID == trackId);
            Site base2 = Find.WorldObjects.AllWorldObjects.OfType<Site>().FirstOrDefault(x => x.ID == siteId);
            t.Require(track != null && base2 != null, "track and base restored after load");
            Track(track);
            Track(base2);
            t.Check(base2.Tile == tileB, "the base is still on its new tile");
            t.Check(track.targetParent == base2, "the missile still targets the base");
            t.Check(track.Destination == tileB && track.RouteRevision == 1, "the new route is restored");
            t.Check(track.TerminalUpdatesRemaining == remaining, $"remaining terminal updates restored ({track.TerminalUpdatesRemaining})");
            int drift = Find.TickManager.TicksGame + track.TicksToArrival - predictedArrival;
            t.Check(Mathf.Abs(drift) <= 2, $"ETA on the new route survives loading (drift {drift})");
            WorldObject_Interceptor restored = track.State.interceptor;
            t.Check(restored != null && restored.ID == interceptorId, "in-flight interceptor reference restored");
            t.Check(restored != null && restored.targetRevision == 0 && restored.targetDestination == tileA,
                "the interceptor still aims at the old route");
            t.Check(base2.GetComponent<WorldObjectComp_AirDefense>().Stock == stock, "abstract stock unchanged by loading");

            var waitResolve = new WaitUntil(() => track.Destroyed || track.State.interceptor == null || track.State.interceptor.ID != interceptorId,
                due - Find.TickManager.TicksGame + 60);
            yield return waitResolve;
            t.Check(!waitResolve.TimedOut && !track.Destroyed, "after loading the interceptor still misses the rerouted missile");
            var waitSecond = new WaitUntil(() => track.Destroyed || (track.State.interceptor != null && track.State.interceptor.ID != interceptorId), 2500);
            yield return waitSecond;
            t.Check(!track.Destroyed && track.State.interceptor != null && track.State.interceptor.targetRevision == 1,
                "the base re-engages on the restored route");
        }
    }
}
