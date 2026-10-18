using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Wave-strike P0 (docs/BVR_波次打擊規劃.md): strike rounds, reservations, per-round launch with ripple
    /// continuation, and world defense engaging the soonest arrival first.
    /// </summary>
    public static class Tests_Strike
    {
        private static ThingDef InterceptorItem => ThingDef.Named("DMSE_InterceptorMissile");
        private static ThingDef BallisticSilo => ThingDef.Named("DMSE_Building_BallisticSilo");
        private static ThingDef LightIncoming => ThingDef.Named("DMSE_Incoming_LightMissile");
        private static MissileBodyDef LightBody => DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Light");
        private static MapComponent_StrikePlanner Planner => Home.GetComponent<MapComponent_StrikePlanner>();
        private const int DebugOwner = MapComponent_StrikePlanner.DebugOwner;

        private static Thing Load(Building_MissileRack rack, ThingDef def)
        {
            Thing missile = ThingMaker.MakeThing(def);
            if (!rack.GetDirectlyHeldThings().TryAdd(missile)) { missile.Destroy(); return null; }
            return missile;
        }

        private static Command FireGizmo(CompMissileLauncher_WorldTargeting launcher)
            => launcher.CompGetGizmosExtra().OfType<Command_Action>().FirstOrDefault();

        public static IEnumerable<Wait> RoundsReservationAndLaunch(TestContext t)
        {
            Building_MissileRack rack = (Building_MissileRack)SpawnBuilding(LightLauncher, 10, 2);
            // The reserved missile sits first so the SAM would have taken it before P0.
            Thing a = Load(rack, LightMissileItem);
            Thing interceptor = Load(rack, InterceptorItem);
            Thing b = Load(rack, LightMissileItem);
            Thing c = Load(rack, LightMissileItem);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompMissileLauncher_Rail rail = rack.TryGetComp<CompMissileLauncher_Rail>();
            CompMissileLauncher_Interceptor sam = rack.TryGetComp<CompMissileLauncher_Interceptor>();
            t.Require(rail != null && sam != null && rack.StoredCount == 4, "light launcher holds three light missiles and an interceptor");
            t.Check(Planner.launchers.Contains(rail), "the rail registers with the strike planner");
            t.Check(rail.StrikeRounds.SequenceEqual(new[] { a, b, c }), "strike rounds skip the interceptor");
            t.Check(rail.BuildFiringConfig(interceptor) == null, "an interceptor is never turned into a strike config");
            t.Check(rail.BuildFiringConfig(a)?.body == LightBody, "a light missile fires with a light-body config");
            t.Check(rail.NextRound == a && rail.SalvoIntervalTicks == 30, "single-shot picks the first strike round; ripple interval 30");

            t.Check(Planner.TryReserve(a, DebugOwner) && StrikeReservation.IsReserved(a), "a missile inside a launcher can be reserved");
            t.Check(!Planner.TryReserve(a, 7) && StrikeReservation.OwnerOf(a) == DebugOwner, "another mission cannot take a reserved missile");
            t.Check(rail.NextRound == b, "single-shot skips the reserved missile");
            string inspect = rail.CompInspectStringExtra() ?? string.Empty;
            t.Check(inspect.Contains("DMSE.Strike.ReservedInspect".Translate(1)), "launcher inspect shows the reserved count");

            int now = Find.TickManager.TicksGame;
            t.Require(sam.ReadyToFire(now), "SAM is ready");
            t.Check(sam.FireWorldInterceptor(now) && interceptor.Destroyed && !a.Destroyed && rack.StoredCount == 3,
                "the SAM fires the interceptor behind the reserved missile, not the reserved missile");
            t.Check(Planner.TryReserve(b, DebugOwner) && Planner.TryReserve(c, DebugOwner), "reserve the remaining missiles");
            t.Check(!sam.HasAmmo, "with every missile reserved the SAM has no ammo");
            Command gizmo = FireGizmo(rail);
            t.Check(gizmo != null && gizmo.Disabled && gizmo.disabledReason == "DMSE.Strike.AllRoundsReserved".Translate(),
                "the single-shot gizmo explains that every missile is reserved");
            Planner.Release(b);
            Planner.Release(c);

            PlanetTile near = TileAtDistance(5f, 8f);
            PlanetTile far = TileAtDistance(40f, 45f);
            t.Require(near.Valid && far.Valid, "target tiles at ~6 and ~42 tiles exist");
            t.Note($"light missile range {rail.BuildFiringConfig(c)?.Range} tiles");
            ScorerProjectile_WorldObject refused = rail.TryLaunchRound(new GlobalTargetInfo(far), c, false, out string farReason);
            t.Check(refused == null && !c.Destroyed && !rail.OnCooldown(now),
                $"an out-of-range launch is refused without spending the missile or the cooldown ({farReason})");

            ScorerProjectile_WorldObject first = rail.TryLaunchRound(new GlobalTargetInfo(near), a, false, out string reason1);
            t.Check(first != null && a.Destroyed && rack.StoredCount == 2, $"the reserved missile launches by reference ({reason1 ?? "ok"})");
            t.Check(first != null && first.config?.body == LightBody && first.destinationTile == near && first.Faction == Player,
                "the world track carries the light config, target and faction");
            t.Check(rail.OnCooldown(now), "the launch starts the cooldown");
            ScorerProjectile_WorldObject blocked = rail.TryLaunchRound(new GlobalTargetInfo(near), b, false, out string reason2);
            t.Check(blocked == null && !b.Destroyed, $"a second single launch waits for the cooldown ({reason2})");
            ScorerProjectile_WorldObject ripple = rail.TryLaunchRound(new GlobalTargetInfo(near), b, true, out string reason3);
            t.Check(ripple != null && b.Destroyed && rack.StoredCount == 1, $"a ripple continuation ignores the cooldown ({reason3 ?? "ok"})");
            t.Check(rail.CooldownUntil == Find.TickManager.TicksGame + 450, "the cooldown restarts from the last ripple round");
            t.Check(Home.listerThings.AllThings.OfType<ScorerProjectile>().Count() >= 2, "both launches spawn departure skyfallers");
            Planner.ReleaseAll(DebugOwner);
        }

        public static IEnumerable<Wait> ReservedSkipAssembly(TestContext t)
        {
            Building_MissileRack rack = (Building_MissileRack)SpawnBuilding(LightLauncher, 10, 2);
            Thing missile = Load(rack, LightMissileItem);
            Thing silo = SpawnBuilding(BallisticSilo, 20, 10);
            yield return WaitPowered(); yield return new WaitTicks(2);
            Pawn colonist = Home.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            t.Require(colonist != null, "a colonist exists");

            // All of this runs inside one tick so no colonist starts the refit.
            CompMissileConfig cfg = missile.TryGetComp<CompMissileConfig>();
            cfg.EnsureInit();
            cfg.pending.SetPart(MissilePartCategory.Warhead, Part("DMSE_Light_Warhead_ContinuousRod"));
            cfg.assemblyConfirmed = true;
            t.Require(cfg.NeedsAssembly, "the missile has a confirmed pending refit");
            var giver = new WorkGiver_AssembleMissile();
            t.Check(MissileAssemblyUtility.GetAssemblyComp(rack) == cfg && giver.PotentialWorkThingsGlobal(colonist).Contains(rack),
                "an unreserved pending missile offers assembly work");
            Planner.TryReserve(missile, DebugOwner);
            t.Check(MissileAssemblyUtility.GetAssemblyComp(rack) == null && !giver.PotentialWorkThingsGlobal(colonist).Contains(rack),
                "a reserved missile offers no assembly work");
            Planner.Release(missile);
            t.Check(MissileAssemblyUtility.GetAssemblyComp(rack) == cfg, "releasing the reservation restores the work");
            cfg.pending.CopyFrom(cfg.config);
            cfg.assemblyConfirmed = false;

            CompMissileLauncher_Ballistic ballistic = silo.TryGetComp<CompMissileLauncher_Ballistic>();
            t.Require(ballistic != null, "the silo has a ballistic launcher");
            t.Check(!ballistic.StrikeRounds.Any(), "an unassembled silo has no strike round");
            silo.TryGetComp<CompMissileConfig>()?.EnsureInit();
            ballistic.MarkLoaded();
            t.Check(ballistic.StrikeRounds.SingleOrDefault() == silo && ballistic.NextRound == silo, "an assembled silo is its own strike round");
            t.Check(ballistic.BuildFiringConfig(silo)?.body == BallisticBody, "the silo fires its ballistic config");
            t.Check(Planner.TryReserve(silo, DebugOwner) && ballistic.NextRound == null, "a reserved silo has no single-shot round");
            Command gizmo = FireGizmo(ballistic);
            t.Check(gizmo != null && gizmo.Disabled && gizmo.disabledReason == "DMSE.Strike.AllRoundsReserved".Translate(),
                "the silo's fire gizmo explains that it is reserved");
            Planner.ReleaseAll(DebugOwner);
            t.Check(ballistic.NextRound == silo, "releasing the silo makes it available again");
        }

        public static IEnumerable<Wait> SoonestArrivalFirst(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack sam = SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            PlanetTile farSource = TileAtDistance(11.5f, 12.5f);
            PlanetTile nearSource = TileAtDistance(7.5f, 8.5f);
            t.Require(farSource.Valid && nearSource.Valid, "source tiles at ~12 and ~8 tiles exist");
            t.Require(HomeBVR.Channels.Free(Player) == 2, "two free fire-control channels");
            t.Require(HoldInReload(sam), "the SAM is armed but reloading, so locks stay pending");

            // Same tick, so the next scan sees all three; the first-created track arrives last.
            WorldObject_IncomingMissile late = LaunchEnemyMissile(farSource);
            WorldObject_IncomingMissile soonA = LaunchEnemyMissile(nearSource);
            WorldObject_IncomingMissile soonB = LaunchEnemyMissile(nearSource);
            t.Check(late.TicksToArrival > soonA.TicksToArrival + 300 && late.TicksToArrival > soonB.TicksToArrival + 300,
                $"the first-created track arrives last ({late.TicksToArrival} vs {soonA.TicksToArrival}/{soonB.TicksToArrival} ticks)");
            var tracks = new[] { late, soonA, soonB };
            var waitLocks = new WaitUntil(() => tracks.Count(w => w.State.lockUntil >= 0) >= 2, 300);
            yield return waitLocks;
            t.Require(!waitLocks.TimedOut, "two tracks get locked");
            t.Check(soonA.State.lockUntil >= 0 && soonB.State.lockUntil >= 0, "the two soonest arrivals hold the two channels");
            t.Check(late.State.lockUntil < 0 && late.State.detectedByFaction == Player, "the latest arrival is detected but waits for a channel");
            t.Check(HomeBVR.Channels.Free(Player) == 0, "both channels are booked");
        }

        /// <summary>
        /// §15-4 finding 2: a node without interceptor ammo does not lock (world track or map wave); a reserved
        /// missile does not count; once the last interceptor is fired, the other pending lock is released.
        /// </summary>
        public static IEnumerable<Wait> NoAmmoNoLock(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack rack = (Building_MissileRack)SpawnBuilding(LightLauncher, 10, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(!HomeBVR.HasInterceptorAmmo(Player), "an empty SAM gives no interceptor ammo");
            PlanetTile source = TileAtDistance(14f, 16f);
            t.Require(source.Valid, "a tile ~15 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);
            DropEnemyPod(1);
            int fcRange = PhaseArray.GetCompProperties<CompProperties_FireControlRadar>().maxRangeTicks;
            BVRWave wave = null;
            var waitRange = new WaitUntil(() =>
            {
                wave = HomeBVR.Waves.FirstOrDefault(w => !w.TerminalOnly);
                return wave != null && wave.tickToImpact - Find.TickManager.TicksGame < fcRange - 30;
            }, 2000);
            yield return waitRange;
            t.Require(!waitRange.TimedOut && wave.targets.Count == 1, "the pod is a map wave inside fire-control range");
            BVRTarget pod = wave.targets[0];
            yield return new WaitTicks(40);
            t.Check(wo.State.detectedByFaction == Player && wo.State.lockUntil < 0, "the world track is detected but not locked without interceptors");
            t.Check(pod.lockUntil < 0, "the map wave is not locked without interceptors");
            t.Check(HomeBVR.Channels.Free(Player) == 2, "no channel is held");

            Thing missile = Load(rack, LightMissileItem);
            Planner.TryReserve(missile, DebugOwner);
            yield return new WaitTicks(40);
            t.Check(!HomeBVR.HasInterceptorAmmo(Player) && wo.State.lockUntil < 0 && pod.lockUntil < 0,
                "a missile reserved for a strike does not arm the SAM");

            Planner.Release(missile);
            var waitLocks = new WaitUntil(() => wo.State.lockUntil >= 0 && pod.lockUntil >= 0, 120);
            yield return waitLocks;
            t.Require(!waitLocks.TimedOut, "with one interceptor loaded, both targets lock");
            t.Check(HomeBVR.Channels.Free(Player) == 0, "both channels are booked");

            var waitFired = new WaitUntil(() => rack.StoredCount == 0, 400);
            yield return waitFired;
            t.Require(!waitFired.TimedOut, "the SAM fires its only interceptor");
            bool worldEngaged = wo.State.interceptor != null;
            var waitRelease = new WaitUntil(() => worldEngaged ? pod.lockUntil < 0 : wo.State.lockUntil < 0, 60);
            yield return waitRelease;
            t.Note($"the interceptor went to the {(worldEngaged ? "world track" : "map wave")}");
            t.Check(!waitRelease.TimedOut, "the other target's pending lock is released once the last interceptor is gone");
            t.Check(HomeBVR.Channels.Free(Player) == 1, "only the in-flight interceptor still holds a channel");
            Planner.ReleaseAll(DebugOwner);
        }

        private static ScorerProjectile_WorldObject LaunchAt(Site site, MissileConfig config, string worldObjectDef, ThingDef incoming)
        {
            var wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed(worldObjectDef));
            wo.skyfallerIncoming = incoming;
            wo.SetFaction(Player);
            wo.Tile = Home.Tile;
            wo.destinationTile = site.Tile;
            wo.targetParent = site;
            wo.config = config;
            Find.WorldObjects.Add(wo);
            Track(wo);
            return wo;
        }

        /// <summary>
        /// §2-3 plan B against an unloaded NPC missile base: 8 light decoys in two waves, then 2 cruise missiles
        /// timed to arrive last. Checks the defense never takes a cruise while an earlier decoy waits, and logs
        /// how far the decoys drain the base's interceptor stock before the first arrival (which would generate
        /// the site map, so observation stops just before it).
        /// </summary>
        public static IEnumerable<Wait> DecoyWavesDrainAbstractStock(TestContext t)
        {
            Site site = SpawnMissileBase(14.5f, 15.5f);
            t.Require(site != null, "an NPC missile base ~15 tiles away");
            WorldObjectComp_AirDefense defense = site.GetComponent<WorldObjectComp_AirDefense>();
            t.Require(defense != null && defense.Available && defense.Stock == 8, "the base has abstract air defense with 8 interceptors");

            var lights = new List<ScorerProjectile_WorldObject>();
            var cruise = new List<ScorerProjectile_WorldObject>();
            int start = Find.TickManager.TicksGame;
            int firstCruiseEngage = -1, stockAtCruiseEngage = -1;
            string orderViolation = null;
            AcceptanceDriver.Watch(() =>
            {
                if (firstCruiseEngage >= 0) { return true; }
                List<ScorerProjectile_WorldObject> engaged = cruise
                    .Where(m => !m.Destroyed && (m.State.lockUntil >= 0 || m.State.interceptor != null)).ToList();
                if (engaged.Count == 0) { return false; }
                firstCruiseEngage = Find.TickManager.TicksGame;
                stockAtCruiseEngage = defense.Stock;
                int cruiseEta = engaged.Min(m => m.TicksToArrival);
                int waiting = lights.Count(l => !l.Destroyed && l.State.lockUntil < 0 && l.State.interceptor == null
                    && l.TicksToArrival < cruiseEta && l.TicksToArrival > 600);
                if (waiting > 0) { orderViolation = $"{waiting} earlier decoy(s) unengaged"; }
                return true;
            });

            MissileConfig LightConfig() => new MissileConfig(LightBody);
            for (int i = 0; i < 4; i++) { lights.Add(LaunchAt(site, LightConfig(), "DMSE_WorldObject_LightMissile", LightIncoming)); }
            yield return new WaitTicks(600);
            for (int i = 0; i < 4; i++) { lights.Add(LaunchAt(site, LightConfig(), "DMSE_WorldObject_LightMissile", LightIncoming)); }
            yield return new WaitTicks(900);
            int lastLightArrival = lights.Where(l => !l.Destroyed).Select(l => l.TicksToArrival + Find.TickManager.TicksGame).DefaultIfEmpty(0).Max();
            for (int i = 0; i < 2; i++) { cruise.Add(LaunchAt(site, CruiseConfig(), "DMSE_WorldObject_CruiseMissile", CruiseIncoming)); }
            int cruiseArrival = Find.TickManager.TicksGame + cruise[0].TicksToArrival;
            t.Note($"cruise launched at +{Find.TickManager.TicksGame - start}, arrives +{cruiseArrival - start}; last decoy arrives +{lastLightArrival - start}");
            t.Check(cruiseArrival > lastLightArrival, "plan B: the cruise wave arrives after every decoy");

            // Stop before the first arrival generates the base map.
            var waitArrival = new WaitUntil(() => lights.Concat(cruise).Where(m => !m.Destroyed)
                .Select(m => m.TicksToArrival).DefaultIfEmpty(0).Min() <= 200, 4000);
            yield return waitArrival;
            int elapsed = Find.TickManager.TicksGame - start;
            int decoyAttempts = lights.Sum(l => l.State.interceptAttempts);
            int cruiseAttempts = cruise.Sum(m => m.State.interceptAttempts);
            t.Note($"+{elapsed}: decoys destroyed {lights.Count(l => l.Destroyed)}/8 after {decoyAttempts} attempts; "
                + $"cruise destroyed {cruise.Count(m => m.Destroyed)}/2 after {cruiseAttempts} attempts; stock {defense.Stock}/8");
            t.Note(firstCruiseEngage >= 0
                ? $"first cruise engagement at +{firstCruiseEngage - start} with stock {stockAtCruiseEngage}"
                : "the cruise wave was never engaged before the first arrival");
            t.Check(orderViolation == null, "no cruise is engaged while an earlier-arriving decoy waits"
                + (orderViolation != null ? $" ({orderViolation})" : string.Empty));
            t.Check(decoyAttempts + cruiseAttempts <= 8 + 1, "attempts never exceed the stock (plus at most one restocked round)");
        }
    }
}
