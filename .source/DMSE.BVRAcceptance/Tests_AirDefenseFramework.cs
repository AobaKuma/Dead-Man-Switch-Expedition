using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>A world object that is not a missile and knows nothing of DMSE: the shape a third-party aircraft mod would ship.</summary>
    public class TestAircraft : WorldObject, IAirTrack
    {
        public PlanetTile from = PlanetTile.Invalid, to = PlanetTile.Invalid;
        public MapParent target;
        public int launchTick, flightTicks = 6000, hits;
        public int HitsToKill = 2;

        public WorldObject Self => this;
        public Faction Attacker => Faction;
        public PlanetTile Origin => from;
        public PlanetTile Destination => to;
        public MapParent TargetParent => target;
        public AirTrackSignature Signature => new AirTrackSignature { distanceLevel = 2f, stealthLevel = 0, speed = 15f, altitude = 5000f };
        private float Angle => GenMath.SphericalDistance(Find.WorldGrid.GetTileCenter(from).normalized, Find.WorldGrid.GetTileCenter(to).normalized);
        public float AngularSpeed => Angle / Mathf.Max(1, flightTicks);
        public int TicksToArrival => Mathf.Max(0, launchTick + flightTicks - Find.TickManager.TicksGame);
        public int RouteRevision => 0;
        public Vector3 PositionAt(int ticksAhead) => Vector3.Slerp(Find.WorldGrid.GetTileCenter(from), Find.WorldGrid.GetTileCenter(to),
            Mathf.Clamp01((Find.TickManager.TicksGame - launchTick + ticksAhead) / (float)Mathf.Max(1, flightTicks)));
        public override Vector3 DrawPos => PositionAt(0);

        public void Notify_Intercepted(Faction by)
        {
            hits++;
            if (hits >= HitsToKill) { Destroy(); }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref from, "from", PlanetTile.Invalid);
            Scribe_Values.Look(ref to, "to", PlanetTile.Invalid);
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref launchTick, "launchTick");
            Scribe_Values.Look(ref flightTicks, "flightTicks", 6000);
            Scribe_Values.Look(ref hits, "hits");
        }
    }

    /// <summary>A world object the air-defense code cannot reach directly; a provider wraps it, as for a vanilla or third-party object.</summary>
    public class TestPlainFlyer : TestAircraftBody { }

    public class TestAircraftBody : WorldObject { }

    public class TestFlyerWrapper : IAirTrack
    {
        public WorldObject flyer;
        public PlanetTile to;
        public int hits;
        public WorldObject Self => flyer;
        public Faction Attacker => flyer.Faction;
        public PlanetTile Origin => flyer.Tile;
        public PlanetTile Destination => to;
        public AirTrackSignature Signature => new AirTrackSignature { distanceLevel = 2f, speed = 15f, altitude = 5000f };
        public float AngularSpeed => 0.00001f;
        public int TicksToArrival => 3000;
        public Vector3 PositionAt(int ticksAhead) => Find.WorldGrid.GetTileCenter(to);
        public void Notify_Intercepted(Faction by) => hits++;
    }

    public class TestFlyerProvider : AirTrackProvider
    {
        public TestFlyerWrapper wrapper;
        public override void CollectTracks(List<IAirTrack> into)
        {
            if (wrapper != null) { into.Add(wrapper); }
        }
    }

    public class TestXmlListener : AirCombatListener
    {
        public int note;
        public override void Notify(AirCombatEvent e) { }
    }

    /// <summary>Air defense framework (.source/DMSE/MissileFramework/AirDefenseFramework.cs) on the real scan, combat and save paths.</summary>
    public static class Tests_AirDefenseFramework
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("AD1", "World layer: modifiers change detection, lock time and hit chance, and the full event sequence fires", WorldPipeline),
            new AcceptanceTest("AD2", "Map layer: the same modifiers and events apply, with the missile's own behavior hook", MapPipeline),
            new AcceptanceTest("AD3", "A non-missile world object is detected, engaged and damaged, and its state survives save and load", ExternalAircraft),
            new AcceptanceTest("AD4", "A provider turns an object it does not own into a track; dropping it discards the state", ProvidedTrack),
            new AcceptanceTest("AD5", "The drop-pod ghost is a track without being a missile", PodGhost),
            new AcceptanceTest("AD6", "Extensions and behaviors load from XML", XmlLoading),
        };

        private static WorldComponent_BVRNetwork Network => WorldComponent_BVRNetwork.Current;

        private static AirDefenseModifier_Factor Fixed(AirDefenseStat stat, float value, bool worldOnly = false, bool mapOnly = false)
            => new AirDefenseModifier_Factor { stat = stat, factor = 0f, offset = value, worldOnly = worldOnly, mapOnly = mapOnly };

        public static IEnumerable<Wait> WorldPipeline(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            yield return new WaitTicks(20);
            List<AirCombatEvent> seen = new List<AirCombatEvent>();
            Action<AirCombatEvent> record = seen.Add;
            AirDefenseModifier_Factor blind = Fixed(AirDefenseStat.Detection, 0f);
            AirDefenseModifier_Factor quickLock = Fixed(AirDefenseStat.LockTicks, 77f);
            AirDefenseModifier_Factor sureHit = Fixed(AirDefenseStat.HitChance, 1f);
            AirCombatEvents.Raised += record;
            AirDefensePipeline.Add(blind);
            try
            {
                // Blind: the base never sees the missile, so nothing else can happen.
                ScorerProjectile_WorldObject hidden = LaunchPlayerMissile(site.Tile, site, CruiseConfig());
                BVRNode node = Network.NodeOf(site);
                t.Require(node != null, "the base is a node");
                float raw = 0f;
                AirDefensePipeline.Remove(blind);
                raw = node.DetectionRange(hidden.TargetProps, hidden);
                AirDefensePipeline.Add(blind);
                t.Check(raw > 0f && node.DetectionRange(hidden.TargetProps, hidden) == 0f, $"the modifier takes the node's range from {raw:0.#} to 0");
                var waitBlind = new WaitTicks(hidden.TicksToArrival / 2);
                yield return waitBlind;
                t.Check(!hidden.Destroyed && !hidden.State.FactionSees(site.Faction, int.MinValue) && hidden.State.interceptor == null,
                    "a blinded node neither sees nor engages the missile");
                t.Check(!seen.Any(e => e.Track == hidden), "nothing is reported for an unseen missile");
                hidden.Destroy();
                AirDefensePipeline.Remove(blind);
                seen.Clear();

                // Normal sight: the whole sequence, with lock time and hit chance from the chain.
                AirDefensePipeline.Add(quickLock);
                AirDefensePipeline.Add(sureHit);
                ScorerProjectile_WorldObject wo = LaunchPlayerMissile(site.Tile, site, CruiseConfig());
                int lockCost = -1;
                Action<AirCombatEvent> readLock = e =>
                {
                    if (e.Kind == AirCombatEventKind.LockStarted && e.Track == wo) { lockCost = wo.State.lockUntil - Find.TickManager.TicksGame; }
                };
                AirCombatEvents.Raised += readLock;
                var waitShot = new WaitUntil(() => wo.Destroyed || wo.State.interceptor != null, 3000);
                yield return waitShot;
                AirCombatEvents.Raised -= readLock;
                t.Require(!wo.Destroyed && wo.State.interceptor != null, "the base launches an interceptor");
                t.Check(lockCost == 77, $"the lock takes the modified 77 ticks (saw {lockCost})");
                t.Check(wo.State.interceptor.hitChance == 1f, $"the interceptor carries the modified hit chance ({wo.State.interceptor.hitChance})");
                var waitResolve = new WaitUntil(() => wo.Destroyed, wo.State.interceptor.interceptTick - Find.TickManager.TicksGame + 30);
                yield return waitResolve;
                t.Check(wo.Destroyed, "the certain hit destroys the missile");

                // Other factions that see the missile report their own first sight; only the base's events count here.
                List<AirCombatEventKind> kinds = seen.Where(e => e.Track == wo && e.Defender == site.Faction).Select(e => e.Kind).ToList();
                t.Note("events: " + string.Join(", ", kinds));
                t.Check(kinds.SequenceEqual(new[] { AirCombatEventKind.TrackDetected, AirCombatEventKind.LockStarted,
                    AirCombatEventKind.InterceptorLaunched, AirCombatEventKind.InterceptResolved }), "detected → lock → launch → resolved, once each");
                AirCombatEvent resolved = seen.Last(e => e.Track == wo);
                t.Check(resolved.Outcome == AirInterceptOutcome.Hit && resolved.Layer == AirCombatLayer.World && resolved.Defender == site.Faction
                    && resolved.Attacker == Player && resolved.Site == site && resolved.HitChance == 1f, "the result names the layer, sides, site and chance");
                t.Check(seen.First(e => e.Track == wo && e.Kind == AirCombatEventKind.InterceptorLaunched).InterceptorLoadout != null,
                    "the launch names the interceptor's loadout");
            }
            finally
            {
                AirCombatEvents.Raised -= record;
                AirDefensePipeline.Remove(blind);
                AirDefensePipeline.Remove(quickLock);
                AirDefensePipeline.Remove(sureHit);
            }
        }

        private class HalfAsTarget : MissileBehaviorProps
        {
            public override void ModifyDefenseAsTarget(AirDefenseQuery query, ref float value)
            {
                if (query.Stat == AirDefenseStat.HitChance) { value *= 0.5f; }
            }
        }

        public static IEnumerable<Wait> MapPipeline(TestContext t)
        {
            SpawnBuilding(PhaseArray, 10, 10);
            SpawnLoadedLauncher(14, 10, 4);
            yield return WaitPowered();
            yield return new WaitTicks(2);
            MapComponent_BVRCombat bvr = HomeBVR;
            Thing sample = MissileProjectileMaker.MakeIncoming(CruiseIncoming);
            int window = bvr.ComputeWarningWindow(sample, 1, Player);
            t.Require(window > 0, $"a radar gives a warning window ({window})");
            AirDefenseModifier_Factor wide = new AirDefenseModifier_Factor { stat = AirDefenseStat.Detection, factor = 2f, mapOnly = true };
            AirDefenseModifier_Factor worldOnly = new AirDefenseModifier_Factor { stat = AirDefenseStat.Detection, factor = 10f, worldOnly = true };
            AirDefenseModifier_Factor quickLock = Fixed(AirDefenseStat.LockTicks, 5f);
            AirDefenseModifier_Factor sureHit = Fixed(AirDefenseStat.HitChance, 1f);
            List<AirCombatEvent> seen = new List<AirCombatEvent>();
            Action<AirCombatEvent> record = seen.Add;
            HalfAsTarget half = new HalfAsTarget();
            MissileBodyDef body = CruiseBody;
            AirDefensePipeline.Add(wide);
            AirDefensePipeline.Add(worldOnly);
            AirCombatEvents.Raised += record;
            try
            {
                t.Check(Mathf.Abs(bvr.ComputeWarningWindow(sample, 1, Player) - window * 2) <= 1, "the map window uses the chain; a world-only modifier stays out");
                AirDefensePipeline.Remove(wide);
                AirDefensePipeline.Remove(worldOnly);
                AirDefensePipeline.Add(quickLock);
                AirDefensePipeline.Add(sureHit);
                body.behaviors = new List<MissileBehaviorProps> { half };
                t.Require(bvr.RegisterSalvo(CruiseIncoming, 1, CruiseConfig(), Enemy, Player), "a salvo is registered");
                BVRWave wave = bvr.Waves[bvr.Waves.Count - 1];
                BVRTarget target = wave.targets[0];
                Thing payload = target.Payload;
                t.Check(seen.Count(e => e.Kind == AirCombatEventKind.TrackDetected && e.TargetThing == payload) == 1, "registering reports the detection");

                var waitLaunch = new WaitUntil(() => seen.Any(e => e.Kind == AirCombatEventKind.InterceptorLaunched), 1500);
                yield return waitLaunch;
                t.Require(!waitLaunch.TimedOut, "the launcher fires");
                AirCombatEvent locked = seen.FirstOrDefault(e => e.Kind == AirCombatEventKind.LockStarted);
                t.Check(locked != null && locked.Effector?.def == PhaseArray, "the lock names the radar");
                AirCombatEvent launched = seen.First(e => e.Kind == AirCombatEventKind.InterceptorLaunched);
                t.Check(launched.Layer == AirCombatLayer.Map && launched.Map == Home && launched.Defender == Player && launched.Attacker == Enemy,
                    "the launch names the layer, map and sides");
                t.Check(Mathf.Approximately(launched.HitChance, 0.5f), $"global 1.0 then the missile's own ×0.5 gives 0.5 ({launched.HitChance})");

                var waitResult = new WaitUntil(() => seen.Any(e => e.Kind == AirCombatEventKind.InterceptResolved), 1500);
                yield return waitResult;
                t.Check(!waitResult.TimedOut, "the interceptor's result is reported");
                AirCombatEvent resolved = seen.FirstOrDefault(e => e.Kind == AirCombatEventKind.InterceptResolved);
                t.Check(resolved != null && (resolved.Outcome == AirInterceptOutcome.Hit || resolved.Outcome == AirInterceptOutcome.Miss)
                    && resolved.Attacker == Enemy && resolved.Defender == Player, $"the result names an outcome and sides ({resolved?.Outcome})");
                if (resolved?.Outcome == AirInterceptOutcome.Hit) { t.Check(bvr.GetTarget(target.id) == null, "a hit removes the target"); }

                bvr.Waves.Clear();
            }
            finally
            {
                body.behaviors = null;
                AirCombatEvents.Raised -= record;
                AirDefensePipeline.Remove(wide);
                AirDefensePipeline.Remove(worldOnly);
                AirDefensePipeline.Remove(quickLock);
                AirDefensePipeline.Remove(sureHit);
            }
        }

        private static T MakeFlyer<T>(Faction faction, PlanetTile tile) where T : WorldObject, new()
        {
            T flyer = new T { def = DefDatabase<WorldObjectDef>.GetNamed("DMSE_DropPodTrack") };
            flyer.ID = Find.UniqueIDsManager.GetNextWorldObjectID();
            flyer.Tile = tile;
            flyer.SetFaction(faction);
            return flyer;
        }

        public static IEnumerable<Wait> ExternalAircraft(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            yield return new WaitTicks(20);
            AirDefenseModifier_Factor sureHit = Fixed(AirDefenseStat.HitChance, 1f);
            AirDefensePipeline.Add(sureHit);
            try
            {
                TestAircraft aircraft = MakeFlyer<TestAircraft>(Player, Home.Tile);
                aircraft.from = Home.Tile;
                aircraft.to = site.Tile;
                aircraft.target = site;
                aircraft.launchTick = Find.TickManager.TicksGame;
                // Fire control only guides inside maxRangeTicks of arrival (6000 for the base), and two interceptors must fit in the flight.
                aircraft.flightTicks = 5500;
                Find.WorldObjects.Add(aircraft);
                Track(aircraft);
                int id = aircraft.ID;

                IBVRTrack track = Network.TrackOf(aircraft);
                t.Require(track != null && track.Config == null, "the aircraft is a track without a missile config");
                t.Check(ReferenceEquals(track, Network.TrackOf(aircraft)), "the network keeps one track per aircraft");
                t.Check(Network.Tracks.Contains(track), "the network lists it without any registration");
                t.Check(WorldComponent_FactionAirDefenseAlert.TargetSite(track) == site, "its target site comes from the interface");

                var waitShot = new WaitUntil(() => track.State.interceptor != null, 4000);
                yield return waitShot;
                BVRNode node = Network.NodeOf(site);
                t.Note($"eta {track.TicksToArrival}, range {node?.DetectionRange(track.TargetProps, track):0.#}, distance {MissileWorldUtility.SurfaceTilesBetween(MissileWorldUtility.TileCenter(site.Tile), track.PositionAt(0)):0.#}, status {track.State.StatusKey}");
                t.Require(!waitShot.TimedOut, "the base engages the aircraft");
                t.Check(track.State.FactionSees(site.Faction, int.MinValue), "the base has a contact");
                int firstSeen = track.State.ObservationOf(site.Faction).firstSeenTick;
                WorldObject_Interceptor shot = track.State.interceptor;
                t.Check(shot.target == aircraft && shot.hitChance == 1f, "the interceptor aims at the aircraft");
                int interceptorId = shot.ID, due = shot.interceptTick;

                // Save with the interceptor in flight: both the state kept by the network and the reference must return.
                RememberEnemyForReload();
                Game before = Current.Game;
                GameDataSaveLoader.SaveGame(TestSaveName);
                yield return new WaitFrames(10);
                GameDataSaveLoader.LoadGame(TestSaveName);
                var waitReload = new WaitUntil(() => Current.Game != before && Current.ProgramState == ProgramState.Playing, 999999);
                yield return waitReload;
                yield return new WaitFrames(5);

                aircraft = Find.WorldObjects.AllWorldObjects.OfType<TestAircraft>().FirstOrDefault(x => x.ID == id);
                t.Require(aircraft != null, "the aircraft is restored");
                Track(aircraft);
                Track(aircraft.target);
                track = Network.TrackOf(aircraft);
                t.Check(track.State.ObservationOf(aircraft.target.Faction)?.firstSeenTick == firstSeen, "the contact kept by the network is restored");
                t.Check(track.State.interceptor != null && track.State.interceptor.ID == interceptorId, "the in-flight interceptor is restored");
                t.Check(track.State.interceptor?.target == aircraft, "the interceptor's target is restored");

                TestAircraft restored = aircraft;
                var waitHit = new WaitUntil(() => restored.hits >= 1, Mathf.Max(60, due - Find.TickManager.TicksGame + 60));
                yield return waitHit;
                t.Check(restored.hits >= 1 && !restored.Destroyed || restored.hits >= 2, $"the first hit is the aircraft's to handle: it keeps flying (hits {restored.hits})");
                var waitKill = new WaitUntil(() => restored.Destroyed, Mathf.Max(60, restored.TicksToArrival));
                yield return waitKill;
                t.Check(restored.Destroyed && restored.hits == 2, $"a later interceptor finishes it (hits {restored.hits})");
                yield return new WaitTicks(BVRWorldTuningDef.Current.scanIntervalTicks + 1);
                t.Check(Field<Dictionary<int, BVRTrackState>>(Network, "externalStates").Count == 0, "the state of a destroyed aircraft is discarded");
            }
            finally { AirDefensePipeline.Remove(sureHit); }
        }

        public static IEnumerable<Wait> ProvidedTrack(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            yield return new WaitTicks(20);
            TestPlainFlyer flyer = MakeFlyer<TestPlainFlyer>(Player, site.Tile);
            Find.WorldObjects.Add(flyer);
            Track(flyer);
            TestFlyerProvider provider = new TestFlyerProvider { wrapper = new TestFlyerWrapper { flyer = flyer, to = site.Tile } };
            int detected = 0;
            Action<AirCombatEvent> count = e =>
            {
                // Each faction reports its own first sight; a neutral settlement may see it too.
                if (e.Kind == AirCombatEventKind.TrackDetected && e.TargetObject == flyer && e.Defender == site.Faction)
                {
                    detected++;
                    if (e.Track != provider.wrapper) { detected = -1000; }
                }
            };
            AirCombatEvents.Raised += count;
            AirDefenseExtensions.AddProvider(provider);
            try
            {
                t.Check(Network.TrackOf(flyer) != null, "a provided object is a track");
                var waitSeen = new WaitUntil(() => detected != 0, 600);
                yield return waitSeen;
                t.Check(detected == 1, $"the base reports it once, naming the mod's own track (saw {detected})");
                IBVRTrack track = Network.TrackOf(flyer);
                t.Check(track.State.FactionSees(site.Faction, int.MinValue), "the network holds its contact");

                provider.wrapper = null;
                yield return new WaitTicks(BVRWorldTuningDef.Current.scanIntervalTicks + 1);
                t.Check(Network.TrackOf(flyer) == null, "once dropped, the object is no track");
                t.Check(!Network.Tracks.Any(x => x.Self == flyer), "the network no longer lists it");
                t.Check(Field<Dictionary<int, BVRTrackState>>(Network, "externalStates").Count == 0, "its state is discarded");
            }
            finally
            {
                AirCombatEvents.Raised -= count;
                AirDefenseExtensions.RemoveProvider(provider);
            }
        }

        public static IEnumerable<Wait> PodGhost(TestContext t)
        {
            SpawnBuilding(PhaseArray, 10, 10);
            yield return WaitPowered();
            t.Require(HomeBVR.RegisterSalvo(CruiseIncoming, 1, CruiseConfig(), Enemy, Player), "a wave target exists");
            int targetId = HomeBVR.Waves[HomeBVR.Waves.Count - 1].targets[0].id;

            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamed("DMSE_DropPodTrack");
            WorldObject_DropPodTrack ghost = (WorldObject_DropPodTrack)WorldObjectMaker.MakeWorldObject(def);
            ghost.Tile = Home.Tile;
            ghost.SetFaction(Enemy);
            ghost.spawnTick = Find.TickManager.TicksGame;
            ghost.arrivalTick = ghost.spawnTick + 2000;
            ghost.startPos = MissileWorldUtility.TileCenter(TileAtDistance(4f, 6f));
            ghost.endPos = MissileWorldUtility.TileCenter(Home.Tile);
            ghost.targetTile = Home.Tile;
            ghost.targetMap = Home;
            ghost.waveTargetId = targetId;
            ghost.incomingDef = CruiseIncoming;
            Find.WorldObjects.Add(ghost);
            Track(ghost);

            t.Check(!typeof(MissileWorldObject).IsAssignableFrom(typeof(WorldObject_DropPodTrack)), "the ghost is not a missile");
            t.Check(ReferenceEquals(Network.TrackOf(ghost), ghost) && Network.Tracks.Contains(ghost), "the network tracks it directly");
            t.Check(!Network.MissileTracks.Any(x => (WorldObject)x == ghost), "it is not listed among missiles");
            t.Check(ghost.Config == null && ghost.TargetProps == CruiseIncoming.GetModExtension<BVRTargetProps>(), "its signature comes from the incoming def");
            ghost.Notify_Intercepted(Player);
            t.Check(ghost.Destroyed && HomeBVR.GetTarget(targetId) == null, "intercepting the ghost removes its wave target");
            HomeBVR.Waves.Clear();
        }

        public static IEnumerable<Wait> XmlLoading(TestContext t)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(@"<Fortified.AirDefenseExtensionDef>
                <defName>ADTest_Xml</defName>
                <modifiers>
                  <li Class=""Fortified.AirDefenseModifier_Factor""><stat>HitChance</stat><factor>0.5</factor><offset>0.1</offset><order>3</order><mapOnly>true</mapOnly></li>
                </modifiers>
                <listeners><li Class=""DMSE.BVRAcceptance.TestXmlListener""><note>7</note></li></listeners>
                <trackProviders><li Class=""DMSE.BVRAcceptance.TestFlyerProvider"" /></trackProviders>
              </Fortified.AirDefenseExtensionDef>");
            AirDefenseExtensionDef def = DirectXmlToObject.ObjectFromXml<AirDefenseExtensionDef>(doc.DocumentElement, false);
            t.Require(def != null, "the extension def parses");
            t.Check(!def.ConfigErrors().Any(), "it has no config errors");
            AirDefenseModifier_Factor factor = def.modifiers.OfType<AirDefenseModifier_Factor>().FirstOrDefault();
            t.Check(factor != null && factor.stat == AirDefenseStat.HitChance && factor.factor == 0.5f && factor.offset == 0.1f && factor.order == 3 && factor.mapOnly,
                "the modifier keeps its settings");
            t.Check(def.listeners.OfType<TestXmlListener>().FirstOrDefault()?.note == 7, "a listener class from another assembly loads");
            t.Check(def.trackProviders.OfType<TestFlyerProvider>().Any(), "a provider class from another assembly loads");

            AirDefenseExtensions.Register(def);
            try
            {
                t.Check(AirDefensePipeline.MayModify() && AirCombatEvents.HasListeners, "registering the def activates its modifier and listener");
            }
            finally
            {
                AirDefensePipeline.Remove(def.modifiers[0]);
                AirCombatEvents.RemoveListener(def.listeners[0]);
                AirDefenseExtensions.RemoveProvider(def.trackProviders[0]);
            }

            // The behaviors from the earlier framework work were only ever attached in code.
            XmlDocument profile = new XmlDocument();
            profile.LoadXml(@"<li Class=""Fortified.MissileBehaviorProps_FlightProfile"">
                <phases>
                  <li><untilProgress>0.4</untilProgress><speedFactor>2</speedFactor><altitude>30000</altitude></li>
                  <li><untilProgress>1</untilProgress><altitude>50</altitude><evasiveEntry>true</evasiveEntry></li>
                </phases>
              </li>");
            MissileBehaviorProps props = DirectXmlToObject.ObjectFromXml<MissileBehaviorProps>(profile.DocumentElement, false);
            MissileBehaviorProps_FlightProfile parsed = props as MissileBehaviorProps_FlightProfile;
            t.Require(parsed != null, "a behavior loads through its Class attribute");
            t.Check(parsed.behaviorClass == typeof(MissileBehavior_FlightProfile), "the XML route keeps the behavior class");
            t.Check(parsed.phases.Count == 2 && parsed.phases[0].speedFactor == 2f && parsed.phases[1].evasiveEntry && !parsed.ConfigErrors(CruiseBody).Any(), "the phases parse");
            yield break;
        }
    }
}
