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
    class SpeedProps : MissileBehaviorProps { public float factor; public SpeedProps() { behaviorClass = typeof(SpeedBehavior); } }
    class SpeedBehavior : MissileBehavior
    {
        public override bool HasSpeedProfile => true;
        public override float SpeedFactorAt(float routeProgress) => ((SpeedProps)props).factor;
    }
    class VariableFakeTrack : FakeTrack, IAirTrack { bool IAirTrack.VariableSpeed => true; }

    // Missile behavior framework: signature copy (altitude bug), behavior set, flight profile data, virtual guidance traits.
    static void MissileBehaviors()
    {
        // A signature part used to drop the altitude and debris profile of the base props.
        var debris = new InterceptDebrisProfileDef { defName = "SignatureDebris" };
        var skyfaller = new ThingDef
        {
            defName = "SignatureIncoming",
            modExtensions = new List<DefModExtension>
            {
                new BVRTargetProps { distanceLevel = 1.5f, stealthLevel = 2, speed = 20f, altitude = 50f, debrisProfile = debris },
            },
        };
        var body = new MissileBodyDef { defName = "SignatureBody", incomingSkyfaller = skyfaller };
        var decoy = new MissilePartDef { defName = "SignaturePart", category = MissilePartCategory.Payload, signatureFactor = 2f };
        var config = new MissileConfig(body);
        config.parts.Add(decoy);
        var track = new WorldObject_IncomingMissile { config = config };
        BVRTargetProps props = track.TargetProps;
        Near(props.distanceLevel, 3f, "signature factor scales distance level");
        Near(props.altitude, 50f, "signature factor keeps the altitude");
        Check(props.debrisProfile == debris, "signature factor keeps the debris profile");
        Check(props.stealthLevel == 2 && Mathf.Approximately(props.speed, 20f), "signature factor keeps stealth and speed");
        Check(ReferenceEquals(track.TargetProps, props), "unchanged signature reuses the cached props");

        var plain = new WorldObject_IncomingMissile { config = new MissileConfig(body) };
        Check(ReferenceEquals(plain.TargetProps, skyfaller.GetModExtension<BVRTargetProps>()), "no factor and no behavior returns the def's own props");

        // Behavior set: built from body then parts, only for props with a behavior class.
        var stateless = new MissileBehaviorProps();
        body.behaviors = new List<MissileBehaviorProps> { new SpeedProps { factor = 2f }, stateless };
        decoy.behaviors = new List<MissileBehaviorProps> { new SpeedProps { factor = 0.25f } };
        Check(config.BehaviorProps.Count() == 3, "config lists body and part behaviors");
        Check(config.BehaviorProps.First() == body.behaviors[0] && config.BehaviorProps.Last() == decoy.behaviors[0], "body behaviors come first");
        MissileBehaviorSet set = MissileBehaviorSet.Create(config, track);
        Check(set.Count == 2 && set.HasSpeedProfile, "stateless props create no instance; speed profile is detected");
        Near(set.SpeedFactorAt(0.5f), 0.5f, "speed factors multiply");
        Check(MissileBehaviorSet.Create(null, track) == MissileBehaviorSet.Empty, "no loadout means the shared empty set");
        body.behaviors = new List<MissileBehaviorProps> { new SpeedProps { factor = 0f } };
        decoy.behaviors = null;
        Near(MissileBehaviorSet.Create(config, track).SpeedFactorAt(0f), 0.01f, "speed factor never reaches zero");
        body.behaviors = new List<MissileBehaviorProps> { new MissileBehaviorProps { behaviorClass = typeof(string) } };
        Check(body.behaviors[0].ConfigErrors(body).Any(), "a behaviorClass of the wrong type is a config error");
        body.behaviors = null;

        // Flight profile data.
        var profile = new MissileBehaviorProps_FlightProfile
        {
            phases = new List<MissileFlightPhase>
            {
                new MissileFlightPhase { untilProgress = 0.3f, speedFactor = 0.5f },
                new MissileFlightPhase { untilProgress = 1f, speedFactor = 2f, altitude = 50f, stealthOffset = 1 },
            },
        };
        Check(profile.PhaseIndexAt(0f) == 0 && profile.PhaseIndexAt(0.29f) == 0 && profile.PhaseIndexAt(0.3f) == 1, "phase boundaries are exclusive");
        Check(profile.PhaseIndexAt(1f) == 1, "the last phase covers arrival");
        Check(!profile.ConfigErrors(body).Any(), "a valid profile has no errors");
        Check(new MissileBehaviorProps_FlightProfile().ConfigErrors(body).Any(), "an empty profile is an error");
        profile.phases[1].untilProgress = 0.2f;
        Check(profile.ConfigErrors(body).Any(), "phases must increase");
        profile.phases[1].untilProgress = 1f;
        Near(profile.CreateBehavior(new FrameworkFakeTrack { progress = 0.1f }).SpeedFactorAt(0.5f), 2f, "speed factor reads the asked progress");
        MissileBehavior flight = profile.CreateBehavior(new FrameworkFakeTrack { progress = 0.5f });
        var signature = new AirTrackSignature { distanceLevel = 1f, stealthLevel = 1, speed = 15f, altitude = 1000f };
        flight.ModifySignature(ref signature);
        Check(Mathf.Approximately(signature.altitude, 50f) && signature.stealthLevel == 2, "a phase changes altitude and stealth");

        // Release settings.
        var release = new MissileBehaviorProps_ReleaseChildren();
        Check(release.ConfigErrors(body).Any(), "a body releasing itself is an error");
        Check(release.MakeChildConfig(null) == null, "no body means no child");
        Check(release.MakeChildConfig(body).body == body, "child falls back to the parent's body");
        release.childParts = new List<MissilePartDef> { decoy };
        Check(release.MakeChildConfig(body).parts.Contains(decoy), "child parts come from the settings");

        // Traits that used to be type checks.
        Check(new GuidanceType_AntiRadiation().HomesOnEmissions && !new GuidanceType_ActiveRadar().HomesOnEmissions, "only anti-radiation homes on emissions");
        Check(new GuidanceType_TV().UsesGroundChannel && new GuidanceType_TV().ExtraGroundChannels == 1, "TV books one extra channel");
        Check(new GuidanceType_RadioCommand().UsesGroundChannel && !new GuidanceType_Laser().UsesGroundChannel, "ground channels follow the guidance trait");

        // Variable speed takes the general solver instead of the constant-speed closed form.
        var variable = new VariableFakeTrack { angle = 1f, speed = 1f / 30000f };
        Vector3 node = variable.PositionAt(variable.TicksToArrival);
        Check(BVRInterceptSolver.TrySolve(variable, node, 2.25f / 30000f, out int solved, out _), "variable-speed track is solvable");
        BVRInterceptSolver.TrySolveGeneral(variable, node, 2.25f / 30000f, out int general, out _);
        Check(solved == general, "variable-speed track uses the general solution");
    }

    class FrameworkFakeTrack : IMissileTrack
    {
        public float progress;
        public WorldObject Self => null;
        public Faction Attacker => null;
        public IMissileLoadout Loadout => null;
        public PlanetTile Origin => PlanetTile.Invalid;
        public PlanetTile Destination => PlanetTile.Invalid;
        public MapParent TargetParent => null;
        public AirTrackSignature Signature => default;
        public float AngularSpeed => 0f;
        public void Notify_Intercepted(Faction by) { }
        public int TicksToArrival => 0;
        public Vector3 PositionAt(int ticksAhead) => Vector3.zero;
        public float RouteProgress => progress;
        public void NotifyManoeuvre() { }
    }
}
