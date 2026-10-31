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


static partial class Test
{
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

        // Only guidance that needs continuous outside support books channels (docs/導彈與雷達科技重整_執行計畫.md §4).
        MissileConfig Cfg(MissileSizeClass size, GuidanceType guidance = null, GuidanceType builtIn = null)
        {
            var cfg = new MissileConfig(new MissileBodyDef { sizeClass = size, builtInGuidance = builtIn });
            if (guidance != null) { cfg.parts.Add(new MissilePartDef { category = MissilePartCategory.Guidance, guidanceType = guidance }); }
            return cfg;
        }
        Check(Cfg(MissileSizeClass.Medium).StrikeChannelCost == 0 && Cfg(MissileSizeClass.Ballistic).StrikeChannelCost == 0,
            "no seeker: inertial missiles book no guidance channel");
        Check(Cfg(MissileSizeClass.Medium, new GuidanceType_TV()).StrikeChannelCost == 0
            && Cfg(MissileSizeClass.Ballistic, new GuidanceType_Laser()).StrikeChannelCost == 0,
            "TV and laser guidance book no guidance channel");
        Check(Cfg(MissileSizeClass.Medium, new GuidanceType_FireControl()).StrikeChannelCost == 1
            && Cfg(MissileSizeClass.Ballistic, new GuidanceType_FireControl()).StrikeChannelCost == 2,
            "fire-control guidance books one channel, two for a ballistic body");
        Check(Cfg(MissileSizeClass.Medium, null, new GuidanceType_RadioCommand()).StrikeChannelCost == 1,
            "built-in radio command books a channel");
        Check(Cfg(MissileSizeClass.Medium, new GuidanceType_Laser(), new GuidanceType_RadioCommand()).StrikeChannelCost == 0,
            "an installed autonomous seeker overrides built-in radio command");
        var ballistic = Cfg(MissileSizeClass.Ballistic, new GuidanceType_FireControl());
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
        Check(manual.SingleLaunchDisabledReason() == null, "single fire-control ballistic shot is enabled with three free guidance channels");
        planner.AddGuidanceHold(side, 102000, 1);
        planner.AddGuidanceHold(side, 102000, 1);
        string unavailableChannels = manual.SingleLaunchDisabledReason();
        Check(unavailableChannels != null, "single fire-control ballistic shot is disabled when only one channel is free");
        Check(manual.TryLaunchRound(GlobalTargetInfo.Invalid, launchParent, false, out string channelFailure) == null
            && channelFailure == unavailableChannels && manual.ammo,
            "actual launch rechecks channels after target selection and leaves ammo untouched on failure");
        manual.config = Cfg(MissileSizeClass.Ballistic);
        Check(manual.SingleLaunchDisabledReason() == null, "an inertial ballistic shot needs no free channel");
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
        Check(manual.SingleLaunchDisabledReason() == null, "an inertial shot needs no fire-control radar");
        manual.config = ballistic;
        Check(manual.SingleLaunchDisabledReason() != null, "a fire-control shot is disabled without an active strike-capable fire-control radar");
        Current.Game.Maps.Remove(map);
        Field(launchParent, "mapIndexOrState", (sbyte)-2);

        var holdSave = new StrikeGuidanceHold { channelCost = 3, releaseTick = 103000 };
        var queueSave = new StrikeQueuedLaunch { channelCost = 2, fireTick = 102030 };
        var channelXml = SaveXml("Channels", () => { Scribe_Deep.Look(ref holdSave, "hold"); Scribe_Deep.Look(ref queueSave, "queued"); });
        Check(channelXml.Descendants("channelCost").Select(e => e.Value).SequenceEqual(new[] { "3", "2" }),
            "real Scribe save output preserves weighted guidance and queue costs");

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
        var saved = SaveXml("Strike", mission.ExposeData);
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
    // An externally guided missile (one channel); autonomous seekers book none (channelCost 0).
    static StrikePlanSlot PlanSlot(object launcher, int flight, int salvo = 30, int cooldown = 450, int ready = 0, int channels = 1)
        => new StrikePlanSlot { launcher = launcher, flightTicks = flight, salvoInterval = salvo, launchCooldown = cooldown, readyTick = ready, channelCost = channels };
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
        var autonomous = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>>
        {
            Enumerable.Range(0, 5).Select(_ => PlanSlot(new object(), 1000, channels: 0)).ToList(),
        }, 2, 600, 60, 0, new[] { 500, 300 });
        Check(!autonomous[0].overCapacity && autonomous[0].fireTick == 0, "zero-channel missiles ignore the channel pool and held channels");
        var heavy = PlanSlot(new object(), 1000); heavy.channelCost = 3;
        var weightedTimeline = StrikeTimeline.Simulate(new List<List<StrikePlanSlot>>
        {
            new List<StrikePlanSlot> { heavy }, new List<StrikePlanSlot> { heavy }
        }, 4, 600, 60, 0, new int[0]);
        Check(weightedTimeline[0].fireTick == 0 && weightedTimeline[1].fireTick == 601,
            "a three-channel launch holds all three until guidance ends");
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
}
