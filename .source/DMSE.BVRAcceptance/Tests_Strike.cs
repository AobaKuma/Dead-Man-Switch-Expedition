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

        // ------------------------------------------------------------------
        //  P1: auto strike console
        // ------------------------------------------------------------------

        private static ThingDef StrikeCenter => ThingDef.Named("DMSE_StrikeCommandCenter");
        private static ThingDef LoiterLauncher => ThingDef.Named("DMSE_Building_LoiteringLauncher");
        private static ThingDef Termite => ThingDef.Named("DMSE_Missile_Termite");

        private static StrikeWave WaveOf(Building_MissileRack rack, int take = int.MaxValue)
        {
            var wave = new StrikeWave();
            foreach (Thing round in rack.TryGetComp<CompMissileLauncher_Rail>().StrikeRounds.Take(take).ToList())
            {
                wave.slots.Add(new StrikeSlot { launcher = rack, round = round });
            }
            return wave;
        }

        public static IEnumerable<Wait> AutoConsoleWaves(TestContext t)
        {
            Thing centerThing = SpawnBuilding(StrikeCenter, 20, 20);
            SpawnBuilding(PhaseArray, 2, 2);
            SpawnBuilding(PhaseArray, 2, 8);
            Building_MissileRack a = SpawnLoadedLauncher(10, 2, 4);
            Building_MissileRack b = SpawnLoadedLauncher(14, 2, 4);
            Building_MissileRack c = SpawnLoadedLauncher(18, 2, 4);
            Building_MissileRack samOnly = (Building_MissileRack)SpawnBuilding(LightLauncher, 22, 2);
            Load(samOnly, InterceptorItem);
            Building_MissileRack loiter = (Building_MissileRack)SpawnBuilding(LoiterLauncher, 26, 2);
            Thing termite = Load(loiter, Termite);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompStrikeConsole console = centerThing.TryGetComp<CompStrikeConsole>();
            t.Require(console != null && console.Active && Planner.consoles.Contains(console), "the command center has an active, registered strike console");
            t.Check(console.StrikeCapacity == 4 && console.StrikeFree == 4, $"two phased arrays give 4 strike channels (got {console.StrikeCapacity})");
            t.Check(console.CannotPlanReason() == null, "the console can plan");

            PlanetTile target = TileAtDistance(13.5f, 15f);
            t.Require(target.Valid, "a target tile ~14 tiles away exists");
            List<StrikeCandidate> candidates = StrikeRoundUtility.Candidates(Home, target, -1, out int elsewhere);
            int loiterRange = loiter.TryGetComp<CompMissileLauncher_Rail>().BuildFiringConfig(termite)?.Range ?? 0;
            t.Note($"loitering range {loiterRange}, target {MissileWorldUtility.RangeDistance(Home.Tile, target):0.0} tiles");
            t.Check(candidates.Count(x => x.Assignable && x.round.def == LightMissileItem) == 12 && elsewhere == 0, "12 light missiles are in range");
            t.Check(candidates.Any(x => x.round == termite && x.issue == StrikeRoundIssue.OutOfRange), "the loitering munition is listed as out of range");
            t.Check(!candidates.Any(x => x.round.def == InterceptorItem), "interceptors are not listed");
            t.Check(candidates.Where(x => x.Assignable).All(x => x.flightTicks > 2500 && x.groupLabel.Contains(LightBody.LabelCap)),
                "candidates carry flight time and a light-missile group label");

            var draft = new StrikeMission { targetTile = target, state = StrikeMissionState.Planning };
            draft.waves.Add(WaveOf(a));
            draft.waves.Add(WaveOf(b));
            draft.waves.Add(WaveOf(c));
            var over = new StrikeMission { targetTile = target };
            over.waves.Add(WaveOf(a));
            over.waves[0].slots.Add(new StrikeSlot { launcher = b, round = b.HeldThings[0] });
            t.Check(console.ValidateDraft(over) == "DMSE.Strike.WaveOverCap".Translate(1, 5, 4).ToString(), "a wave above the guidance limit is refused");
            t.Check(console.ValidateDraft(draft) == null, "three waves of four are valid");
            t.Require(console.Commit(draft, out string commitReason), $"the plan is confirmed ({commitReason ?? "ok"})");
            int id = draft.id;
            t.Check(console.mission == draft && draft.state == StrikeMissionState.AwaitingComputation && id >= 1, "the mission is computing");
            t.Check(Planner.ReservedBy(id).Count == 12, "all 12 missiles are reserved");
            Command single = FireGizmo(a.TryGetComp<CompMissileLauncher_Rail>());
            t.Check(single != null && single.Disabled, "the launcher's single-shot gizmo is disabled while its missiles are reserved");
            var another = new StrikeMission { targetTile = target };
            another.waves.Add(WaveOf(c, 1));
            t.Check(console.CannotFireReason(0, false) != null && !console.Commit(another, out _), "nothing fires while computing; a second plan is refused");

            var waitReady = new WaitUntil(() => draft.state == StrikeMissionState.Ready, 900);
            yield return waitReady;
            t.Require(!waitReady.TimedOut, "firing solutions finish");

            int fired = Find.TickManager.TicksGame;
            t.Check(console.FireWave(0, false) == 4, "wave 1 queues all four missiles");
            t.Check(console.StrikeFree == 0 && HomeBVR.Channels.Strike(Player) == 4, "queued missiles book all four channels at once");
            t.Check(console.CannotFireReason(1, false) == "DMSE.Strike.NotEnoughChannels".Translate(0, 4).ToString(),
                "wave 2 is refused for lack of guidance channels");
            var waitRipple = new WaitUntil(() => a.StoredCount == 0, 300);
            yield return waitRipple;
            t.Require(!waitRipple.TimedOut, "the four-cell launcher fires all four missiles");
            List<int> launches = draft.waves[0].slots.Select(s => s.launchTick).OrderBy(x => x).ToList();
            t.Check(launches[0] - fired <= 2 && launches.Zip(launches.Skip(1), (x, y) => y - x).All(d => d == 30),
                $"ripple launches 30 ticks apart ({string.Join(",", launches.Select(x => x - fired))})");
            CompMissileLauncher_Rail railA = a.TryGetComp<CompMissileLauncher_Rail>();
            t.Check(railA.CooldownUntil == launches.Last() + 450, "the launcher's cooldown starts after its last ripple round");
            t.Check(draft.waves[0].slots.All(s => s.state == StrikeSlotState.Launched && s.track != null
                && s.track.strikeMissionId == id && s.track.strikeWave == 0), "each world track is tagged with mission and wave");
            t.Check(HomeBVR.Channels.Strike(Player) == 4 && draft.state == StrikeMissionState.Executing, "launched missiles hold their channels in the guidance period");

            var waitFree = new WaitUntil(() => console.StrikeFree >= 4, 1200);
            yield return waitFree;
            int freed = Find.TickManager.TicksGame;
            t.Require(!waitFree.TimedOut, "guidance channels free up");
            t.Check(freed >= launches.Last() + BVRWorldTuningDef.Current.strikeGuidanceTicks,
                $"all channels free only after the guidance period ({freed - launches.Last()} ticks after the last launch)");
            t.Check(console.CannotFireReason(1, false) == null && console.FireWave(1, false) == 4, "wave 2 fires once the channels are free");
            var waitHalf = new WaitUntil(() => b.StoredCount <= 2, 200);
            yield return waitHalf;
            console.CancelMission();
            int left = b.StoredCount;
            t.Check(console.mission == null && Planner.ReservedBy(id).Count == 0 && Planner.QueuedCount(id) == 0,
                "cancelling clears the mission, its reservations and its queued launches");
            yield return new WaitTicks(90);
            t.Check(b.StoredCount == left && left > 0, $"queued launches stop on cancel ({left} left in launcher B)");
            t.Check(c.TryGetComp<CompMissileLauncher_Rail>().NextRound != null, "wave 3 missiles are released for single shots");
        }

        public static IEnumerable<Wait> StrikeSaveLoadAndCompletion(TestContext t)
        {
            Thing centerThing = SpawnBuilding(StrikeCenter, 20, 20);
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack a = SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompStrikeConsole console = centerThing.TryGetComp<CompStrikeConsole>();
            PlanetTile target = TileAtDistance(8f, 10f);
            t.Require(console != null && target.Valid, "console and target");
            var draft = new StrikeMission { targetTile = target, state = StrikeMissionState.Planning };
            draft.waves.Add(WaveOf(a, 2));
            var second = new StrikeWave();
            foreach (Thing round in a.TryGetComp<CompMissileLauncher_Rail>().StrikeRounds.Skip(2).ToList())
            {
                second.slots.Add(new StrikeSlot { launcher = a, round = round });
            }
            draft.waves.Add(second);
            t.Require(console.Commit(draft, out string reason), $"plan confirmed ({reason ?? "ok"})");
            int id = draft.id;
            yield return new WaitUntil(() => draft.state == StrikeMissionState.Ready, 900);
            t.Require(draft.state == StrikeMissionState.Ready, "ready");

            // Save between the two ripple launches of wave 1: one hold and one queued launch must survive.
            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            t.Check(console.FireWave(0, false) == 2, "wave 1 queues two missiles");
            var waitFirst = new WaitUntil(() => a.StoredCount == 3, 60);
            yield return waitFirst;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            t.Require(!waitFirst.TimedOut && Planner.QueuedCount(id) == 1, "first round launched, second still queued");
            t.Check(HomeBVR.Channels.Strike(Player) == 2, "one guidance hold and one queued launch book both channels");
            int firstLaunch = draft.waves[0].slots.Max(s => s.launchTick);
            RememberEnemyForReload();
            Game before = Current.Game;
            GameDataSaveLoader.SaveGame(TestSaveName);
            yield return new WaitFrames(10);
            GameDataSaveLoader.LoadGame(TestSaveName);
            yield return new WaitUntil(() => Current.Game != before && Current.ProgramState == ProgramState.Playing, 999999);
            yield return new WaitFrames(5);
            t.allowPause = false;

            Thing center = Home.listerThings.ThingsOfDef(StrikeCenter).FirstOrDefault();
            Building_MissileRack rack = Home.listerThings.AllThings.OfType<Building_MissileRack>().FirstOrDefault(r => r.def == LightLauncher);
            foreach (Thing th in Home.listerThings.AllThings.Where(x => x.def == StrikeCenter || x.def == PhaseArray || x.def == LightLauncher).ToList()) { Track(th); }
            console = center?.TryGetComp<CompStrikeConsole>();
            t.Require(console?.mission != null && rack != null, "console, mission and launcher restored");
            StrikeMission m = console.mission;
            t.Check(m.id == id && m.state == StrikeMissionState.Executing && m.waves.Count == 2, "mission id, state and waves restored");
            // The game may run a few dozen ticks while loading settles, so the queued launch can already have fired.
            var waitSecond = new WaitUntil(() => rack.StoredCount == 2, 120);
            yield return waitSecond;
            List<int> wave1 = m.waves[0].slots.Select(s => s.launchTick).OrderBy(x => x).ToList();
            t.Check(!waitSecond.TimedOut && m.waves[0].slots.All(s => s.state == StrikeSlotState.Launched) && wave1[1] == firstLaunch + 30,
                $"the queued launch restored from the save fires on its scheduled tick (+{wave1[1] - firstLaunch})");
            t.Check(HomeBVR.Channels.Strike(Player) == 2 && console.StrikeFree == 0, "the saved guidance hold and the new one book both channels");
            t.Check(Planner.ReservedBy(id).Count == 2 && rack.TryGetComp<CompMissileLauncher_Rail>().NextRound == null,
                "reservations rebuilt for wave 2's missiles");

            var waitFree = new WaitUntil(() => console.StrikeFree >= 2, 1500);
            yield return waitFree;
            t.Check(!waitFree.TimedOut && Find.TickManager.TicksGame >= firstLaunch + BVRWorldTuningDef.Current.strikeGuidanceTicks,
                "the saved guidance hold expires on schedule");
            var waitCooldown = new WaitUntil(() => console.CannotFireReason(1, false) == null, 900);
            yield return waitCooldown;
            t.Require(!waitCooldown.TimedOut, "wave 2 becomes fireable once channels and launcher are ready");
            t.Check(console.FireWave(1, false) == 2, "wave 2 queues its two missiles");
            var waitDone = new WaitUntil(() => console.mission == null, 200);
            yield return waitDone;
            t.Check(!waitDone.TimedOut && rack.StoredCount == 0, "the mission completes once every missile is launched");
            t.Check(Planner.ReservedBy(id).Count == 0 && console.CannotPlanReason() != null, "reservations are released; with no missiles left the console cannot plan");
        }

        /// <summary>
        /// Draws the strike window in both modes (the runner closes force-paused windows every frame, so the
        /// planning window is opened with those flags cleared). Auto-assign runs through the window itself.
        /// Drawing errors land in Player.log.
        /// </summary>
        public static IEnumerable<Wait> StrikeDialogRenders(TestContext t)
        {
            Thing centerThing = SpawnBuilding(StrikeCenter, 20, 20);
            SpawnBuilding(PhaseArray, 2, 2);
            SpawnLoadedLauncher(10, 2, 4);
            SpawnLoadedLauncher(14, 2, 4);
            Building_MissileRack loiter = (Building_MissileRack)SpawnBuilding(LoiterLauncher, 26, 2);
            Load(loiter, Termite);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompStrikeConsole console = centerThing.TryGetComp<CompStrikeConsole>();
            PlanetTile target = TileAtDistance(13.5f, 15f);
            t.Require(console != null && target.Valid, "console and target");

            var draft = new StrikeMission { targetTile = target, state = StrikeMissionState.Planning };
            draft.waves.Add(new StrikeWave());
            var plan = new Dialog_StrikePlan(console, draft) { forcePause = false, absorbInputAroundWindow = false };
            Find.WindowStack.Add(plan);
            typeof(Dialog_StrikePlan).GetMethod("AutoAssign", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(plan, new object[] { console.StrikeCapacity });
            yield return new WaitFrames(10);
            t.Check(Find.WindowStack.IsOpen(plan), "the planning window stays open while drawing");
            t.Check(draft.waves.Count == 4 && draft.waves.All(w => w.slots.Count == 2), "auto-assign makes four waves of two (two channels)");
            t.Screenshot("strike_plan");
            yield return new WaitFrames(3);
            plan.Close(doCloseSound: false);
            t.Require(console.Commit(draft, out string reason), $"plan confirmed ({reason ?? "ok"})");

            var exec = new Dialog_StrikePlan(console, console.mission);
            Find.WindowStack.Add(exec);
            yield return new WaitUntil(() => draft.state == StrikeMissionState.Ready, 900);
            console.TryFireFromUI(0, partial: false);
            yield return new WaitTicks(60);
            t.Check(Find.WindowStack.IsOpen(exec) && draft.waves[0].slots.All(s => s.state == StrikeSlotState.Launched),
                "the execution window stays open while wave 1 launches");
            t.Screenshot("strike_exec");
            yield return new WaitFrames(3);
            console.CancelMission();
            yield return new WaitFrames(3);
            t.Check(!Find.WindowStack.IsOpen(exec), "the execution window closes itself when the mission ends");
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
