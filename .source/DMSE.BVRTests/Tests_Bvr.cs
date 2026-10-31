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
        var saved = SaveXml("BVRTest", original.ExposeData);
        Check(saved.Element("firstDetectedTick")?.Value == "1500" && saved.Element("interceptAttempts")?.Value == "3"
            && saved.Element("playerNotified")?.Value == "True" && saved.Element("lockUntil")?.Value == "2100",
            "real Scribe saving retains detection/attempt/lock values");
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
}
