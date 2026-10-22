using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// Real production classes and real 1.7 reference assemblies. The minimal fixture avoids booting a game/render loop.
static class Test
{
    static int count;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        count++;
    }
    static void Near(float actual, float expected, string name, float tolerance = 0.001f)
        => Check(Math.Abs(actual - expected) <= tolerance, name + $" ({actual} != {expected})");
    static void Field(object obj, string name, object value)
    {
        for (Type type = obj.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field != null) { field.SetValue(obj, value); return; }
        }
        throw new Exception("Fixture field missing: " + name);
    }
    static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static TickManager clock;
    static World world;
    static List<WorldObject> objects;
    static void Tick(int now) => Field(clock, "ticksGameInt", now);
    static Faction Faction(bool player) => new Faction { def = new FactionDef { isPlayer = player } };
    static ThingWithComps Parent(Faction faction)
    {
        var parent = new ThingWithComps { def = Empty<ThingDef>() };
        Field(parent, "factionInt", faction);
        return parent;
    }
    static void Setup()
    {
        var game = Empty<Game>();
        Current.Game = game;
        clock = Empty<TickManager>();
        game.tickManager = clock;
        world = Empty<World>();
        Field(game, "worldInt", world);
        Field(game, "maps", new List<Map>());
        world.worldObjects = new WorldObjectsHolder();
        objects = new List<WorldObject>();
        Field(world.worldObjects, "worldObjects", objects);
        world.components = new List<WorldComponent>();
        world.factionManager = new FactionManager();
        world.grid = new WorldGrid();
        var surface = Empty<SurfaceLayer>();
        Field(surface, "averageTileSize", 0.5f);
        Field(surface, "radius", 100f);
        Field(world.grid, "surface", surface);
        var network = new WorldComponent_BVRNetwork(world);
        world.components.Add(network);
        Tick(1000);
    }
    static void Detection()
    {
        Near(MissileWorldMotion.DetectionRange(8, 0, new BVRTargetProps()), 8, "early/loiter");
        Near(MissileWorldMotion.DetectionRange(8, 0, new BVRTargetProps { distanceLevel = 1.2f }), 9.6f, "early/light");
        Near(MissileWorldMotion.DetectionRange(8, 0, new BVRTargetProps { distanceLevel = 1.5f, stealthLevel = 1 }), 4.8f, "early/cruise stealth");
        Near(MissileWorldMotion.DetectionRange(8, 0, new BVRTargetProps { distanceLevel = 2, stealthLevel = 2 }), 6.4f, "early/ballistic stealth");
        Near(MissileWorldMotion.DetectionRange(12, 2, new BVRTargetProps()), 18, "phased/loiter");
        Near(MissileWorldMotion.DetectionRange(12, 2, new BVRTargetProps { distanceLevel = 1.2f }), 21.6f, "phased/light");
        Near(MissileWorldMotion.DetectionRange(12, 2, new BVRTargetProps { distanceLevel = 1.5f, stealthLevel = 1 }), 22.5f, "phased/cruise");
        Near(MissileWorldMotion.DetectionRange(12, 2, new BVRTargetProps { distanceLevel = 2, stealthLevel = 2 }), 24, "phased/ballistic");
        Near(MissileWorldMotion.DetectionRange(12, 100, new BVRTargetProps()), 18, "anti-stealth bonus capped");
        Near(MissileWorldMotion.DetectionRange(0, 2, new BVRTargetProps()), 0, "zero range disabled");
        Near(MissileWorldMotion.BaseAngularSpeed, 1f / 30000f, "world surface scale");
        Near(MissileWorldMotion.AngularSpeed(new MissileConfig(new MissileBodyDef { baseWorldSpeedFactor = 0.75f })),
            0.75f / 30000f, "missile body speed");
    }
    static void Solver()
    {
        foreach (float angle in new[] { 0.01f, 0.1f, 0.5f, 1f, 2.5f })
        foreach (float speed in new[] { 0.55f, 0.75f, 1f, 1.5f })
        {
            var track = new FakeTrack { angle = angle, speed = speed / 30000f };
            Vector3 node = track.PositionAt(track.TicksToArrival);
            float interceptorSpeed = 2.25f / 30000f;
            bool solved = BVRInterceptSolver.TrySolve(track, node, interceptorSpeed, out int ticks, out Vector3 point);
            Check(solved, "long-enough head-on solution");
            Near(Vector3.Distance(point, track.PositionAt(ticks)), 0, "meeting point follows target");
            float flown = GenMath.SphericalDistance(node.normalized, point.normalized);
            Check(Math.Abs(flown - interceptorSpeed * ticks) < 0.001f, "interceptor reaches point within rounding tolerance");
            Check(ticks < track.TicksToArrival - 60, "meeting leaves terminal margin");
        }
        var late = new FakeTrack { angle = 0.001f, speed = 1f / 30000f };
        Check(!BVRInterceptSolver.TrySolve(late, late.PositionAt(late.TicksToArrival), 2.25f / 30000f, out _, out _),
            "reject too-late launch");
    }
    static void FireControl()
    {
        var settings = new CompProperties_FireControlRadar { powerLevel = 2, maxHitChance = 0.9f, lockOnTicks = 120 };
        var cruise = new BVRTarget { speed = 30, stealthLevel = 1 };
        Near(CompFireControlRadar.ComputeHitChance(settings, cruise, 3000), .738f, "cruise fire control");
        Check(CompFireControlRadar.LockOnTicksFor(settings, cruise) == 150, "cruise lock time");
        var ballistic = new BVRTarget { speed = 80, stealthLevel = 2 };
        Near(CompFireControlRadar.ComputeHitChance(settings, ballistic, 2400), .198f, "ballistic fire control");
        Check(CompFireControlRadar.ComputeHitChance(settings, cruise, 50) < .1f, "short guidance degrades accuracy");
        var stealth = new BVRTarget { speed = 15, stealthLevel = 4 };
        Near(CompFireControlRadar.ComputeHitChance(settings, stealth, 3000), .225f, "stealth gap compounds");
        Check(CompFireControlRadar.LockOnTicksFor(settings, stealth) == 240, "stealth gap extends lock");
    }
    static void LedgerAndTerminal()
    {
        var defender = Faction(false);
        var other = Faction(false);
        var map = new Map();
        var manager = new MapComponent_BVRCombat(map);
        var radar = new OnlineRadar { props = new CompProperties_FireControlRadar { maxTargets = 3, supportsArtilleryGuidance = true }, parent = Parent(defender) };
        var otherRadar = new OnlineRadar { props = new CompProperties_FireControlRadar { maxTargets = 2 }, parent = Parent(other) };
        manager.fireControlRadars.Add(radar);
        manager.fireControlRadars.Add(otherRadar);
        Check(manager.Channels.Capacity(defender) == 3 && manager.Channels.Capacity(other) == 2, "faction capacity isolation");
        radar.OccupyArtilleryChannel();
        manager.Waves.Add(new BVRWave { defenderFaction = defender, targets = new List<BVRTarget> { new BVRTarget { lockUntil = 1200 } } });
        manager.Waves.Add(new BVRWave { defenderFaction = other, targets = new List<BVRTarget> { new BVRTarget { midcourseEngagedUntil = 1200 } } });
        Check(manager.CountEngaged(defender) == 1 && manager.CountEngaged(other) == 1, "faction occupancy isolation");
        Check(manager.Channels.Free(defender) == 1, "artillery and local target share capacity");
        // A world lock belongs to this map's parent; this minimal map has no parent (null).
        var track = new WorldObject_IncomingMissile();
        track.State.lockNode = map.Parent;
        track.State.lockFaction = defender;
        track.State.lockUntil = 1100;
        objects.Add(track);
        Check(manager.Channels.Free(defender) == 0, "world lock consumes last shared channel");
        track.State.ClearLock();
        var interceptor = new WorldObject_Interceptor { launchNode = map.Parent, def = new WorldObjectDef { canHaveFaction = true } };
        interceptor.SetFaction(defender);
        objects.Add(interceptor);
        Check(manager.Channels.Free(defender) == 0, "in-flight world interceptor retains channel");
        objects.Remove(interceptor);
        Check(manager.Channels.Free(defender) == 1, "destroyed interceptor releases channel");
        Tick(1201);
        Check(manager.CountEngaged(other) == 0, "expired local flight releases channel");
        // Actual terminal-registration path, with no search radar present.
        manager.Waves.Clear();
        var ciws = new OnlineCIWS { parent = Parent(defender), props = new CompProperties_TerminalCIWS { terminalWindowTicks = 1800 } };
        manager.ciwsTurrets.Add(ciws);
        Check(manager.TerminalWindowTicks(defender) == 1800, "CIWS terminal window needs no search radar");
        var terminalWave = new BVRWave { defenderFaction = defender,
            targets = new List<BVRTarget> { new BVRTarget { terminalOnly = true } } };
        manager.Waves.Add(terminalWave);
        Check(terminalWave.TerminalOnly && manager.CountEngaged(defender) == 0, "terminal-only wave reserves no SAM channel");
        ciws.online = false;
        Check(manager.TerminalWindowTicks(defender) == 0 && terminalWave.TerminalOnly,
            "loss of CIWS does not turn a terminal-only wave back into a SAM wave");
        terminalWave.targets.Add(new BVRTarget());
        Check(!terminalWave.TerminalOnly, "legacy waves remain eligible for full defense");
        objects.Clear();
    }
    static void InventoryAndPersistence()
    {
        Tick(2000);
        var parent = new MapParent { def = new WorldObjectDef { canHaveFaction = true } };
        parent.SetFaction(Faction(false));
        var defense = new WorldObjectComp_AirDefense { parent = parent,
            props = new CompProperties_WorldAirDefense { interceptorStock = 8, restockPerDay = 4, launchCooldownTicks = 240 } };
        Check(defense.Stock == 8 && defense.Available, "unloaded NPC starts with configured stock");
        Check(defense.TryConsume(2000) && defense.Stock == 7, "NPC launch consumes one missile");
        Check(!defense.TryConsume(2100) && defense.Stock == 7, "cooldown prevents a second consumption");
        Tick(32000);
        Check(defense.Stock == 8, "fractional replenishment caps at stock capacity");
        Check(defense.TryConsume(32000) && defense.Stock == 7, "restocked node can launch again");
        var map = new Map();
        map.info.parent = parent;
        Current.Game.Maps.Add(map);
        Tick(62000);
        Check(!defense.Available && defense.Stock == 7, "loaded map disables abstract stock/replenishment");
        Check(!defense.TryConsume(62000), "loaded map cannot consume abstract missiles");
        Current.Game.Maps.Remove(map);
        Tick(77000);
        Check(defense.Available && defense.Stock == 8, "abstract defense resumes after map removal");

        var original = new BVRTrackState { firstDetectedTick = 1500, interceptAttempts = 3, playerNotified = true, lockUntil = 2100 };
        string path = Path.Combine(Path.GetTempPath(), "dmse-bvr-state-" + Guid.NewGuid() + ".xml");
        Scribe.saver.InitSaving(path, "BVRTest");
        original.ExposeData();
        Scribe.saver.FinalizeSaving();
        var saved = XDocument.Load(path).Root;
        Check(saved.Element("firstDetectedTick")?.Value == "1500" && saved.Element("interceptAttempts")?.Value == "3"
            && saved.Element("playerNotified")?.Value == "True" && saved.Element("lockUntil")?.Value == "2100",
            "real Scribe saving retains detection/attempt/lock values");
        File.Delete(path);
        var loaded = new BVRTrackState();
        Check(loaded.lockUntil == -1 && loaded.firstDetectedTick == -1 && !loaded.playerNotified
            && loaded.interceptor == null, "legacy missing-state defaults are safe");
        loaded.playerNotified = true;
        loaded.interceptAttempts = 3;
        loaded.ClearLock();
        Check(loaded.lockUntil == -1 && loaded.lockNode == null && loaded.lockFaction == null
            && !loaded.lockNodeTile.Valid, "releasing lock clears every channel owner field");
        Check(loaded.playerNotified && loaded.interceptAttempts == 3, "releasing a lock preserves notification and attempt history");
    }
    static int nextThingId = 100;
    // Unique ids as in a real game: Thing.Equals compares id and def.
    static ThingWithComps Round(ThingDef def) => new ThingWithComps { def = def, thingIDNumber = nextThingId++ };
    static ThingDef RoleDef(MissileRole? role)
    {
        var def = Empty<ThingDef>();
        Field(def, "stackLimit", 1);
        if (role.HasValue) def.modExtensions = new List<DefModExtension> { new MissileRoleExtension { role = role.Value } };
        return def;
    }
    // Wave-strike P0 (docs/BVR_波次打擊規劃.md §5-2, §8-4, §11).
    static void StrikeP0()
    {
        ThingDef plain = RoleDef(null), interceptorDef = RoleDef(MissileRole.Interceptor), dualDef = RoleDef(MissileRole.Dual);
        Check(MissileRoleExtension.RoleOf(plain) == MissileRole.Strike, "unmarked missile defaults to strike");
        Check(!MissileRoleExtension.CanStrike(Round(interceptorDef)), "interceptor cannot strike");
        Check(MissileRoleExtension.CanStrike(Round(dualDef)), "dual-role missile can strike");
        Check(!MissileRoleExtension.CanStrike(null), "no missile, no strike");

        var planner = new MapComponent_StrikePlanner(new Map());
        var a = Round(plain);
        var b = Round(plain);
        Check(planner.OwnerOf(a) == -1 && !planner.IsReserved(a), "rounds start unreserved");
        Check(planner.TryReserve(a, 1) && planner.OwnerOf(a) == 1, "reserve for mission 1");
        Check(planner.TryReserve(a, 1), "re-reserving for the same owner succeeds");
        Check(!planner.TryReserve(a, 2) && planner.OwnerOf(a) == 1, "another mission cannot take a reserved round");
        Check(planner.TryReserve(b, 2) && planner.ReservedBy(2).Single() == b, "second mission reserves its own round");
        planner.ReleaseAll(1);
        Check(!planner.IsReserved(a) && planner.OwnerOf(b) == 2, "releasing one mission keeps the other's rounds");
        Field(b, "mapIndexOrState", (sbyte)-2);
        Check(b.Destroyed && !planner.IsReserved(b) && planner.ReservedBy(2).Count == 0, "a destroyed round drops its reservation");
        Check(!planner.TryReserve(null, 1) && !planner.TryReserve(a, -1), "invalid reservations are rejected");

        // Rail magazine shared with a SAM: interceptors are not strike rounds and never get the platform body.
        var body = new MissileBodyDef { defName = "TestLightBody" };
        var rack = new Building_MissileRack { def = plain, thingIDNumber = nextThingId++ };
        var rail = new CompMissileLauncher_Rail { parent = rack, props = new CompProperties_MissileRailLauncher { missileBody = body } };
        var interceptor = Round(interceptorDef);
        var strike = Round(dualDef);
        Check(!rail.HasAmmo && rail.NextRound == null, "empty rail has no round");
        rack.GetDirectlyHeldThings().TryAdd(interceptor);
        Check(!rail.HasAmmo && rail.BuildFiringConfig(interceptor) == null, "an interceptor alone is not rail ammo or a strike config");
        rack.GetDirectlyHeldThings().TryAdd(strike);
        Check(rail.StrikeRounds.SequenceEqual(new Thing[] { strike }), "only the strike-capable missile is a strike round");
        Check(rail.HasAmmo && rail.NextRound == strike, "single-shot uses the first strike round, skipping the interceptor");
        Check(rail.BuildFiringConfig(strike)?.body == body, "an unconfigured strike round falls back to the platform body");
        Check(rail.BuildFiringConfig(a) == null, "a round outside the magazine has no config");
        Check(!rail.OwnsRound(a), "a round outside the magazine is not owned");
        Check(rail.OwnsRound(strike), "a loaded strike round is owned");
        Check(rail.SalvoIntervalTicks == 30, "default ripple interval");

        // Ballistic silo: the platform itself is the round once assembled.
        var silo = Round(plain);
        var ballistic = new CompMissileLauncher_Ballistic { parent = silo, props = new CompProperties_BallisticLauncher() };
        Check(!ballistic.StrikeRounds.Any() && ballistic.NextRound == null, "unassembled silo has no round");
        ballistic.MarkLoaded();
        Check(ballistic.StrikeRounds.Single() == silo && ballistic.NextRound == silo, "assembled silo is its own round");
        Check(ballistic.BuildFiringConfig(a) == null, "a silo only fires itself");

        // World defense engagement order: soonest arrival first, ties keep creation order.
        var far = new FakeTrack { angle = 0.3f, speed = 1f / 30000f };
        var nearA = new FakeTrack { angle = 0.1f, speed = 1f / 30000f };
        var fast = new FakeTrack { angle = 0.3f, speed = 1.5f / 30000f };
        var nearB = new FakeTrack { angle = 0.1f, speed = 1f / 30000f };
        var order = WorldComponent_BVRNetwork.InEngagementOrder(new[] { far, nearA, fast, nearB });
        Check(order.SequenceEqual(new[] { nearA, nearB, fast, far }), "engagement order is by arrival time, stable for ties");

        // §15-4 finding 2: locking needs interceptor ammo (an Active SAM, reloading included, or abstract stock).
        var defender = Faction(false);
        var manager = new MapComponent_BVRCombat(new Map());
        var sam = new ArmedLauncher { parent = Parent(defender), props = new CompProperties_MissileLauncher { interceptorSkyfaller = Empty<ThingDef>() } };
        manager.launchers.Add(sam);
        Check(!manager.HasInterceptorAmmo(defender), "an inactive or empty SAM gives no interceptor ammo");
        sam.armed = true;
        Check(manager.HasInterceptorAmmo(defender) && !manager.HasInterceptorAmmo(Faction(false)), "an active SAM arms only its own faction");
        var site = new MapParent { def = new WorldObjectDef { canHaveFaction = true } };
        site.SetFaction(defender);
        var stock = new WorldObjectComp_AirDefense { parent = site,
            props = new CompProperties_WorldAirDefense { interceptorStock = 1, restockPerDay = 0, launchCooldownTicks = 0 } };
        var node = new BVRNode { parent = site, faction = defender, defense = new WorldAirDefenseAdapter(stock) };
        Check(node.HasInterceptorAmmo, "an abstract node with stock is armed");
        Check(stock.TryConsume(Find.TickManager.TicksGame) && !node.HasInterceptorAmmo, "an abstract node with no stock left is not armed");
        Check(new BVRNode { parent = site, faction = defender, local = manager }.HasInterceptorAmmo, "a map node reads its SAMs");
    }
    // Wave-strike P1: shared channel booking, config fingerprint, auto-assign, mission helpers and save output.
    static void StrikeP1()
    {
        Tick(100000);
        var side = Faction(false);
        var map = new Map();
        var planner = new MapComponent_StrikePlanner(map);
        map.components.Add(planner);
        var manager = new MapComponent_BVRCombat(map);
        manager.fireControlRadars.Add(new OnlineRadar { props = new CompProperties_FireControlRadar { maxTargets = 3 }, parent = Parent(side) });
        manager.fireControlRadars.Add(new OnlineRadar { props = new CompProperties_FireControlRadar { maxTargets = 2, supportsStrikeGuidance = false }, parent = Parent(side) });
        Check(manager.Channels.Capacity(side) == 5 && manager.Channels.StrikeCapacity(side) == 3, "strike capacity counts only strike-capable radars");
        Check(manager.Channels.StrikeFree(side) == 3, "all strike channels free at start");
        planner.AddGuidanceHold(side, 100600);
        planner.Enqueue(new StrikeQueuedLaunch { console = Parent(side), missionId = 1, fireTick = 100030 });
        Check(manager.Channels.Strike(side) == 2 && manager.Channels.Free(side) == 3 && manager.Channels.StrikeFree(side) == 1,
            "queued and guided strike missiles book shared channels");
        Check(planner.TicksUntilNextRelease(side) == 600 && planner.QueuedCount(1) == 1, "next guidance release and queue count");
        planner.DropQueued(1);
        Check(manager.Channels.Strike(side) == 1 && planner.QueuedCount(1) == 0, "dropping a mission's queue frees its booked channels");
        Check(manager.Channels.Strike(Faction(false)) == 0, "another faction's strike use is separate");
        Tick(100600);
        Check(manager.Channels.Strike(side) == 0 && planner.TicksUntilNextRelease(side) == -1, "the guidance period ends on time");

        var body = new MissileBodyDef { defName = "HashBody" };
        var warhead = new MissilePartDef { defName = "HashWarhead", category = MissilePartCategory.Warhead };
        var seeker = new MissilePartDef { defName = "HashSeeker", category = MissilePartCategory.Guidance };
        var c1 = new MissileConfig(body); c1.parts.Add(warhead); c1.parts.Add(seeker);
        var c2 = new MissileConfig(body); c2.parts.Add(seeker); c2.parts.Add(warhead);
        Check(StrikeRoundUtility.ConfigHash(c1) == StrikeRoundUtility.ConfigHash(c2), "config fingerprint ignores part order");
        Check(StrikeRoundUtility.ConfigHash(c1) != StrikeRoundUtility.ConfigHash(new MissileConfig(body)), "config fingerprint sees a refit");

        var plan = StrikeRoundUtility.AutoAssign(10, 4, 5);
        Check(plan.Select(w => w.Count).SequenceEqual(new[] { 4, 4, 2 }) && plan[2].SequenceEqual(new[] { 8, 9 }),
            "auto-assign fills from the cheapest; the most valuable land in the last wave");
        Check(StrikeRoundUtility.AutoAssign(10, 4, 2).Sum(w => w.Count) == 8, "rounds beyond the wave limit are left out");
        Check(StrikeRoundUtility.AutoAssign(3, 0, 5).Count == 0 && StrikeRoundUtility.AutoAssign(0, 4, 5).Count == 0, "degenerate auto-assign inputs");

        var mission = new StrikeMission { id = 7, name = "Operation Test", state = StrikeMissionState.Executing };
        var first = new StrikeWave { launchedTick = 100 };
        first.slots.Add(new StrikeSlot { state = StrikeSlotState.Launched, solved = true });
        var second = new StrikeWave();
        second.slots.Add(new StrikeSlot { state = StrikeSlotState.Pending, solved = true, configHash = 42 });
        second.slots.Add(new StrikeSlot { state = StrikeSlotState.Skipped, skipReason = "gone" });
        mission.waves.Add(first);
        mission.waves.Add(second);
        Check(mission.NextWaveIndex == 1 && mission.RoundCount == 3 && mission.AllSlots.Count(x => x.Open) == 1,
            "next wave, round count and open slots");
        string path = Path.Combine(Path.GetTempPath(), "dmse-strike-" + Guid.NewGuid() + ".xml");
        Scribe.saver.InitSaving(path, "Strike");
        mission.ExposeData();
        Scribe.saver.FinalizeSaving();
        var saved = XDocument.Load(path).Root;
        File.Delete(path);
        var savedSlots = saved.Descendants("slots").SelectMany(x => x.Elements("li")).ToList();
        Check(saved.Element("id")?.Value == "7" && saved.Element("state")?.Value == "Executing"
            && saved.Element("waves")?.Elements("li").Count() == 2 && savedSlots.Count == 3
            && savedSlots[1].Element("configHash")?.Value == "42" && savedSlots[2].Element("skipReason")?.Value == "gone"
            && saved.Element("name")?.Value == "Operation Test",
            "real Scribe saving keeps mission name, waves and slot states");
        var fresh = new StrikeMission();
        Check(fresh.state == StrikeMissionState.Planning && fresh.computeDoneTick == -1 && fresh.NextWaveIndex == -1
            && new StrikeSlot().state == StrikeSlotState.Pending && new StrikeWave().launchedTick == -1, "safe defaults");
    }
    // Wave-strike P2/P3: manual computation work, missile-by-missile solving, incremental additions.
    static void StrikeP2()
    {
        Near(StrikeComputation.TotalWork(1500, 400, 1f, 4), 3100, "manual work: base + per missile");
        Near(StrikeComputation.TotalWork(1500, 400, 1.5f, 4), 4650, "cross-layer work factor");
        float baseLeft = 1500f, credit = 0f;
        Near(StrikeComputation.Remaining(baseLeft, credit, 400, 4), 3100, "remaining work at the start");
        Check(StrikeComputation.Advance(ref baseLeft, ref credit, 1499, 400, 4) == 0 && Math.Abs(baseLeft - 1) < 0.01f,
            "work fills the base first");
        Check(StrikeComputation.Advance(ref baseLeft, ref credit, 1, 400, 4) == 0 && baseLeft == 0 && credit == 0,
            "finishing the base solves nothing yet");
        Check(StrikeComputation.Advance(ref baseLeft, ref credit, 400, 400, 4) == 1, "each missile's share solves one");
        Check(StrikeComputation.Advance(ref baseLeft, ref credit, 600, 400, 3) == 1 && Math.Abs(credit - 200) < 0.01f,
            "leftover work carries over as credit");
        Near(StrikeComputation.Remaining(baseLeft, credit, 400, 3), 1000, "remaining work after solving two of four plus credit");
        Near(StrikeComputation.Remaining(baseLeft, credit, 400, 5), 1800, "adding missiles only adds their share (incremental)");
        Check(StrikeComputation.Advance(ref baseLeft, ref credit, 9999, 400, 2) == 2, "solving stops at the unsolved count");
        float b2 = 1500f, c2 = 0f;
        Check(StrikeComputation.Advance(ref b2, ref c2, 1500, 0, 3) == 3, "with no per-missile work everything is solved after the base");
    }
    static StrikePlanSlot PlanSlot(object launcher, int flight, int salvo = 30, int cooldown = 450, int ready = 0)
        => new StrikePlanSlot { launcher = launcher, flightTicks = flight, salvoInterval = salvo, launchCooldown = cooldown, readyTick = ready };
    // Wave-strike P3: arrival timeline and order warnings (docs §2-3 plan A/B).
    static void StrikeP3()
    {
        List<StrikePlanSlot> Wave(int count, int flight) => Enumerable.Range(0, count).Select(_ => PlanSlot(new object(), flight)).ToList();
        var planA = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>> { Wave(4, 3000), Wave(4, 3000), Wave(2, 2250) },
            4, 600, 60, 0, new int[0]);
        Check(planA[0].fireTick == 0 && planA[0].arriveMin == 3001 && planA[0].arriveMax == 3001, "wave 1 fires at once and arrives together");
        Check(planA[1].fireTick == 601 && planA[1].arriveMin == 3602, "wave 2 waits for the guidance period to free four channels");
        Check(planA[2].fireTick == 1202 && planA[2].arriveMin == 3453, "faster wave 3 fires last but arrives at 3453");
        Check(planA[2].earlierThan == 1 && planA[2].suggestedDelay == 209 && planA[1].earlierThan == -1,
            "plan A: wave 3 is flagged as arriving before wave 2, with the delay that fixes it");
        var planB = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>> { Wave(4, 3000), Wave(4, 3000), Wave(2, 2500) },
            4, 600, 60, 0, new int[0]);
        Check(planB.All(p => p.earlierThan == -1), "plan B: slower last wave keeps the order");

        object rail = new object();
        var ripple = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>>
        {
            Enumerable.Range(0, 4).Select(_ => PlanSlot(rail, 1000)).ToList(),
            new List<StrikePlanSlot> { PlanSlot(rail, 1000) },
        }, 8, 600, 60, 0, new int[0]);
        Check(ripple[0].arriveMin == 1001 && ripple[0].arriveMax == 1091, "one launcher ripples four rounds 30 ticks apart");
        Check(ripple[1].fireTick == 541, "the same launcher fires again only after its cooldown from the last round");

        var over = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>> { Wave(5, 1000) }, 4, 600, 60, 0, new int[0]);
        Check(over[0].overCapacity && over[0].fireTick == -1, "a wave above the channel capacity never fires");
        var busy = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>> { Wave(2, 1000) }, 2, 600, 60, 0, new[] { 500, 300 });
        Check(busy[0].fireTick == 500, "channels held by earlier launches delay the wave until both free up");
        var flying = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>>
        {
            new List<StrikePlanSlot> { new StrikePlanSlot { launched = true, arrivalTick = 2000 }, new StrikePlanSlot { launched = true, arrivalTick = -1 } },
            Wave(1, 1500),
        }, 4, 600, 60, 100, new int[0]);
        Check(flying[0].arriveMin == 2000 && flying[0].fireTick == -1 && flying[1].fireTick == 100 && flying[1].earlierThan == 0
            && flying[1].suggestedDelay == 2000 + 60 - 1601, "in-flight missiles count by their ETA; finished ones are ignored");
    }
    // Wave-strike P4: decoy signature and threat-assessment engagement order.
    static void StrikeP4()
    {
        var body = new MissileBodyDef { defName = "DecoyBody" };
        var decoy = new MissilePartDef { defName = "Decoy", category = MissilePartCategory.Payload, signatureFactor = 2f };
        var flare = new MissilePartDef { defName = "Flare", category = MissilePartCategory.Guidance, signatureFactor = 1.5f };
        var plain = new MissileConfig(body);
        var both = new MissileConfig(body); both.parts.Add(decoy); both.parts.Add(flare);
        Near(plain.SignatureFactor, 1f, "no decoy part: signature x1");
        Near(both.SignatureFactor, 3f, "signature factors multiply");

        var incoming = Empty<ThingDef>();
        var baseProps = new BVRTargetProps { distanceLevel = 1.2f, stealthLevel = 1, speed = 22 };
        incoming.modExtensions = new List<DefModExtension> { baseProps };
        var normal = new WorldObject_IncomingMissile { incomingSkyfaller = incoming, config = plain };
        var withDecoy = new WorldObject_IncomingMissile { incomingSkyfaller = incoming, config = new MissileConfig(body) };
        withDecoy.config.parts.Add(decoy);
        Check(ReferenceEquals(normal.TargetProps, baseProps), "a plain missile uses its skyfaller's BVR properties");
        Check(Math.Abs(withDecoy.TargetProps.distanceLevel - 2.4f) < 0.001f && withDecoy.TargetProps.stealthLevel == 1
            && Math.Abs(withDecoy.TargetProps.speed - 22f) < 0.001f, "a decoy payload doubles the radar size only");
        Near(MissileWorldMotion.DetectionRange(12, 2, withDecoy.TargetProps), 2f * MissileWorldMotion.DetectionRange(12, 2, normal.TargetProps),
            "a decoy is detected twice as far out");

        var soon = new FakeTrack { angle = 0.1f, speed = 1f / 30000f };
        var bigLate = new FakeTrack { angle = 0.3f, speed = 1f / 30000f };
        var smallLate = new FakeTrack { angle = 0.2f, speed = 1f / 30000f };
        var size = new Dictionary<FakeTrack, float> { { soon, 1.2f }, { bigLate, 2.4f }, { smallLate, 1.2f } };
        var tracks = new[] { soon, bigLate, smallLate };
        Check(WorldComponent_BVRNetwork.InEngagementOrder(tracks, t => size[t]).SequenceEqual(new[] { bigLate, soon, smallLate }),
            "threat assessment engages the largest signature first, then the soonest arrival");
        Check(WorldComponent_BVRNetwork.InEngagementOrder(tracks, t => 0f).SequenceEqual(new[] { soon, smallLate, bigLate }),
            "without threat assessment the order is soonest arrival first");
    }
    // Missile content: default parts, jam/chaff aggregation, anti-ship terminal updates, altitude factors.
    static void MissileContent()
    {
        var body = new MissileBodyDef { defName = "ContentBody", terminalUpdates = 3 };
        body.slots.Add(MissilePartCategory.Warhead);
        body.slots.Add(MissilePartCategory.Guidance);
        body.slots.Add(MissilePartCategory.Payload);
        var warhead = new MissilePartDef { defName = "W", category = MissilePartCategory.Warhead };
        var guidance = new MissilePartDef { defName = "G", category = MissilePartCategory.Guidance };
        var payload = new MissilePartDef { defName = "P", category = MissilePartCategory.Payload, jamStrength = 0.35f, chaffCharges = 3 };
        var spare = new MissilePartDef { defName = "P2", category = MissilePartCategory.Payload, jamStrength = 0.2f, chaffCharges = 1 };
        body.defaultParts = new List<MissilePartDef> { warhead, guidance, payload, spare };

        var cfg = MissileConfig.NewWithDefaults(body);
        Check(cfg.parts.Count == 3, "default parts cover each slot once");
        Check(cfg.PartFor(MissilePartCategory.Warhead) == warhead && cfg.PartFor(MissilePartCategory.Payload) == payload,
            "the first part of a category wins");
        Near(cfg.JamStrength, 0.35f, "jam strength takes the strongest part");
        Check(cfg.ChaffCharges == 3, "default payload carries its own chaff charges");
        var twoPayloads = new MissileConfig(body);
        twoPayloads.parts.Add(payload);
        twoPayloads.parts.Add(spare);
        Check(twoPayloads.ChaffCharges == 4, "chaff charges add up across parts");

        spare.compatibleBodies = new List<MissileBodyDef> { new MissileBodyDef { defName = "OtherBody" } };
        var filtered = MissileConfig.NewWithDefaults(body);
        Check(filtered.PartFor(MissilePartCategory.Payload) == payload, "incompatible defaults are dropped");

        var antiShip = new MissilePartDef
        {
            defName = "AS",
            category = MissilePartCategory.Guidance,
            guidanceType = new GuidanceType_AntiShip(),
        };
        var armed = new MissileConfig(body);
        armed.parts.Add(antiShip);
        Check(armed.TracksMobileTargets, "an anti-ship seeker tracks mobile targets");
        Check(armed.TerminalUpdates == 5, "an anti-ship seeker adds two terminal updates");
        Check(!cfg.HasTerminalGuidance && cfg.TerminalUpdates == 0, "no seeker part means no terminal updates");

        Near(MissileWorldMotion.AltitudeFactor(1000f), 1f, "1000 m is neutral");
        Check(MissileWorldMotion.AltitudeFactor(100f) < MissileWorldMotion.AltitudeFactor(1000f), "low altitude shortens detection");
        Check(MissileWorldMotion.AltitudeFactor(100000f) > MissileWorldMotion.AltitudeFactor(1000f), "high altitude lengthens detection");
        Near(MissileWorldMotion.AltitudeFactor(1f), 0.6f, "low altitude clamp");
        Near(MissileWorldMotion.AltitudeFactor(1000000f), 1.3f, "high altitude clamp");

        var low = new BVRTargetProps { altitude = 100f };
        var high = new BVRTargetProps { altitude = 100000f };
        Check(MissileWorldMotion.DetectionRange(12, 2, low) < MissileWorldMotion.DetectionRange(12, 2, high),
            "altitude scales the world detection range");
    }
    // P4 area defense: the general meeting solver must handle a target crossing the node's envelope.
    static void AreaDefense()
    {
        var track = new LineTrack { a = new Vector3(100, 0, 0), b = new Vector3(0, 0, 100), eta = 3000 };
        var origin = new Vector3(70, 70, 0);
        float speed = track.AngularSpeed * 1.5f;
        Check(BVRInterceptSolver.TrySolveGeneral(track, origin, speed, out int ticks, out Vector3 point),
            "the area-defense solver finds a meeting point for a crossing target");
        Check(ticks > 0 && ticks < track.TicksToArrival, "the meeting happens before the target arrives");
        Near(GenMath.SphericalDistance(origin.normalized, point.normalized) / ticks, speed,
            "the meeting point matches the interceptor speed", 0.01f);
        Check(!BVRInterceptSolver.TrySolveGeneral(track, origin, track.AngularSpeed * 0.05f, out _, out _),
            "a much slower interceptor cannot reach a crossing target");
        Near(track.AngularSpeed, GenMath.SphericalDistance(track.a.normalized, track.b.normalized) / 3000f,
            "the line track speed matches its path");
    }
    // Skyfaller → Projectile migration (docs/Missile_Skyfaller轉Projectile_計畫.md §5): headings, edge points, skyfaller angle.
    static void MissileMapEntry()
    {
        foreach (var (h, x, z) in new[] { (0f, 0f, 1f), (90f, 1f, 0f), (180f, 0f, -1f), (270f, -1f, 0f) })
        {
            Vector3 v = MissileMapEntryUtility.HeadingVector(h);
            Near(v.x, x, $"heading {h} x"); Near(v.z, z, $"heading {h} z");
        }
        foreach (float h in new[] { 0f, 12.5f, 45f, 133f, 180f, 222f, 300f, 359f })
        {
            Near(MissileMapEntryUtility.HeadingOf(MissileMapEntryUtility.HeadingVector(h)), h, $"heading round trip {h}", 0.01f);

            // A skyfaller with the converted angle sits on the heading side of its target (it flies in from there).
            Vector3 center = new Vector3(100f, 0f, 100f);
            Vector3 drawn = SkyfallerDrawPosUtility.DrawPos_Accelerate(center, 100, MissileMapEntryUtility.SkyfallerAngleForHeading(h), 1f);
            Near(MissileMapEntryUtility.HeadingOf(drawn - center), h, $"skyfaller angle matches heading {h}", 0.01f);
        }

        IntVec3 size = new IntVec3(250, 1, 250);
        Vector3 mid = new Vector3(125f, 0f, 125f);
        Vector3 n = MissileMapEntryUtility.EdgePoint(size, mid, 0f);
        Check(Math.Abs(n.x - 125f) < 0.01f && Math.Abs(n.z - 250f) < 0.01f, "north ray exits the +Z edge above the start");
        Vector3 e = MissileMapEntryUtility.EdgePoint(size, mid, 90f);
        Check(Math.Abs(e.x - 250f) < 0.01f && Math.Abs(e.z - 125f) < 0.01f, "east ray exits the +X edge");
        Vector3 ne = MissileMapEntryUtility.EdgePoint(size, mid, 45f);
        Check(Math.Abs(ne.x - 250f) < 0.01f && Math.Abs(ne.z - 250f) < 0.01f, "45° ray exits the north-east corner, not the middle of a side");
        foreach (var (from, h) in new[] { (new Vector3(10f, 0f, 200f), 300f), (new Vector3(240f, 0f, 30f), 170f), (new Vector3(60f, 0f, 60f), 215f), (mid, 359.5f) })
        {
            Vector3 p = MissileMapEntryUtility.EdgePoint(size, from, h);
            bool onEdge = Math.Abs(p.x) < 0.01f || Math.Abs(p.x - 250f) < 0.01f || Math.Abs(p.z) < 0.01f || Math.Abs(p.z - 250f) < 0.01f;
            Check(onEdge, $"edge point {p} lies on the map border (heading {h})");
            Near(MissileMapEntryUtility.HeadingOf(p - from), h, $"edge point keeps the true heading {h}", 0.01f);
            Vector3 inside = MissileMapEntryUtility.InsetFromEdge(size, p);
            Check(inside.x >= 0.5f && inside.x <= 249.5f && inside.z >= 0.5f && inside.z <= 249.5f, "inset edge point is inside the map");
        }

        foreach (var (h, rot) in new[] { (44.9f, Rot4.North), (45f, Rot4.East), (134.9f, Rot4.East), (135f, Rot4.South),
                                          (224.9f, Rot4.South), (225f, Rot4.West), (314.9f, Rot4.West), (315f, Rot4.North), (-10f, Rot4.North) })
        {
            Check(MissileMapEntryUtility.EdgeRot(h) == rot, $"heading {h} maps to the {rot} edge like PlanetLayer.GetRotFromTo");
        }
    }

    static void CiwsAimDirections()
    {
        var props = new CompProperties_TerminalCIWS { shotsPerBurst = 5, aimLeadCells = 4, aimSweepCells = 4 };
        var ciws = new CompTerminalCIWS { props = props };
        var size = new IntVec3(250, 1, 250);
        var origin = new IntVec3(125, 0, 125);
        foreach (float heading in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
        {
            var missile = new Projectile_MissileIncoming { def = Empty<ThingDef>(), angle = 0f };
            missile.SetEntryHeading(heading); // Deliberately differs from angle: held missiles retain their entry heading.
            var target = new BVRTarget(missile, origin, null, 1);
            IntVec3 far = ciws.CalculateAimCell(target, origin, size, 0f, 2);
            IntVec3 near = ciws.CalculateAimCell(target, origin, size, 1f, 2);
            Near(Mathf.Abs(Mathf.DeltaAngle(MissileMapEntryUtility.HeadingOf((far - origin).ToVector3()), heading)),
                0f, $"CIWS aims into incoming heading {heading}", 1f);
            Check((near - origin).LengthHorizontalSquared < (far - origin).LengthHorizontalSquared,
                $"CIWS follows approach toward landing from {heading}");
            IntVec3 first = ciws.CalculateAimCell(target, origin, size, 0.5f, 0);
            IntVec3 last = ciws.CalculateAimCell(target, origin, size, 0.5f, 4);
            Vector3 sweep = (last - first).ToVector3();
            Check(sweep.magnitude >= 7f, $"CIWS sweeps across heading {heading}");
            Check(Mathf.Abs(Vector3.Dot(sweep, MissileMapEntryUtility.HeadingVector(heading))) < 1.5f,
                $"CIWS sweep is perpendicular to heading {heading}");
        }
        // The aim follows the missile's landing-to-entry line, including a turret away from that line.
        var east = new Projectile_MissileIncoming { def = Empty<ThingDef>() };
        east.SetEntryHeading(90f);
        var offset = new BVRTarget(east, new IntVec3(70, 0, 40), null, 2);
        IntVec3 aim = ciws.CalculateAimCell(offset, origin, size, 0f, 2);
        Check(aim.x > origin.x && aim.z == 40, "CIWS uses entry trajectory rather than turret-to-north edge");
        props.shotsPerBurst = 1;
        foreach (var landing in new[] { new IntVec3(0, 0, 0), new IntVec3(249, 0, 249) })
        {
            offset.position = landing;
            aim = ciws.CalculateAimCell(offset, origin, size, 0f, 0);
            Check(aim.x >= 0 && aim.x < size.x && aim.z >= 0 && aim.z < size.z,
                "CIWS single-shot aim remains inside map at corners");
        }
        var noPayload = new BVRTarget { position = new IntVec3(20, 0, 125) };
        aim = ciws.CalculateAimCell(noPayload, origin, size, 0f, 0);
        Check(aim.x < origin.x && aim.z == origin.z, "CIWS missing-payload fallback uses target bearing");
        var legacy = new BVRTarget(new Skyfaller { def = Empty<ThingDef>(), angle = 270f }, origin, null, 3);
        aim = ciws.CalculateAimCell(legacy, origin, size, 0f, 0);
        Check(aim.x < origin.x && aim.z == origin.z, "CIWS follows skyfaller entry angle");
        var noHeading = new BVRTarget(new Projectile_MissileIncoming { def = Empty<ThingDef>(), angle = 180f }, origin, null, 4);
        aim = ciws.CalculateAimCell(noHeading, origin, size, 0f, 0);
        Check(aim.z < origin.z && aim.x == origin.x, "CIWS older missiles use flight angle when entry heading is absent");
    }

    public static void Main()
    {
        Setup();
        CiwsAimDirections();
        MissileMapEntry();
        Detection(); Solver(); FireControl(); LedgerAndTerminal(); InventoryAndPersistence(); StrikeP0(); StrikeP1(); StrikeP2(); StrikeP3(); StrikeP4();
        MissileContent(); AreaDefense();
        Console.WriteLine($"PASS: {count} BVR checks (real game/DMSE assemblies).");
    }
    class OnlineRadar : CompFireControlRadar { public override bool Active => true; }
    class ArmedLauncher : CompMissileLauncher_Interceptor { public bool armed; public override bool Active => armed; }
    class OnlineCIWS : CompTerminalCIWS { public bool online = true; public override bool Active => online; }
    class FakeTrack : IBVRTrack
    {
        public float angle, speed;
        public WorldObject Self => null;
        public Faction Attacker => null;
        public PlanetTile Origin => PlanetTile.Invalid;
        public PlanetTile Destination => PlanetTile.Invalid;
        public BVRTargetProps TargetProps => BVRTargetProps.Default;
        public MissileConfig Config => null;
        public float AngularSpeed => speed;
        public int TicksToArrival => (int)Math.Ceiling(angle / speed);
        public Vector3 PositionAt(int ticksAhead)
        {
            float a = Math.Min(angle, speed * ticksAhead);
            return new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 100;
        }
        public BVRTrackState State { get; } = new BVRTrackState();
        public void Notify_Intercepted(Faction by) { }
    }
    class LineTrack : IBVRTrack
    {
        public Vector3 a, b;
        public int eta = 1000;
        public WorldObject Self => null;
        public Faction Attacker => null;
        public PlanetTile Origin => PlanetTile.Invalid;
        public PlanetTile Destination => PlanetTile.Invalid;
        public BVRTargetProps TargetProps => BVRTargetProps.Default;
        public MissileConfig Config => null;
        public float AngularSpeed => GenMath.SphericalDistance(a.normalized, b.normalized) / Mathf.Max(1, eta);
        public int TicksToArrival => eta;
        public Vector3 PositionAt(int ticksAhead)
            => Vector3.Slerp(a, b, Mathf.Clamp01(ticksAhead / (float)Mathf.Max(1, eta)));
        public BVRTrackState State { get; } = new BVRTrackState();
        public void Notify_Intercepted(Faction by) { }
    }
}
