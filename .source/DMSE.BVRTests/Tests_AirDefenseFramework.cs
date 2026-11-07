using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

static partial class Test
{
    class AddModifier : AirDefenseModifier
    {
        public float add;
        public override void Modify(AirDefenseQuery query, ref float value) => value += add;
    }
    class TimesModifier : AirDefenseModifier
    {
        public float times;
        public override void Modify(AirDefenseQuery query, ref float value) => value *= times;
    }
    class ThrowingModifier : AirDefenseModifier
    {
        public override void Modify(AirDefenseQuery query, ref float value) { value = 999f; throw new InvalidOperationException("boom"); }
    }
    class NaNModifier : AirDefenseModifier
    {
        public override void Modify(AirDefenseQuery query, ref float value) => value = float.NaN;
    }
    class TargetTimesProps : MissileBehaviorProps
    {
        public float asTarget = 1f, asInterceptor = 1f;
        public override void ModifyDefenseAsTarget(AirDefenseQuery query, ref float value) => value *= asTarget;
        public override void ModifyDefenseAsInterceptor(AirDefenseQuery query, ref float value) => value *= asInterceptor;
    }
    class FakeLoadout : IMissileLoadout
    {
        public List<MissileBehaviorProps> props = new List<MissileBehaviorProps>();
        public float PayloadCapacity => 0f;
        public bool PenetratesThickRoof => false;
        public IEnumerable<MissileBehaviorProps> BehaviorProps => props;
    }
    class FakeAircraft : IAirTrack
    {
        public AirTrackSignature signature = new AirTrackSignature { distanceLevel = 2f, stealthLevel = 3, speed = 40f, altitude = 9000f };
        public float selfFactor = 1f;
        public int hits;
        public WorldObject Self => null;
        public Faction Attacker => null;
        public PlanetTile Origin => PlanetTile.Invalid;
        public PlanetTile Destination => PlanetTile.Invalid;
        public AirTrackSignature Signature => signature;
        public float AngularSpeed => 0.001f;
        public int TicksToArrival => 500;
        public Vector3 PositionAt(int ticksAhead) => Vector3.forward * 100f;
        public bool ModifiesDefense => selfFactor != 1f;
        public void ModifyDefense(AirDefenseQuery query, ref float value) => value *= selfFactor;
        public void Notify_Intercepted(Faction by) => hits++;
    }
    class RecordingListener : AirCombatListener
    {
        public int count;
        public override void Notify(AirCombatEvent e) => count++;
    }
    class ThrowingListener : AirCombatListener
    {
        public override void Notify(AirCombatEvent e) => throw new InvalidOperationException("boom");
    }

