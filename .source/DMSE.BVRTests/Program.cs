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
using Verse.Grammar;

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
        LanguageDatabase.activeLanguage = new LoadedLanguage("English");
        Field(LanguageDatabase.activeLanguage, "workerInt", new LanguageWorker_English());
        Field(LanguageDatabase.activeLanguage, "dataIsLoaded", true);
        foreach (string file in Directory.GetFiles("1.7/Languages/English/Keyed", "*.xml"))
        {
            foreach (XElement entry in XDocument.Load(file).Root.Elements())
            {
                string key = entry.Name.LocalName;
                LanguageDatabase.activeLanguage.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = entry.Value };
            }
        }
        LanguageDatabase.activeLanguage.keyedReplacements["PeriodSeconds"] =
            new LoadedLanguage.KeyedReplacement { key = "PeriodSeconds", value = "{0} seconds" };
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

        Tick(101000);
        planner.AddGuidanceHold(side, 101600, 2);
        planner.Enqueue(new StrikeQueuedLaunch { console = Parent(side), missionId = 2, fireTick = 101030, channelCost = 3 });
        Check(planner.GuidanceOccupied(side) == 5 && manager.Channels.StrikeFree(side) == 0, "weighted queue and flight holds share the full channel pool");
        Check(planner.GuidanceReleaseTicks(side).Count() == 5, "timeline receives a release tick for every occupied channel");
        planner.DropQueued(2);
        Check(planner.GuidanceOccupied(side) == 2, "cancelling a TV ballistic queue releases all three booked channels");
        Tick(101600);
        Check(planner.GuidanceOccupied(side) == 0, "weighted guidance hold releases all channels at its deadline");
        Check(new StrikeQueuedLaunch().channelCost == 1 && new StrikeGuidanceHold().channelCost == 1,
            "legacy reservations retain their one-channel default");

        var ordinary = new MissileConfig(new MissileBodyDef { sizeClass = MissileSizeClass.Medium });
        var ballistic = new MissileConfig(new MissileBodyDef { sizeClass = MissileSizeClass.Ballistic });
        var tv = new MissilePartDef { category = MissilePartCategory.Guidance, guidanceType = new GuidanceType_TV() };
        Check(ordinary.StrikeChannelCost == 1 && ballistic.StrikeChannelCost == 2, "ordinary and ballistic base channel costs");
        ordinary.parts.Add(tv); ballistic.parts.Add(tv);
        Check(ordinary.StrikeChannelCost == 2 && ballistic.StrikeChannelCost == 3, "TV adds one channel to either base cost");
        var builtInTV = new MissileConfig(new MissileBodyDef { sizeClass = MissileSizeClass.Ballistic, builtInGuidance = new GuidanceType_TV() });
        Check(builtInTV.StrikeChannelCost == 3, "built-in TV uses the same channel surcharge");
        builtInTV.parts.Add(new MissilePartDef { category = MissilePartCategory.Guidance, guidanceType = new GuidanceType_Laser() });
        Check(builtInTV.StrikeChannelCost == 2, "an installed non-TV seeker overrides built-in TV for channel cost");
        var weighted = StrikeRoundUtility.AutoAssign(new[] { 1, 2, 3, 1, 2 }, 4, 5);
        Check(weighted.Select(w => string.Join(",", w)).SequenceEqual(new[] { "0,1", "2,3", "4" }), "auto assignment packs weighted costs without splitting missiles");
        Check(StrikeRoundUtility.AutoAssign(new[] { 3, 1, 2 }, 2, 5).SelectMany(w => w).SequenceEqual(new[] { 1, 2 }),
            "auto assignment leaves individually unaffordable missiles out");
        Check(StrikeRoundUtility.AutoAssign(new[] { 2, 2, 2 }, 2, 2).Count == 2, "weighted auto assignment respects the wave limit");

        // A real map/component ledger backs the exact eligibility method used by the launch Gizmo.
        map.components.Add(manager);
        Current.Game.Maps.Add(map);
        var launchParent = Parent(side);
        Field(launchParent, "mapIndexOrState", (sbyte)(Current.Game.Maps.Count - 1));
        var manual = new ManualLauncher { parent = launchParent, config = ballistic };
        Check(manual.SingleLaunchDisabledReason() == null, "single TV ballistic shot is enabled with three free guidance channels");
        planner.AddGuidanceHold(side, 102000, 1);
        string unavailableChannels = manual.SingleLaunchDisabledReason();
        Check(unavailableChannels != null, "single TV ballistic shot is disabled when only two channels are free");
        Check(manual.TryLaunchRound(GlobalTargetInfo.Invalid, launchParent, false, out string channelFailure) == null
            && channelFailure == unavailableChannels && manual.ammo,
            "actual launch rechecks channels after target selection and leaves ammo untouched on failure");
        manual.config = new MissileConfig(new MissileBodyDef { sizeClass = MissileSizeClass.Ballistic });
        Check(manual.SingleLaunchDisabledReason() == null, "ordinary ballistic shot fits two remaining channels");
        manual.online = false;
        Check(manual.SingleLaunchDisabledReason() != null, "single shot is disabled on an inactive platform");
        manual.online = true;
        Field(manual, "cooldownUntil", 102000);
        Check(manual.SingleLaunchDisabledReason() != null, "single shot is disabled during cooldown");
        Field(manual, "cooldownUntil", 0);
        manual.ammo = false;
        Check(manual.SingleLaunchDisabledReason() != null, "single shot is disabled without ammunition");
        manual.ammo = true;
        planner.TryReserve(launchParent, 9);
        Check(manual.SingleLaunchDisabledReason() != null, "single shot cannot use ammunition reserved by a strike mission");
        planner.Release(launchParent);
        Tick(102000);
        manager.fireControlRadars.Clear();
        Check(manual.SingleLaunchDisabledReason() != null, "single shot is disabled without an active strike-capable fire-control radar");
        Current.Game.Maps.Remove(map);
        Field(launchParent, "mapIndexOrState", (sbyte)-2);

        string channelSave = Path.Combine(Path.GetTempPath(), "dmse-strike-channel-cost.xml");
        var holdSave = new StrikeGuidanceHold { channelCost = 3, releaseTick = 103000 };
        var queueSave = new StrikeQueuedLaunch { channelCost = 2, fireTick = 102030 };
        Scribe.saver.InitSaving(channelSave, "Channels");
        Scribe_Deep.Look(ref holdSave, "hold");
        Scribe_Deep.Look(ref queueSave, "queued");
        Scribe.saver.FinalizeSaving();
        var channelXml = XDocument.Load(channelSave);
        Check(channelXml.Descendants("channelCost").Select(e => e.Value).SequenceEqual(new[] { "3", "2" }),
            "real Scribe save output preserves weighted guidance and queue costs");
        File.Delete(channelSave);

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
        var heavy = PlanSlot(new object(), 1000); heavy.channelCost = 3;
        var weightedTimeline = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>>
        {
            new List<StrikePlanSlot> { heavy }, new List<StrikePlanSlot> { heavy }
        }, 4, 600, 60, 0, new int[0]);
        Check(weightedTimeline[0].fireTick == 0 && weightedTimeline[1].fireTick == 601,
            "a TV ballistic launch holds three channels until guidance ends");
        var weightedOver = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>> { new List<StrikePlanSlot> { heavy } },
            2, 600, 60, 0, new int[0]);
        Check(weightedOver[0].overCapacity && weightedOver[0].fireTick == -1, "one costly missile can exceed wave channel capacity");
        var weightedBusy = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>> { new List<StrikePlanSlot> { heavy } },
            4, 600, 60, 0, new[] { 300, 500 });
        Check(weightedBusy[0].fireTick == 300, "weighted wave waits for enough channels rather than just one free channel");

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
    // NPC air-defense alert (docs/NPC防空警戒五階段_執行計畫.md §3, §5, §6.1): pure rules and route geometry.
    static void AirDefenseAlert()
    {
        Faction defender = Faction(false), neutral = Faction(false), ally = Faction(false);
        Field(defender, "relations", new List<FactionRelation>
        {
            new FactionRelation(ally, FactionRelationKind.Ally), new FactionRelation(neutral, FactionRelationKind.Neutral)
        });
        Check(!AirDefenseAlertPolicy.MayEngage(2, defender, neutral, defender), "alert: stage 2 only observes");
        Check(AirDefenseAlertPolicy.MayEngage(3, defender, neutral, defender), "alert: stage 3 engages missiles aimed at its own faction");
        Check(!AirDefenseAlertPolicy.MayEngage(3, defender, neutral, neutral), "alert: stage 3 lets third-party strikes pass");
        Check(AirDefenseAlertPolicy.MayEngage(4, defender, neutral, neutral), "alert: stage 4 engages neutral transit");
        Check(AirDefenseAlertPolicy.MayEngage(5, defender, neutral, null), "alert: stage 5 engages transit to an empty tile");
        Check(!AirDefenseAlertPolicy.MayEngage(5, defender, ally, defender), "alert: allies are never engaged");
        Check(!AirDefenseAlertPolicy.MayEngage(5, defender, defender, defender), "alert: own missiles at own sites are not attacks");
        Check(!AirDefenseAlertPolicy.MayEngage(4, defender, null, neutral), "alert: unknown transit is not engaged");
        Check(AirDefenseAlertPolicy.MayEngage(4, defender, null, defender), "alert: unknown attacker on own site is engaged");
        Check(AirDefenseAlertPolicy.MayObserve(defender, null) && AirDefenseAlertPolicy.MayObserve(defender, neutral)
            && !AirDefenseAlertPolicy.MayObserve(defender, ally) && !AirDefenseAlertPolicy.MayObserve(defender, defender),
            "alert: neutral and unknown missiles are observed, own and allied ones are not");
        Near(AirDefenseAlertPolicy.DetectionFactor(1, true), 0f, "alert: stage 1 has no local detection");
        Near(AirDefenseAlertPolicy.DetectionFactor(2, true), 0.4f, "alert: stage 2 uses the observation radius");
        Near(AirDefenseAlertPolicy.DetectionFactor(4, false), 0f, "alert: radar warming up detects nothing");
        Near(AirDefenseAlertPolicy.DetectionFactor(4, true), 1f, "alert: stage 4 uses full radius");

        AirDefenseStageInputs In(int cur, float threat, int waves = 0, bool direct = false, bool hold = false, int baseline = 2, bool radar = true)
            => new AirDefenseStageInputs { current = cur, threat = threat, recentWaves = waves, directThreat = direct,
                holdElapsed = hold, baseline = baseline, hasRadar = radar };
        Check(AirDefenseAlertPolicy.NextStage(In(2, 20)) == 3, "alert: T 20 raises stage 2 to 3");
        Check(AirDefenseAlertPolicy.NextStage(In(2, 5, direct: true)) == 3, "alert: a direct threat jumps to stage 3");
        Check(AirDefenseAlertPolicy.NextStage(In(3, 50)) == 4, "alert: T 50 raises to stage 4");
        Check(AirDefenseAlertPolicy.NextStage(In(4, 50, waves: 5)) == 4, "alert: regional pressure alone (0.5R <= 50) cannot blockade");
        Check(AirDefenseAlertPolicy.NextStage(In(4, 85, waves: 1)) == 4, "alert: a blockade needs two attack waves");
        Check(AirDefenseAlertPolicy.NextStage(In(4, 85, waves: 2)) == 5, "alert: T 80 with two waves orders a blockade");
        Check(AirDefenseAlertPolicy.NextStage(In(4, 40, hold: true)) == 4, "alert: stage 4 holds above its down threshold");
        Check(AirDefenseAlertPolicy.NextStage(In(4, 30, hold: false)) == 4, "alert: minimum hold delays a downgrade");
        Check(AirDefenseAlertPolicy.NextStage(In(4, 30, hold: true)) == 3, "alert: T below 35 drops to stage 3");
        Check(AirDefenseAlertPolicy.NextStage(In(5, 0, hold: true)) == 4, "alert: downgrades move one stage at a time");
        Check(AirDefenseAlertPolicy.NextStage(In(5, 70, waves: 0, hold: true)) == 4, "alert: expired attack records lift a blockade");
        Check(AirDefenseAlertPolicy.NextStage(In(5, 70, waves: 1, hold: true)) == 5, "alert: a blockade holds above 60");
        Check(AirDefenseAlertPolicy.NextStage(In(3, 0, hold: true, direct: true)) == 3, "alert: a direct threat keeps stage 3");
        Check(AirDefenseAlertPolicy.NextStage(In(3, 0, hold: true, baseline: 3)) == 3, "alert: never below the baseline");
        Check(AirDefenseAlertPolicy.NextStage(In(1, 90, waves: 3, baseline: 1, radar: false)) == 1, "alert: no radar stays at stage 1");
        Check(AirDefenseAlertPolicy.NextStage(In(1, 4, baseline: 1)) == 2, "alert: a credible alert wakes stage 1 with a radar");

        float applied = 0f;
        Near(AirDefenseAlertPolicy.TopUp(ref applied, 4f), 4f, "alert: first transit event");
        Near(AirDefenseAlertPolicy.TopUp(ref applied, 20f), 16f, "alert: targeted upgrade only adds the difference");
        Near(AirDefenseAlertPolicy.TopUp(ref applied, 4f), 0f, "alert: repeated lower event adds nothing");
        Near(AirDefenseAlertPolicy.EffectiveThreat(10f, 60f), 30f, "alert: T = max(L, 0.5 R)");
        Near(AirDefenseAlertPolicy.Decayed(10f, 2500), 8f, "alert: 2 points per game hour");

        // Fixture surface: radius 100, 200 tiles per radian.
        Vector3 a = new Vector3(100, 0, 0), b = new Vector3(0, 0, 100), mid = (a + b).normalized * 100f;
        Near(AirspaceRestrictionUtility.SegmentDistanceTiles(mid, a, b), 0f, "airspace: point on the route", 0.01f);
        Vector3 above = (mid.normalized * Mathf.Cos(0.05f) + Vector3.up * Mathf.Sin(0.05f)) * 100f;
        Near(AirspaceRestrictionUtility.SegmentDistanceTiles(above, a, b), 10f, "airspace: transit passing 10 tiles from a node", 0.05f);
        Vector3 beyond = new Vector3(-Mathf.Sin(0.1f), 0, Mathf.Cos(0.1f)) * 100f;
        Near(AirspaceRestrictionUtility.SegmentDistanceTiles(beyond, a, b), 20f, "airspace: past the endpoint uses the endpoint", 0.05f);
        Near(AirspaceRestrictionUtility.SegmentDistanceTiles(a, a, a), 0f, "airspace: single-point takeoff check");
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

    // docs/攔截殘骸特效_實作與技術美術計畫.md §3–§4: burst point on the incoming route, debris keeps the incoming momentum.
    static void InterceptDebrisGeometry()
    {
        var p = InterceptDebrisPhysics.Default;
        var size = new IntVec3(250, 1, 250);
        var center = new IntVec3(125, 0, 125);
        var landing = new IntVec3(125, 0, 125);
        Vector3 landingPos = landing.ToVector3Shifted(); landingPos.y = 0f;
        foreach (float heading in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
        {
            var missile = new Projectile_MissileIncoming { def = Empty<ThingDef>(), angle = 0f };
            missile.SetEntryHeading(heading);
            Vector3 back = MissileMapEntryUtility.HeadingVector(heading);
            Vector3 forward = -back;
            Vector3 edge = MissileMapEntryUtility.EdgePoint(size, landingPos, heading);

            // Midcourse: virtual burst outside the map; only remote debris fades into view.
            var mid = InterceptEventData.Build(missile, landing, size, center, false, IntVec3.Invalid, 0f, null, 17 + (int)heading, p);
            Check(mid.hasHeading && mid.payloadKind == InterceptPayloadKind.Missile, $"intercept data reads missile heading {heading}");
            Near(mid.heading, heading, $"intercept data heading {heading}");
            Vector3 drawn = mid.BurstDrawPos(p); drawn.y = 0f;
            Check(mid.remote && InterceptGeometry.DrawHidden(mid.burstGround, mid.burstHeight, size, p),
                $"midcourse burst is hidden outside the map for {heading}");
            Check(InterceptGeometry.DistanceOutside(size, drawn) > 0f,
                $"midcourse burst draw position is outside the map for {heading}");
            Near(drawn.z - mid.burstGround.z, mid.burstHeight * p.heightScale, $"midcourse ground lies under the drawn burst for {heading}", 0.01f);
            Check(mid.burstHeight >= p.midcourseHeight.min && mid.burstHeight <= p.midcourseHeight.max, $"midcourse height for {heading}");


            // Terminal: at the CIWS aim cell, jittered only along the route; height follows the approach.
            IntVec3 aim = (landingPos + back * 30f).ToIntVec3();
            var onMap = InterceptEventData.Build(missile, landing, size, center, true, aim, p.terminalRemoteApproach, null, 3, p);
            var near = InterceptEventData.Build(missile, landing, size, center, true, aim, 1f, null, 3, p);
            Vector3 offset = onMap.burstGround - aim.ToVector3Shifted(); offset.y = 0f;
            Check(Mathf.Abs(Vector3.Dot(offset, new Vector3(back.z, 0f, -back.x))) < 0.01f
                && offset.magnitude <= p.terminalJitter + 0.01f, $"terminal burst stays on the CIWS aim line for {heading}");
            Check(!onMap.remote && !near.remote, $"terminal bursts drawn inside the map keep their airburst for {heading}");
            Near(near.burstHeight, p.terminalHeightNear, $"terminal height near {heading}");

            // Far terminal (just entered the window): a virtual burst beyond the entry edge, never drawn, debris fades in.
            var far = InterceptEventData.Build(missile, landing, size, center, true, aim, 0f, null, 3, p);
            Near(far.burstHeight, p.terminalHeightFar, $"terminal height far {heading}");
            Check(far.remote, $"far terminal intercept is remote for {heading}");
            Check(InterceptGeometry.DrawHidden(far.burstGround, far.burstHeight, size, p),
                $"far terminal burst is drawn out of sight for {heading} (south entries pushed further out)");
            Check(Vector3.Dot(far.burstGround - edge, back) >= -0.01f, $"far terminal ground point is at or beyond the entry edge for {heading}");
            Rand.PushState(2000 + (int)heading);
            try
            {
                int visibleLight = 0, visibleHeavy = 0;
                const int tries = 200;
                for (int i = 0; i < tries; i++)
                {
                    DebrisState light = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, far.targetSpeed, 0.4f, p);
                    DebrisState heavy = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, far.targetSpeed, 2f, p);
                    if (InterceptGeometry.BecomesVisible(light, size, p, Vector3.zero)) { visibleLight++; }
                    if (InterceptGeometry.BecomesVisible(heavy, size, p, Vector3.zero)) { visibleHeavy++; }
                }
                Console.WriteLine($"  remote debris heading {heading}: distance beyond edge {Vector3.Dot(far.burstGround - edge, back):0.#}, "
                    + $"visible light {visibleLight * 100 / tries}% heavy {visibleHeavy * 100 / tries}%");
                Check(visibleHeavy >= tries / 2, $"most heavy chunks from a far intercept reach the screen for {heading} ({visibleHeavy}/{tries})");
                Check(visibleLight > 0, $"some light fragments from a far intercept reach the screen for {heading} ({visibleLight}/{tries})");
            }
            finally { Rand.PopState(); }

            // Debris keeps the incoming momentum: mean horizontal velocity points along the flight direction,
            // landings fall ahead of the burst, heavy chunks carry further than light fragments.
            Rand.PushState(1000 + (int)heading);
            try
            {
                Vector3 meanVelocity = Vector3.zero, meanLight = Vector3.zero, meanHeavy = Vector3.zero;
                int maxTicks = 0;
                const int n = 200;
                for (int i = 0; i < n; i++)
                {
                    DebrisState light = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, 1f, 0.2f, p);
                    DebrisState heavy = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, 1f, 2.5f, p);
                    meanVelocity += light.velocity + heavy.velocity;
                    meanLight += InterceptGeometry.SimulateLanding(light, p, Vector3.zero, out int t1) - far.burstGround;
                    meanHeavy += InterceptGeometry.SimulateLanding(heavy, p, Vector3.zero, out int t2) - far.burstGround;
                    maxTicks = Mathf.Max(maxTicks, Mathf.Max(t1, t2));
                }
                Check(Vector3.Angle(meanVelocity, forward) < 30f, $"debris velocity follows incoming direction {heading}");
                Check(Vector3.Dot(meanLight / n, forward) > 0f && Vector3.Dot(meanHeavy / n, forward) > 0f,
                    $"debris lands ahead of the burst for {heading}");
                Check(Vector3.Dot(meanHeavy / n, forward) > Vector3.Dot(meanLight / n, forward),
                    $"heavy debris carries further than light fragments for {heading}");
                Check(maxTicks < p.maxLifeTicks, $"all debris lands within the lifetime for {heading} ({maxTicks})");
            }
            finally { Rand.PopState(); }
        }

        // Fall time (P6 tuning, plan §11 "1–2 s"): a heavy chunk from a mid terminal height lands in roughly 1–2 seconds.
        Rand.PushState(11);
        try
        {
            int total = 0;
            for (int i = 0; i < 50; i++)
            {
                InterceptGeometry.SimulateLanding(InterceptGeometry.Launch(Vector3.zero, 14f, 90f, 1f, 2f, p), p, Vector3.zero, out int ticks);
                total += ticks;
            }
            float mean = total / 50f;
            Check(mean >= 55f && mean <= 130f, $"heavy chunk from 14 cells lands in {mean:0} ticks on average (55–130)");
        }
        finally { Rand.PopState(); }

        // A hulk keeps the most momentum and barely spreads.
        Rand.PushState(5);
        try
        {
            DebrisState hulk = InterceptGeometry.Launch(Vector3.zero, 15f, 90f, 1f, 2f, p, hulk: true);
            Check(Vector3.Angle(hulk.velocity, Vector3.left) < 10f, "hulk flies on along the incoming direction");
        }
        finally { Rand.PopState(); }

        // Heading fallbacks: skyfaller angle (drop pods), then map center → landing.
        var pod = InterceptEventData.Build(new Skyfaller { def = Empty<ThingDef>(), angle = 200f }, landing, size, center, true,
            IntVec3.Invalid, 0.5f, null, 9, p);
        Check(pod.payloadKind == InterceptPayloadKind.DropPod && Mathf.Abs(pod.heading - 200f) < 0.001f, "drop pod uses skyfaller angle");
        var none = InterceptEventData.Build(null, new IntVec3(200, 0, 125), size, center, false, IntVec3.Invalid, 0f, null, 9, p);
        Check(!none.hasHeading && Mathf.Abs(Mathf.DeltaAngle(none.heading, 90f)) < 0.5f, "missing payload falls back to center bearing");
        Check(none.remote && none.burstGround.x > size.x,
            "fallback midcourse burst is virtual beyond the east edge");
        Near(InterceptGeometry.AverageSpeed(null), InterceptGeometry.FallbackSpeed, "unknown payload speed fallback");

        // A terminal burst whose height lift carries it past the north edge is no longer pulled back onto the edge:
        // it stays at the aim point and becomes remote (no airburst, debris fades in).
        var north = new Projectile_MissileIncoming { def = Empty<ThingDef>() };
        north.SetEntryHeading(0f);
        var edgeBurst = InterceptEventData.Build(north, new IntVec3(125, 0, 245), size, center, true, new IntVec3(125, 0, 249), 0.5f, null, 4, p);
        Check(edgeBurst.remote && Mathf.Abs(edgeBurst.burstGround.z - 249.5f) <= p.terminalJitter + 0.01f,
            "terminal burst lifted past the north edge stays at the aim point and turns remote");
        Near(edgeBurst.burstHeight, Mathf.Lerp(p.terminalHeightFar, p.terminalHeightNear, 0.5f), "remote edge burst keeps its height");

        // Fade band: transparent within drawEdgeInset of the border (and outside), opaque remoteFadeCells further in.
        Near(InterceptGeometry.EdgeFade(size, new Vector3(-3f, 0f, 125f), p), 0f, "edge fade is 0 off the map");
        Near(InterceptGeometry.EdgeFade(size, new Vector3(125f, 0f, size.z - p.drawEdgeInset + 0.5f), p), 0f, "edge fade is 0 inside the edge band");
        Near(InterceptGeometry.EdgeFade(size, new Vector3(p.drawEdgeInset + p.remoteFadeCells * 0.5f, 0f, 125f), p), 0.5f,
            "edge fade is half way through the fade cells", 0.01f);
        Near(InterceptGeometry.EdgeFade(size, new Vector3(125f, 0f, 125f), p), 1f, "edge fade is 1 well inside the map");
        Check(InterceptGeometry.ForwardTravel(0f, 18f, p) < 0.01f && InterceptGeometry.ForwardTravel(0.3f, 18f, p) < InterceptGeometry.ForwardTravel(1f, 18f, p),
            "representative chunk travel grows with the target speed");

        // Remote pieces in the container start transparent, are not culled while still outside, fade in, and leave normally.
        var fleckDefRemote = new FleckDef { solidTime = 10f, fadeOutTime = 0.5f };
        var remoteFlecks = new InterceptDebrisFlecks(p);
        var westMissile = new Projectile_MissileIncoming { def = Empty<ThingDef>() };
        westMissile.SetEntryHeading(270f);
        var west = InterceptEventData.Build(westMissile, landing, size, center, true, IntVec3.Invalid, 0f, null, 21, p);
        Check(west.remote && west.burstGround.x < 0f, "far terminal from the west puts the virtual burst off the west edge");
        Rand.PushState(78);
        try
        {
            for (int i = 0; i < 80 && remoteFlecks.Count < 30; i++)
            {
                DebrisState s = InterceptGeometry.Launch(west.burstGround, west.burstHeight, west.heading, west.targetSpeed, Rand.Range(0.2f, 0.6f), p);
                if (InterceptGeometry.BecomesVisible(s, size, p, Vector3.zero)) { remoteFlecks.Spawn(fleckDefRemote, s, 0.6f, Color.white, Color.grey, 30, remote: true); }
            }
        }
        finally { Rand.PopState(); }
        int remoteSpawned = remoteFlecks.Count;
        Check(remoteSpawned > 0, $"visible remote fragments are spawned ({remoteSpawned})");
        bool allStartHidden = true;
        for (int i = 0; i < remoteFlecks.Count; i++) { allStartHidden &= remoteFlecks.Pieces[i].fleck.instanceColor.a <= 0f; }
        Check(allStartHidden, "remote fragments start fully transparent");
        float maxAlpha = 0f;
        for (int tick = 0; tick < p.maxLifeTicks + 10 && remoteFlecks.Count > 0; tick++)
        {
            remoteFlecks.Tick(null, size, Vector3.zero);
            for (int i = 0; i < remoteFlecks.Count; i++) { maxAlpha = Mathf.Max(maxAlpha, remoteFlecks.Pieces[i].fleck.instanceColor.a); }
        }
        Check(remoteFlecks.Count == 0 && remoteFlecks.expiredCount == 0
            && remoteFlecks.landedCount + remoteFlecks.culledCount == remoteSpawned, "remote fragments leave by landing or culling");
        Check(maxAlpha >= p.remoteMinFade, $"remote fragments fade in once inside the map ({maxAlpha:0.##})");

        // B-layer debris container (§5.3): every piece leaves by landing or culling, never by the fleck lifetime;
        // pieces from an eastern entry land west of the burst; the drawn height follows the state.
        var fleckDef = new FleckDef { solidTime = 10f, fadeOutTime = 0.5f };
        var flecks = new InterceptDebrisFlecks(p);
        Vector3 burstGround = new Vector3(200f, 0f, 125f);
        Rand.PushState(77);
        try
        {
            for (int i = 0; i < 60; i++)
            {
                flecks.Spawn(fleckDef, InterceptGeometry.Launch(burstGround, 15f, 90f, 1f, Rand.Range(0.2f, 0.6f), p),
                    0.6f, Color.white, Color.grey, 30);
            }
        }
        finally { Rand.PopState(); }
        Check(flecks.Count == 60, "debris container holds every spawned piece");
        Near(flecks.Pieces[0].fleck.position.height, 15f * p.heightScale, "debris fleck height starts at the burst height");
        float sumX = 0f; int landedSeen = 0;
        for (int tick = 0; tick < p.maxLifeTicks + 10 && flecks.Count > 0; tick++)
        {
            for (int i = 0; i < flecks.Count; i++)
            {
                if (flecks.Pieces[i].state.height <= flecks.Pieces[i].state.verticalSpeed * -1.01f) { sumX += flecks.Pieces[i].state.ground.x; landedSeen++; }
            }
            flecks.Tick(null, size, Vector3.zero);
        }
        Check(flecks.Count == 0, "all debris pieces leave the container");
        Check(flecks.landedCount + flecks.culledCount == 60 && flecks.expiredCount == 0,
            $"debris leaves by landing or culling, not lifetime (landed {flecks.landedCount}, culled {flecks.culledCount})");
        Check(landedSeen > 0 && sumX / landedSeen < burstGround.x, "debris from an eastern entry lands west of the burst");

        // Culling: far off-map or too old.
        var cull = new DebrisState { ground = new Vector3(-p.offMapCull - 1f, 0f, 10f) };
        Check(InterceptGeometry.ShouldCull(cull, size, p), "debris far off-map is culled");
        cull.ground = new Vector3(-5f, 0f, 10f);
        Check(!InterceptGeometry.ShouldCull(cull, size, p), "debris just off-map keeps flying");
    }

    // docs/導彈發射特效_重製計畫.md §4.2 (L1): drifting smoke puffs stop, rise, drift with the wind, grow, fade, and expire.
    static void FxSmokeMotion()
    {
        var spec = new SmokePuffSpec
        {
            def = new FleckDef { solidTime = 1000f },
            pos = new Vector3(100f, 0f, 100f),
            velocity = new Vector3(0f, 0f, -1f),
            size = 2f, growth = 3f, lifeTicks = 480,
            drag = 0.08f, buoyancy = 0.004f, windFactor = 0.01f,
            hotColor = new Color(1f, 0.7f, 0.4f, 0.9f), color = new Color(1f, 1f, 1f, 0.8f), hotTicks = 10
        };

        // Exhaust push dies out within ~0.5 s; buoyancy then lifts the puff (+z on screen).
        Vector3 pos = spec.pos, vel = spec.velocity;
        for (int i = 0; i < 30; i++) { FxSmokeParticles.Step(ref pos, ref vel, spec, Vector3.zero); }
        Check(Mathf.Abs(vel.z) < 0.15f, $"smoke exhaust push dies out within 30 ticks (vz {vel.z:0.###})");
        float zAt30 = pos.z;
        for (int i = 0; i < 120; i++) { FxSmokeParticles.Step(ref pos, ref vel, spec, Vector3.zero); }
        Check(vel.z > 0f && pos.z > zAt30, "buoyancy lifts the smoke afterwards");

        // Wind drifts it downwind.
        Vector3 wpos = spec.pos, wvel = Vector3.zero;
        for (int i = 0; i < 120; i++) { FxSmokeParticles.Step(ref wpos, ref wvel, spec, new Vector3(1.5f, 0f, 0f)); }
        Check(wpos.x - spec.pos.x > 1f, $"wind drifts the smoke downwind ({wpos.x - spec.pos.x:0.#} cells)");

        // Growth and fade.
        Near(FxSmokeParticles.SizeAt(spec, 0), 2f, "smoke starts at its spawn size");
        Near(FxSmokeParticles.SizeAt(spec, spec.lifeTicks), 2f * (1f + 3f), "smoke ends at size × (1 + growth)");
        Check(FxSmokeParticles.SizeAt(spec, 120) < FxSmokeParticles.SizeAt(spec, 240), "smoke keeps widening");
        Check(FxSmokeParticles.AlphaAt(spec, 0) < 0.5f && Mathf.Abs(FxSmokeParticles.AlphaAt(spec, 50) - 1f) < 0.001f, "smoke fades in");
        Check(FxSmokeParticles.AlphaAt(spec, 300) < 1f && FxSmokeParticles.AlphaAt(spec, 470) < 0.05f, "smoke fades out over the second half of its life");

        // Container: puffs expire at their life; a puff blown off the map is culled.
        var smoke = new FxSmokeParticles();
        var size = new IntVec3(250, 1, 250);
        Rand.PushState(3);
        try
        {
            smoke.Spawn(spec);
            var older = spec; older.ageOffset = 400;
            smoke.Spawn(older);
            var edge = spec; edge.pos = new Vector3(249f, 0f, 100f); edge.velocity = new Vector3(2f, 0f, 0f); edge.drag = 0f;
            smoke.Spawn(edge);
        }
        finally { Rand.PopState(); }
        Check(smoke.Count == 3, "smoke container holds every puff");
        for (int i = 0; i < 100; i++) { smoke.Tick(size, Vector3.zero); }
        Check(smoke.Count == 1 && smoke.expiredCount == 1 && smoke.culledCount == 1,
            $"older puff expired and the off-map puff was culled (left {smoke.Count}, expired {smoke.expiredCount}, culled {smoke.culledCount})");
        for (int i = 0; i < 400; i++) { smoke.Tick(size, Vector3.zero); }
        Check(smoke.Count == 0 && smoke.expiredCount == 2, "the remaining puff expires at its life");

        // Ambient light is multiplied in (alpha untouched); amount 0 = self-lit, 1 = fully lit; no map = white.
        Color dusk = new Color(0.78f, 0.64f, 0.48f, 1f);
        Color lit = FxLighting.Apply(new Color(1f, 1f, 1f, 0.8f), dusk);
        Check(Mathf.Abs(lit.r - 0.78f) < 0.001f && Mathf.Abs(lit.b - 0.48f) < 0.001f && Mathf.Abs(lit.a - 0.8f) < 0.001f, "lighting multiplies the colour, keeps alpha");
        Color selfLit = FxLighting.Apply(new Color(1f, 0.7f, 0.4f, 1f), dusk, 0f);
        Check(Mathf.Abs(selfLit.g - 0.7f) < 0.001f, "self-lit (amount 0) ignores the ambient light");
        Color half = FxLighting.Apply(Color.white, dusk, 0.5f);
        Check(Mathf.Abs(half.b - 0.74f) < 0.001f, "partly self-lit blends toward the ambient light");
        Check(FxLighting.LightAt(null, Vector3.zero) == Color.white, "no map: unlit (white)");

        // Smoke shade is a grey tint on one texture (§0.5), matching the old light/mid/dark texture mix on average.
        Color tint = new Color(1f, 0.9f, 0.8f, 0.4f);
        Near(LaunchFxEmitter.ShadeTint(tint, 0f, 0f).r, 1f, "shade 0: the light texture untinted");
        Near(LaunchFxEmitter.ShadeTint(tint, 1f, 0f).r, 0.32f / 0.95f, "shade 1: as dark as the old dark texture");
        Near(LaunchFxEmitter.ShadeTint(tint, 0.5f, 0f).r, 0.25f + 0.5f * 0.6f / 0.95f + 0.25f * 0.32f / 0.95f, "shade 0.5: the old mix's average");
        Color shaded = LaunchFxEmitter.ShadeTint(tint, 0.5f, 1f);
        Near(shaded.a, 0.4f, "shade keeps alpha");
        Near(shaded.g / shaded.r, 0.9f, "shade keeps the hue");
        Check(LaunchFxEmitter.ShadeTint(tint, 0.5f, 1f).r > LaunchFxEmitter.ShadeTint(tint, 0.5f, -1f).r, "jitter varies the shade");

        // Ground cloud (§0.6): lobes of unequal reach plus a dense core, not an even ring.
        Rand.PushState(1234);
        try
        {
            var sectors = new int[12];
            int core = 0, total = 2000;
            LaunchFxEmitter.GroundLobe[] lobes = LaunchFxEmitter.PlanLobes(180f);
            Check(lobes.Length >= 3 && lobes.Length <= 5, $"3-5 ground lobes ({lobes.Length})");
            for (int i = 0; i < total; i++)
            {
                LaunchFxEmitter.GroundPlacement g = LaunchFxEmitter.GroundCloudPlacement(lobes);
                if (g.speedFactor <= 0.3f && g.sizeFactor >= 1.1f) { core++; continue; }
                sectors[Mathf.FloorToInt(Mathf.Repeat(g.heading, 360f) / 30f) % 12]++;
            }
            int outer = total - core, max = 0, empty = 0;
            foreach (int c in sectors) { max = Mathf.Max(max, c); if (c < outer / 12 / 4) { empty++; } }
            Check(max > 2.5f * outer / 12f, $"dust bunches into lobes (busiest 30° sector {max} vs even {outer / 12})");
            Check(empty >= 3, $"some directions get almost no dust ({empty} sparse sectors)");
            Check(core > total * 0.2f && core < total * 0.4f, $"about 30 % of the dust stays in the core ({core}/{total})");
        }
        finally { Rand.PopState(); }
    }

    // Departure turn (2026-10-26): rate-capped, ramps up, eases out, never overshoots; bigger turns take longer.
    static void DepartureTurn()
    {
        var f = new MissileFlightExtension();
        int Ticks(float from, float to, out float maxStep, out bool overshoot)
        {
            float a = from; maxStep = 0f; overshoot = false;
            for (int t = 1; t < 1000; t++)
            {
                float next = MissileDepartureKinematics.TurnStep(f, a, to, t);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(Mathf.DeltaAngle(a, next)));
                if (Mathf.Sign(Mathf.DeltaAngle(a, to)) != Mathf.Sign(Mathf.DeltaAngle(next, to)) && Mathf.Abs(Mathf.DeltaAngle(next, to)) > 1e-4f) { overshoot = true; }
                a = next;
                if (Mathf.Abs(Mathf.DeltaAngle(a, to)) < 1e-4f) { return t; }
            }
            return -1;
        }
        int small = Ticks(0f, 20f, out float s1, out bool o1), right = Ticks(0f, 90f, out float s2, out bool o2), wide = Ticks(0f, -150f, out float s3, out bool o3);
        Check(small > 0 && right > 0 && wide > 0, $"turns finish (20°: {small}, 90°: {right}, 150°: {wide} ticks)");
        Check(small < right && right < wide, "bigger turns take longer");
        Check(Mathf.Max(s1, Mathf.Max(s2, s3)) <= f.maxTurnRate + 1e-4f, $"turn rate never exceeds maxTurnRate ({Mathf.Max(s2, s3):0.##}°/tick)");
        Check(!o1 && !o2 && !o3, "turn never overshoots the heading");
        Near(MissileDepartureKinematics.TurnStep(f, 0f, 90f, 1), f.maxTurnRate / f.turnRampTicks, "turn rate ramps up from zero");
        Check(MissileDepartureKinematics.TurnStep(f, 350f, 10f, 100) > 350f, "turns the short way across north");
        float fast = MissileDepartureKinematics.TurnStep(f, 0f, 180f, 100, 2f);
        Check(fast > f.maxTurnRate && 2f / (fast * Mathf.Deg2Rad) <= f.maxTurnRadius + 0.01f, $"fast missiles turn quicker so the radius stays within maxTurnRadius ({fast:0.##}°/tick at 2 cells/tick)");
        var map = new IntVec3(250, 1, 250);
        Near(MissileDepartureKinematics.TurnRadiusAt(f, new Vector3(17f, 0f, 125f), 270f, map), 8.5f, "turn radius shrinks to half the room ahead (17 cells to the west edge)");
        Near(MissileDepartureKinematics.TurnRadiusAt(f, new Vector3(125f, 0f, 125f), 0f, map), f.maxTurnRadius, "open map: maxTurnRadius");
        Check(MissileDepartureKinematics.InwardSide(new Vector3(37f, 0f, 21f), 180f, map) == -1, "near the south-west corner heading south, the U-turn swings east (toward the centre)");
        Check(MissileDepartureKinematics.TurnStep(f, 180f, 0f, 100, 0f, -1f, -1) < 180f, "a U-turn follows the preferred side");
        var st = new MissileCruiseState { motion0 = 0f, motion = 45f, heading = 90f };
        Near(MissileDepartureKinematics.TurnBlend(st), 0.5f, "turn blend = share of the turn done");
    }

    // docs/導彈發射特效_重製計畫.md §4.1, §7: thrust timeline and smoke budget pressure.
    static void LaunchFxTimeline()
    {
        var solid = new LaunchFxProfileDef { rampTicks = 4 };
        Near(LaunchFxEmitter.ThrustAt(solid, -1, 0, 0.1f), 0f, "no thrust before ignition");
        Near(LaunchFxEmitter.ThrustAt(solid, -1, 2, 0.1f), 0.5f, "thrust ramps up over rampTicks");
        Near(LaunchFxEmitter.ThrustAt(solid, -1, 200, 0.5f), 1f, "full thrust while burning");
        var cruise = new LaunchFxProfileDef { rampTicks = 4, boosterTicks = 45, sustainThrust = 0.12f };
        Near(LaunchFxEmitter.ThrustAt(cruise, -1, 45, 0.2f), 1f, "booster at full thrust until burnout");
        Near(LaunchFxEmitter.ThrustAt(cruise, -1, 60, 0.3f), 0.12f, "after burnout thrust settles at the sustainer", 0.001f);
        Near(LaunchFxEmitter.ThrustAt(solid, 30, 30, 0.1f), 1f, "a burn-limited motor burns to its burn duration");
        Near(LaunchFxEmitter.ThrustAt(solid, 30, 38, 0.1f), 0f, "and is out 8 ticks later", 0.001f);
        Near(LaunchFxEmitter.ThrustAt(solid, 0, 10, 0.1f), 0f, "burnDurationTicks 0 disables the flame");
        var curved = new LaunchFxProfileDef { rampTicks = 1, thrustCurve = new SimpleCurve { new CurvePoint(0f, 1f), new CurvePoint(1f, 0.5f) } };
        Near(LaunchFxEmitter.ThrustAt(curved, -1, 10, 1f), 0.5f, "thrust curve scales by launch progress");

        int cap = LaunchFxBudget.SmokeCap;
        Check(LaunchFxBudget.Pressure(0) == 0 && LaunchFxBudget.Pressure((int)(cap * 0.75f)) == 1 && LaunchFxBudget.Pressure(cap) == 2,
            "smoke budget pressure: normal, over 70 %, at the cap");
        Check(LaunchFxBudget.BurstCount(16, cap) < LaunchFxBudget.BurstCount(16, 0), "bursts shrink at the smoke cap");
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

    static void InertialDistribution()
    {
        Rand.PushState(19871022);
        try
        {
            foreach (IntVec3 size in new[] { new IntVec3(301, 1, 301), new IntVec3(41, 1, 201) })
            {
                int cx = size.x / 2, cz = size.z / 2;
                int radius = Math.Min(Math.Min(cx, size.x - 1 - cx), Math.Min(cz, size.z - 1 - cz));
                double sx = 0, sz = 0, square = 0;
                int samples = 12000, inner = 0;
                for (int i = 0; i < samples; i++)
                {
                    IntVec3 cell = GuidanceType.InertialTarget(size);
                    int x = cell.x - cx, z = cell.z - cz, r2 = x * x + z * z;
                    if (cell.x < 0 || cell.x >= size.x || cell.z < 0 || cell.z >= size.z || r2 > radius * radius)
                        throw new Exception("inertial sample escaped the map's inscribed circle");
                    sx += x; sz += z; square += r2;
                    if (r2 <= radius * radius / 4f) inner++;
                }
                Check(Math.Abs(sx / samples) < 1.5 && Math.Abs(sz / samples) < 1.5,
                    "inertial distribution is centered on square and rectangular maps");
                double normalizedVariance = square / samples / (radius * radius);
                Check(normalizedVariance > 0.19 && normalizedVariance < 0.24,
                    "inertial variance matches a circularly truncated Gaussian, not uniform scatter");
                Check(inner > samples * 0.65 && inner < samples * 0.72,
                    "inertial samples concentrate near the center");
            }
            Check(GuidanceType.InertialTarget(new IntVec3(1, 1, 1)) == IntVec3.Zero,
                "single-cell map has a valid inertial target");
            Check(!new GuidanceType_Inertial().TerminalGuidance && new GuidanceType_ActiveRadar().TerminalGuidance,
                "active radar provides terminal guidance; inertial does not");
        }
        finally { Rand.PopState(); }
    }

    static void ShahedGuidanceOptions()
    {
        // 彈體的預設零件可能來自其他檔案（共用部件，如 DMSE_Payload_None），所以掃整個 Missile 目錄。
        XElement[] all = Directory.GetFiles("1.7/Defs/Missile", "*.xml")
            .SelectMany(f => XDocument.Load(f).Root.Elements()).ToArray();
        XElement Node(string name) => all.Single(x => (string)x.Element("defName") == name);
        XElement bodyXml = Node("DMSE_MissileBody_Shahed");
        var body = new MissileBodyDef
        {
            defName = (string)bodyXml.Element("defName"),
            sizeClass = Enum.Parse<MissileSizeClass>((string)bodyXml.Element("sizeClass")),
            builtInGuidance = (GuidanceType)Activator.CreateInstance(typeof(GuidanceType).Assembly.GetType(
                (string)bodyXml.Element("builtInGuidance").Attribute("Class"), true)),
            terminalUpdates = (int)bodyXml.Element("terminalUpdates"),
            slots = bodyXml.Element("slots").Elements("li").Select(x => Enum.Parse<MissilePartCategory>(x.Value)).ToList()
        };
        var advanced = new ThingDef { defName = "ComponentSpacer" };
        MissilePartDef Part(string name)
        {
            XElement xml = Node(name);
            var part = new MissilePartDef
            {
                defName = name,
                category = Enum.Parse<MissilePartCategory>((string)xml.Element("category")),
                compatibleBodies = new List<MissileBodyDef> { body }
            };
            if (xml.Element("guidanceType") != null)
            {
                part.guidanceType = (GuidanceType)Activator.CreateInstance(typeof(GuidanceType).Assembly.GetType(
                    (string)xml.Element("guidanceType").Attribute("Class"), true));
            }
            if (xml.Element("costList") != null)
            {
                XElement cost = xml.Element("costList").Elements().Single();
                Check(cost.Name.LocalName == advanced.defName, "Shahed TV upgrade uses an advanced component");
                part.costList = new List<ThingDefCountClass> { new ThingDefCountClass(advanced, (int)cost) };
            }
            return part;
        }
        body.defaultParts = bodyXml.Element("defaultParts").Elements("li").Select(x => Part(x.Value)).ToList();
        MissileConfig cfg = MissileConfig.NewWithDefaults(body);
        Check(cfg.TargetingGuidance is GuidanceType_Inertial && !cfg.PlayerChoosesTarget && !cfg.HasTerminalGuidance,
            "manufactured Shahed starts with inertial guidance, not free TV");
        Check(cfg.StrikeChannelCost == 1, "inertial Shahed occupies one channel");
        var pending = cfg.Clone();
        pending.SetPart(MissilePartCategory.Guidance, Part("DMSE_Shahed_Guidance_TV"));
        var comp = new CompMissileConfig { config = cfg, pending = pending };
        var required = comp.TotalRequired();
        Check(required.Count == 1 && required.TryGetValue(advanced, out int amount) && amount == 1,
            "switching Shahed to TV requires exactly one advanced component");
        Check(pending.PlayerChoosesTarget && pending.StrikeChannelCost == 2,
            "TV Shahed permits manual selection and occupies two channels");
        Check(pending.TerminalUpdates == 0 && !pending.TracksMobileTargets,
            "TV upgrade does not give Shahed moving-base tracking");
        pending.SetPart(MissilePartCategory.Guidance, null);
        Check(pending.TargetingGuidance is GuidanceType_Inertial && pending.StrikeChannelCost == 1,
            "removing the TV module restores inertial targeting and releases the surcharge");
    }

    static void ShortBoosterEffects()
    {
        XElement launch = XDocument.Load("1.7/Defs/Missile/DMSE_MissileLaunchProjectiles.xml").Root.Elements()
            .Single(x => (string)x.Element("defName") == "DMSE_Launch_Shahed");
        XElement effect = launch.Element("comps").Elements("li")
            .Single(x => (string)x.Attribute("Class") == "DMSE.CompProperties_EffecterHotLaunch");
        var props = new CompProperties_EffecterHotLaunch
        {
            burnDurationTicks = (int)effect.Element("burnDurationTicks"),
            effectScale = (float)effect.Element("effectScale")
        };
        Check(props.IsBurning(0, 0) && props.IsBurning(29, 0) && !props.IsBurning(30, 0)
            && !props.IsBurning(300, 0), "short booster ends after 30 ticks, including cruise");
        Check(!props.IsBurning(9, 10) && props.IsBurning(10, 10) && props.IsBurning(39, 10)
            && !props.IsBurning(40, 10), "booster duration starts at ignition, respecting cold-launch delay");
        Near(props.effectScale, 0.35f, "long-range loitering munition effect scale");
        var unchanged = new CompProperties_EffecterHotLaunch();
        Check(unchanged.IsBurning(3000, 0) && unchanged.effectScale == 1f,
            "other missiles retain unlimited exhaust and original effect size");
        props.burnDurationTicks = 0;
        Check(!props.IsBurning(0, 0), "zero-duration booster is disabled");
    }

    // docs/導彈組裝台與裝配方案_實作計畫.md §8.3, §5.2, §6: bench cost equals print-then-refit, rule-string
    // stripping, and blueprint files round-trip by defName.
    static void AssemblyBench()
    {
        static ThingDef Res(string name) => new ThingDef { defName = name, label = name };
        ThingDef steel = Res("Steel"), chem = Res("Chemfuel"), tant = Res("DMSE_TantastenAlloy"),
            graphene = Res("DMSE_Graphene"), spacer = Res("ComponentSpacer"), industrial = Res("ComponentIndustrial");
        var body = new MissileBodyDef
        {
            defName = "Test_MissileBody_Yj", label = "hypersonic body", designWord = "Hypersonic", refundFraction = 0.5f,
            slots = new List<MissilePartCategory> { MissilePartCategory.Warhead, MissilePartCategory.Guidance,
                MissilePartCategory.Propulsion, MissilePartCategory.Payload }
        };
        MissilePartDef Part(string name, MissilePartCategory cat, string word, bool omit, params ThingDefCountClass[] cost)
            => new MissilePartDef
            {
                defName = name, label = name, category = cat, designWord = word, omitFromDesignName = omit,
                compatibleBodies = new List<MissileBodyDef> { body }, costList = cost.ToList()
            };
        MissilePartDef he = Part("Test_Warhead_HE", MissilePartCategory.Warhead, "HE", false,
            new ThingDefCountClass(steel, 30), new ThingDefCountClass(chem, 20));
        MissilePartDef ap = Part("Test_Warhead_AP", MissilePartCategory.Warhead, "AP", false,
            new ThingDefCountClass(steel, 40), new ThingDefCountClass(tant, 5));
        MissilePartDef antiShip = Part("Test_Guidance_AntiShip", MissilePartCategory.Guidance, "Anti-ship", false,
            new ThingDefCountClass(industrial, 5), new ThingDefCountClass(spacer, 1));
        MissilePartDef standard = Part("Test_Propulsion_Standard", MissilePartCategory.Propulsion, null, true);
        MissilePartDef booster = Part("Test_Propulsion_Booster", MissilePartCategory.Propulsion, "Boost", false,
            new ThingDefCountClass(chem, 40), new ThingDefCountClass(steel, 20));
        MissilePartDef noPayload = Part("Test_Payload_None", MissilePartCategory.Payload, null, true);
        body.defaultParts = new List<MissilePartDef> { he, antiShip, standard, noPayload };
        var item = new ThingDef
        {
            defName = "Test_Missile_Yj", label = "test missile", category = ThingCategory.Item,
            costList = new List<ThingDefCountClass> { new ThingDefCountClass(chem, 270), new ThingDefCountClass(tant, 70),
                new ThingDefCountClass(graphene, 90), new ThingDefCountClass(spacer, 6) },
            comps = new List<CompProperties> { new CompProperties_MissileConfig { defaultBody = body } }
        };
        DefDatabase<ThingDef>.Add(item);
        DefDatabase<MissileBodyDef>.Add(body);
        foreach (MissilePartDef p in new[] { he, ap, antiShip, standard, booster, noPayload }) { DefDatabase<MissilePartDef>.Add(p); }
        // 反查快取是靜態的：清掉，讓它看到剛加入的測試 Def。
        typeof(MissileAssemblyUtility).GetField("cachedDefs", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        typeof(MissileItemUtility).GetField("itemByBody", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        Check(MissileItemUtility.ItemDefFor(body) == item, "bench finds the item that declares the body");

        MissileConfig defaults = MissileConfig.NewWithDefaults(body);
        Dictionary<ThingDef, int> plain = MissileAssemblyCost.RequiredFor(defaults);
        Check(plain.Count == 4 && plain[chem] == 270 && plain[tant] == 70 && plain[graphene] == 90 && plain[spacer] == 6,
            "default design costs exactly the item recipe");

        MissileConfig target = defaults.Clone();
        target.SetPart(MissilePartCategory.Warhead, ap);
        target.SetPart(MissilePartCategory.Propulsion, booster);
        Dictionary<ThingDef, int> cost = MissileAssemblyCost.RequiredFor(target);
        Check(cost.Count == 5 && cost[chem] == 300 && cost[tant] == 75 && cost[graphene] == 90 && cost[spacer] == 6 && cost[steel] == 45,
            "AP + booster Yj matches the plan's worked example");

        // 與「列印預設彈 → 改裝」逐材料相等
        var refit = new CompMissileConfig { config = defaults.Clone(), pending = target.Clone() };
        var twoStep = new Dictionary<ThingDef, int>();
        void Add(ThingDef d, int n) { twoStep.TryGetValue(d, out int cur); twoStep[d] = cur + n; }
        foreach (ThingDefCountClass c in item.costList) { Add(c.thingDef, c.count); }
        foreach (KeyValuePair<ThingDef, int> kv in refit.TotalRequired()) { Add(kv.Key, kv.Value); }
        foreach (MissilePartDef removed in refit.RemovedParts())
        {
            foreach (ThingDefCountClass c in removed.costList) { Add(c.thingDef, -Mathf.FloorToInt(c.count * body.refundFraction)); }
        }
        Check(twoStep.Where(kv => kv.Value > 0).All(kv => cost.TryGetValue(kv.Key, out int v) && v == kv.Value)
              && cost.All(kv => twoStep.TryGetValue(kv.Key, out int v) && v == kv.Value),
            "bench cost equals print-then-refit, material by material");

        MissileConfig stripped = defaults.Clone();
        stripped.SetPart(MissilePartCategory.Warhead, null);
        Dictionary<ThingDef, int> noWarhead = MissileAssemblyCost.RequiredFor(stripped);
        Check(noWarhead[chem] == 260 && !noWarhead.ContainsKey(steel), "removed default part refunds half, clamped at zero");

        // 規則字串：本組合沒有的符號剔除，空白與分隔符壓縮
        Check(MissileDesignNamer.Strip(defaults, "[warhead] [payload] [body]") == "[warhead] [body]", "omitted payload symbol is stripped");
        Check(MissileDesignNamer.Strip(stripped, "[body]·[warhead][payload]") == "[body]", "dangling separator is trimmed");
        Check(MissileDesignNamer.Strip(target, "[body] [warhead] [propulsion]") == "[body] [warhead] [propulsion]", "present symbols are kept");
        Check(MissileDesignNamer.Strip(defaults, null) == "[body]", "missing template falls back to the body");
        Dictionary<string, string> symbols = MissileDesignNamer.BuildSymbols(target);
        Check(symbols["body"] == "Hypersonic" && symbols["warhead"] == "AP" && symbols["propulsion"] == "Boost"
              && !symbols.ContainsKey("payload"), "symbols use design words and skip omitted parts");
        Check(MissileDesignNamer.IsValidName("YJ AP-Boost·2") && !MissileDesignNamer.IsValidName("bad/name")
              && !MissileDesignNamer.IsValidName(new string('x', 41)), "name validation follows the xenotype editor rules");

        // 各彈體專屬 Namer（docs/導彈組裝台與裝配方案_實作計畫.md §5.6）
        // GrammarResolver 一定會併入 RulePackDefOf.GlobalUtility；測試環境的 DefDatabase 是空的，先補一個。
        var globalUtility = new RulePackDef { defName = "GlobalUtility" };
        Field(globalUtility, "rulePack", new RulePack());
        if (DefDatabase<RulePackDef>.GetNamedSilentFail("GlobalUtility") == null) { DefDatabase<RulePackDef>.Add(globalUtility); }
        // DefOf 的快取可能早就初始化過（值為 null），直接補上。
        typeof(RulePackDefOf).GetField("GlobalUtility", BindingFlags.Public | BindingFlags.Static).SetValue(null, globalUtility);
        var namerPack = new RulePackDef { defName = "Test_NamerPack" };
        var namerRules = new RulePack();
        Field(namerRules, "rulesStrings", new List<string>
        {
            "r_name(p=3)->[designator]-[num] [warhead] [guidance]",
            "r_name(p=2)->[designator]-[num][suffix] [warhead]",
            "r_name(p=1)->[body] [designator]-[num]"
        });
        Field(namerPack, "rulePack", namerRules);
        var namer = new MissileNamerDef
        {
            defName = "Test_Namer", pack = namerPack, designator = "YJ", numberMin = 1, numberMax = 99
        };
        body.namer = namer;
        Check(MissileDesignNamer.NamerFor(target) == namer && MissileDesignNamer.PackFor(target) == namerPack,
            "the body's namer supplies the design-name pack");
        Check(MissileDesignNamer.BuildSymbols(target)["designator"] == "YJ",
            "[designator] is injected from the namer's design word");
        // 代字保持符號（換語言時重新翻譯），只有編號／後綴在抽樣板時烘成文字。
        Check(System.Text.RegularExpressions.Regex.IsMatch(MissileDesignNamer.Bake(namer, "[designator]-[num]"), @"^\[designator\]-[1-9][0-9]?$"),
            "the number is baked into the rule string");
        string bakedRule = MissileDesignNamer.BuildRuleString(target, namerPack);
        Check(!bakedRule.Contains("[num]") && !bakedRule.Contains("[suffix]")
              && System.Text.RegularExpressions.Regex.IsMatch(bakedRule, @"\[designator\]-[1-9][0-9]?"),
            $"the picked template carries a concrete number ({bakedRule})");
        string named = MissileDesignNamer.Generate(target, null, new HashSet<string>(), out string nameRule);
        Check(!named.NullOrEmpty() && named.Contains("YJ-") && !nameRule.Contains("[num]") && MissileDesignNamer.IsValidName(named),
            $"per-body design name ({named} from {nameRule})");
        string renamed = MissileDesignNamer.Generate(target, null, new HashSet<string> { named }, out _);
        Check(renamed != named, $"a colliding designation draws a new number ({named} / {renamed})");
        var padded = new MissileNamerDef
        {
            defName = "Test_Namer_Padded", pack = namerPack, designator = "DF", numberMin = 7, numberMax = 7,
            numberDigits = 2, suffixes = new List<string> { "D" }
        };
        Check(MissileDesignNamer.Bake(padded, "[designator]-[num][suffix]") == "[designator]-07D",
            "numberDigits pads the number and the suffix follows it");
        Check(MissileDesignNamer.Candidates(target, new HashSet<string>()).Count == 3,
            "every template yields one candidate name");
        body.namer = null;
        Check(!MissileDesignNamer.BuildSymbols(target).ContainsKey("designator")
              && MissileDesignNamer.Strip(target, "[designator] [warhead]") == "[warhead]",
            "a body without a namer drops the designator and its separators");

        // 方案檔：以 defName 存讀；缺 Def 時停用並說明原因
        string dir = Path.Combine(Path.GetTempPath(), "dmse-bench-test");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "design.xml");
        MissileDesignBlueprint bp = MissileDesignBlueprint.From(target, "YJ AP", true, "[body] [warhead]");
        bp.ToXml().Save(file);
        MissileDesignBlueprint loaded = MissileDesignBlueprint.FromXml(file);
        MissileConfig back = loaded.ToConfig();
        Check(loaded.Usable && loaded.name == "YJ AP" && loaded.nameLocked && loaded.ruleString == "[body] [warhead]"
              && back.body == body && back.parts.Count == 4 && back.PartFor(MissilePartCategory.Warhead) == ap,
            "blueprint round-trips through its XML file");
        XDocument broken = XDocument.Load(file);
        broken.Root.Element("partDefNames").Add(new XElement("li", "Test_Removed_Part"));
        broken.Save(file);
        MissileDesignBlueprint missing = MissileDesignBlueprint.FromXml(file);
        Check(!missing.Usable && missing.unusableReason.Contains("Test_Removed_Part"), "blueprint with a removed def is disabled with a reason");
        File.Delete(file);
    }

    public static void Main()
    {
        Setup();
        AirDefenseAlert();
        InertialDistribution();
        ShahedGuidanceOptions();
        ShortBoosterEffects();
        CiwsAimDirections();
        InterceptDebrisGeometry();
        FxSmokeMotion();
        LaunchFxTimeline();
        DepartureTurn();
        MissileMapEntry();
        Detection(); Solver(); FireControl(); LedgerAndTerminal(); InventoryAndPersistence(); StrikeP0(); StrikeP1(); StrikeP2(); StrikeP3(); StrikeP4();
        MissileContent(); AreaDefense();
        AssemblyBench();
        Console.WriteLine($"PASS: {count} BVR checks (real game/DMSE assemblies).");
    }
    class ManualLauncher : CompMissileLauncher_WorldTargeting
    {
        public bool online = true, ammo = true;
        public MissileConfig config;
        public override bool Active => online;
        public override bool HasAmmo => ammo;
        protected override int LaunchCooldownTicks => 60;
        protected override string LoadedStatusText => "loaded";
        protected override ThingDef LaunchSkyfallerDef => parent.def;
        protected override ThingDef IncomingSkyfallerDef => parent.def;
        protected override WorldObjectDef TravelWorldObjectDef => new WorldObjectDef();
        public override IEnumerable<Thing> StrikeRounds => ammo ? new[] { parent } : Enumerable.Empty<Thing>();
        public override MissileConfig BuildFiringConfig(Thing round) => config;
        protected override void ConsumeAmmo(Thing round) { ammo = false; }
        protected override string GizmoLabel => "launch";
        protected override string GizmoDesc => "launch";
        protected override string NoAmmoDisableReason => "no ammo";
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
