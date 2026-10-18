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
    }
    public static void Main()
    {
        Setup();
        Detection(); Solver(); FireControl(); LedgerAndTerminal(); InventoryAndPersistence(); StrikeP0();
        Console.WriteLine($"PASS: {count} BVR checks (real game/DMSE assemblies).");
    }
    class OnlineRadar : CompFireControlRadar { public override bool Active => true; }
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
}