    // Air defense framework: modifier chain order and limits, failure isolation, event stream, non-missile tracks.
    static void AirDefenseFramework()
    {
        var hit = new AirDefenseQuery { Stat = AirDefenseStat.HitChance };

        // With nothing registered the chain is the identity and callers can skip building a query.
        Check(!AirDefensePipeline.MayModify(), "nothing registered: no modification possible");
        Check(!AirDefensePipeline.MayModify(new FakeAircraft(), new FakeLoadout()), "a plain aircraft and an empty loadout modify nothing");
        Near(AirDefensePipeline.Evaluate(hit, 0.5f), 0.5f, "no modifier keeps the value");

        // Order decides the result: later order runs after earlier order, registration breaks ties.
        var late = new TimesModifier { times = 2f, order = 10 };
        var early = new AddModifier { add = 0.1f, order = -5 };
        var tied = new AddModifier { add = 0.05f, order = 10 };
        try
        {
            AirDefensePipeline.Add(late);
            AirDefensePipeline.Add(tied);
            AirDefensePipeline.Add(early);
            AirDefensePipeline.Add(early);
            Check(AirDefensePipeline.MayModify(), "a registered modifier may modify");
            Near(AirDefensePipeline.Evaluate(hit, 0.2f), (0.2f + 0.1f) * 2f + 0.05f, "order first, then registration order; duplicates are ignored");
        }
        finally
        {
            AirDefensePipeline.Remove(late);
            AirDefensePipeline.Remove(tied);
            AirDefensePipeline.Remove(early);
        }
        Check(!AirDefensePipeline.MayModify(), "removed modifiers no longer apply");

        // Each stat keeps its own limits whatever the modifiers do.
        var huge = new AddModifier { add = 100f };
        var negative = new AddModifier { add = -1000f };
        try
        {
            AirDefensePipeline.Add(huge);
            Near(AirDefensePipeline.Evaluate(hit, 0.5f), 1f, "hit chance is capped at 1");
            AirDefensePipeline.Remove(huge);
            AirDefensePipeline.Add(negative);
            Near(AirDefensePipeline.Evaluate(hit, 0.5f), 0f, "hit chance is floored at 0");
            Near(AirDefensePipeline.Evaluate(new AirDefenseQuery { Stat = AirDefenseStat.LockTicks }, 120f), 1f, "a lock takes at least one tick");
            Near(AirDefensePipeline.Evaluate(new AirDefenseQuery { Stat = AirDefenseStat.Detection }, 30f), 0f, "detection is floored at 0");
        }
        finally
        {
            AirDefensePipeline.Remove(huge);
            AirDefensePipeline.Remove(negative);
        }

        // One failing modifier is skipped: its partial write is discarded and the rest of the chain still runs.
        var thrower = new ThrowingModifier();
        var nan = new NaNModifier();
        var after = new TimesModifier { times = 0.5f };
        try
        {
            AirDefensePipeline.Add(thrower);
            AirDefensePipeline.Add(nan);
            AirDefensePipeline.Add(after);
            Near(AirDefensePipeline.Evaluate(hit, 0.8f), 0.4f, "throwing and NaN modifiers are skipped");
        }
        finally
        {
            AirDefensePipeline.Remove(thrower);
            AirDefensePipeline.Remove(nan);
            AirDefensePipeline.Remove(after);
        }

        // Factor modifier: the XML route, filtered by stat and layer.
        var factor = new AirDefenseModifier_Factor { stat = AirDefenseStat.HitChance, factor = 0.5f, offset = 0.1f, worldOnly = true };
        var scratch = 0.6f;
        factor.Modify(new AirDefenseQuery { Stat = AirDefenseStat.HitChance, Layer = AirCombatLayer.World }, ref scratch);
        Near(scratch, 0.4f, "factor then offset");
        factor.Modify(new AirDefenseQuery { Stat = AirDefenseStat.LockTicks, Layer = AirCombatLayer.World }, ref scratch);
        Near(scratch, 0.4f, "another stat is untouched");
        factor.Modify(new AirDefenseQuery { Stat = AirDefenseStat.HitChance, Layer = AirCombatLayer.Map }, ref scratch);
        Near(scratch, 0.4f, "worldOnly skips the map layer");
        var extension = new AirDefenseExtensionDef { defName = "ADTest" };
        Check(extension.ConfigErrors().Any(), "an empty extension def is an error");
        extension.modifiers = new List<AirDefenseModifier> { new AirDefenseModifier_Factor { worldOnly = true, mapOnly = true } };
        Check(extension.ConfigErrors().Any(e => e.Contains("worldOnly")), "a modifier limited to both layers is an error");

        // Loadout and track hooks: each side only sees its own role.
        var shooter = new FakeLoadout { props = { new TargetTimesProps { asTarget = 100f, asInterceptor = 0.5f } } };
        var missile = new FakeLoadout { props = { new TargetTimesProps { asTarget = 0.5f, asInterceptor = 100f }, null } };
        Check(AirDefensePipeline.MayModify(null, missile), "a loadout with behaviors may modify");
        Near(AirDefensePipeline.Evaluate(new AirDefenseQuery { Stat = AirDefenseStat.HitChance, TargetLoadout = missile, InterceptorLoadout = shooter }, 0.8f),
            0.2f, "target and interceptor hooks apply to their own role");
        var jetLike = new FakeAircraft { selfFactor = 0.25f };
        Check(AirDefensePipeline.MayModify(jetLike), "a track declaring modifiers may modify");
        Near(AirDefensePipeline.Evaluate(new AirDefenseQuery { Stat = AirDefenseStat.HitChance, Track = jetLike }, 0.8f), 0.2f, "the track's own hook applies");

        // Events: disabled until someone listens, and one failing handler does not stop the others.
        Check(!AirCombatEvents.HasListeners, "no listeners by default");
        AirCombatEvents.Raise(null);
        var seen = new List<AirCombatEvent>();
        Action<AirCombatEvent> record = seen.Add;
        Action<AirCombatEvent> bad = _ => throw new InvalidOperationException("boom");
        var listener = new RecordingListener();
        var badListener = new ThrowingListener();
        try
        {
            AirCombatEvents.Raised += bad;
            AirCombatEvents.Raised += record;
            AirCombatEvents.AddListener(badListener);
            AirCombatEvents.AddListener(listener);
            AirCombatEvents.AddListener(listener);
            Check(AirCombatEvents.HasListeners, "listeners are detected");
            var sent = new AirCombatEvent { Kind = AirCombatEventKind.InterceptResolved, Outcome = AirInterceptOutcome.Hit };
            AirCombatEvents.Raise(sent);
            Check(seen.Count == 1 && seen[0] == sent, "the event reaches the handler after a failing one");
            Check(listener.count == 1, "the listener runs once after a failing one");
        }
        finally
        {
            AirCombatEvents.Raised -= bad;
            AirCombatEvents.Raised -= record;
            AirCombatEvents.RemoveListener(listener);
            AirCombatEvents.RemoveListener(badListener);
        }
        Check(!AirCombatEvents.HasListeners, "unsubscribing disables events again");

        // Hooks skip all work without listeners or modifiers.
        Check(AirDefenseHooks.MapEvent(AirCombatEventKind.LockStarted, null, null, null) == null, "no listeners: no map event object");
        Near(AirDefenseHooks.MapStat(AirDefenseStat.HitChance, 0.3f, null, null, null), 0.3f, "no modifiers: the map value passes through");
        Near(AirDefenseHooks.WorldStat(AirDefenseStat.Detection, 12f, null, null, null), 12f, "no modifiers: the world value passes through");

        // A track that is not a missile: the wrapper presents it to the network without a missile config.
        var aircraft = new FakeAircraft();
        var wrapper = new BVRExternalTrack(aircraft, new BVRTrackState());
        BVRTargetProps props = wrapper.TargetProps;
        Check(wrapper.Config == null && wrapper.Loadout == null, "an aircraft has no missile config");
        Near(props.distanceLevel, 2f, "the wrapper reads the signature");
        Near(props.altitude, 9000f, "the wrapper reads the altitude");
        Check(props.stealthLevel == 3 && Mathf.Approximately(props.speed, 40f), "the wrapper reads stealth and speed");
        Check(ReferenceEquals(wrapper.TargetProps, props), "an unchanged signature reuses the props");
        aircraft.signature.altitude = 50f;
        Near(wrapper.TargetProps.altitude, 50f, "a changed signature is picked up");
        Check(!ReferenceEquals(BVRTargetProps.Default, wrapper.TargetProps), "the shared default props stay untouched");
        Near(BVRTargetProps.Default.altitude, 1000f, "the shared default keeps its altitude");
        Check(wrapper.TicksToArrival == 500 && wrapper.PositionAt(0) == aircraft.PositionAt(0), "motion comes from the aircraft");
        IBVRTrack missileTrack = new FakeTrack { angle = 1f, speed = 0.001f };
        Near(((IAirTrack)missileTrack).Signature.altitude, BVRTargetProps.Default.altitude, "a network track derives its signature from its props");
        Check(((IAirTrack)missileTrack).RouteRevision == 0 && ((IAirTrack)missileTrack).TargetParent == null, "optional track members have defaults");
        BVRInterceptSolver.TrySolve(new FakeTrack { angle = 1f, speed = 1f / 30000f }, new FakeTrack { angle = 1f, speed = 1f / 30000f }.PositionAt(30000), 2.25f / 30000f, out int solved, out _);
        Check(solved > 0, "the solver still accepts network tracks");
    }
}
