using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using UnityEngine;
using Verse;

static partial class Test
{
    // Intercept altitude envelope, altitude prediction along a flight profile, and the display mapping of altitude tracks.
    static void InterceptAltitude()
    {
        var open = new CompProperties_FireControlRadar();
        Check(!open.HasAltitudeLimits && open.InAltitudeEnvelope(0f) && open.InAltitudeEnvelope(1000000f), "no limits by default");
        Near(open.AltitudeHitFactor(50f), 1f, "no limits, no penalty");

        var radar = new CompProperties_FireControlRadar { minInterceptAltitude = 100f, maxInterceptAltitude = 20000f };
        Check(radar.HasAltitudeLimits, "limits are detected");
        Check(!radar.InAltitudeEnvelope(99f) && !radar.InAltitudeEnvelope(20001f), "outside the envelope");
        Check(radar.InAltitudeEnvelope(100f) && radar.InAltitudeEnvelope(20000f), "the limits themselves are inside");
        Near(radar.AltitudeHitFactor(99f), 0f, "below the floor: no chance");
        Near(radar.AltitudeHitFactor(20001f), 0f, "above the ceiling: no chance");
        Near(radar.AltitudeHitFactor(100f), 0.6f, "at the floor: the edge factor");
        Near(radar.AltitudeHitFactor(20000f), 0.6f, "at the ceiling: the edge factor");
        Near(radar.AltitudeHitFactor(150f), 1f, "one edge ratio above the floor: full chance");
        Near(radar.AltitudeHitFactor(5000f), 1f, "mid envelope: full chance");
        float partway = radar.AltitudeHitFactor(120f);
        Check(partway > 0.6f && partway < 1f, $"inside the edge band the chance rises ({partway})");

        var floorless = new CompProperties_FireControlRadar { maxInterceptAltitude = 1000f };
        Check(floorless.InAltitudeEnvelope(0f), "no floor reaches the ground");
        Near(floorless.AltitudeHitFactor(10f), 1f, "no floor, no floor penalty");
        Check(new CompProperties_FireControlRadar { minInterceptAltitude = 100f }.InAltitudeEnvelope(1e6f), "a negative ceiling means none");

        var parent = new ThingDef { defName = "AltitudeRadar" };
        Check(!radar.ConfigErrors(parent).Any(), "valid limits have no errors");
        Check(new CompProperties_FireControlRadar { minInterceptAltitude = 500f, maxInterceptAltitude = 100f }.ConfigErrors(parent).Any(), "max below min is an error");
        Check(new CompProperties_FireControlRadar { minInterceptAltitude = -1f }.ConfigErrors(parent).Any(), "a negative floor is an error");

        // The hit formula applies the factor to every caller, whichever layer built the target.
        var settings = new CompProperties_FireControlRadar { maxHitChance = 1f, guidanceAccuracyTime = 100f, minInterceptAltitude = 100f, maxInterceptAltitude = 20000f };
        Near(CompFireControlRadar.ComputeHitChance(settings, new BVRTarget { altitude = 5000f }, 100), 1f, "mid envelope keeps the chance");
        Near(CompFireControlRadar.ComputeHitChance(settings, new BVRTarget { altitude = 100f }, 100), 0.6f, "the edge lowers the chance");
        Near(CompFireControlRadar.ComputeHitChance(settings, new BVRTarget { altitude = 60f }, 100), 0f, "out of the envelope there is no chance");
        Near(new BVRTarget().altitude, BVRTargetProps.Default.altitude, "a target without props uses the neutral altitude");

        // Prediction: a phase's altitude applies to the asked progress, not the current one.
        var profile = new MissileBehaviorProps_FlightProfile
        {
            phases = new System.Collections.Generic.List<MissileFlightPhase>
            {
                new MissileFlightPhase { untilProgress = 0.5f, altitude = 30000f },
                new MissileFlightPhase { untilProgress = 0.8f },
                new MissileFlightPhase { untilProgress = 1f, altitude = 50f },
            },
        };
        MissileBehavior flight = profile.CreateBehavior(new FrameworkFakeTrack { progress = 0f });
        float predicted = 1000f;
        flight.ModifyAltitudeAt(0.9f, ref predicted);
        Near(predicted, 50f, "the later phase is predicted");
        predicted = 1000f;
        flight.ModifyAltitudeAt(0.6f, ref predicted);
        Near(predicted, 1000f, "a phase without an altitude keeps the base altitude");
        Near(((IAirTrack)new FakeAircraft()).AltitudeAt(500), 9000f, "a plain track flies at its signature altitude");
        var wrapped = new BVRExternalTrack(new FakeAircraft(), new BVRTrackState());
        Near(((IAirTrack)wrapped).AltitudeAt(100), 9000f, "the network wrapper reads the track's altitude");

        // Status.
        var state = new BVRTrackState { detectedByFaction = null, altitudeDenied = true };
        Check(state.StatusKey == "DMSE.BVR.World.AltitudeDenied", "an unreachable track says so");
        state.lockUntil = 5;
        Check(state.StatusKey == "DMSE.BVR.World.Locking", "a lock in progress takes priority");

        // Display mapping.
        Check(AirTrackDisplay.Lift(50f) < AirTrackDisplay.Lift(3000f) && AirTrackDisplay.Lift(3000f) < AirTrackDisplay.Lift(100000f), "higher flies higher");
        Check(AirTrackDisplay.Lift(50f) > 0.1f, "even a sea skimmer clears the ground");
        Check(AirTrackDisplay.Lift(1e7f) <= 4.5f && AirTrackDisplay.Lift(0f) >= 0.15f, "the lift stays in range");
        Check(AirTrackDisplay.WidthFactor(50f) < 1f && AirTrackDisplay.WidthFactor(100000f) > 1f, "higher lines are wider");
        Near(AirTrackDisplay.CurrentLift(new FakeAircraft()), AirTrackDisplay.Lift(9000f), "a plain track draws at its full height");
        Near(AirTrackDisplay.CurrentLift(new FrameworkFakeTrack { progress = 0f }), 0f, "a missile at launch draws on the ground");
        Near(AirTrackDisplay.CurrentLift(new FrameworkFakeTrack { progress = 1f }), 0f, "a missile on arrival draws on the ground");
        Check(AirTrackDisplay.CurrentLift(new FrameworkFakeTrack { progress = 0.5f }) > 0f, "a missile mid route draws in the air");
        Near(AirTrackDisplay.Raised(new Vector3(100f, 0f, 0f), 2f).x, 102f, "raising follows the surface normal");
        Near(AirTrackDisplay.Ramp(1f, 1f), 1f, "mid route is at full height");
        Near(AirTrackDisplay.Ramp(0f, 1f), 0f, "the launch point is on the ground");
        Near(AirTrackDisplay.Ramp(1f, 0f), 0f, "the arrival point is on the ground");
        Check(AirTrackDisplay.Band(60f) == 0 && AirTrackDisplay.Band(3000f) == 1 && AirTrackDisplay.Band(10000f) == 2 && AirTrackDisplay.Band(50000f) == 3, "altitude bands");
        Check(MissileWorldMotion.AltitudeLabel(60f) == "60 m" && MissileWorldMotion.AltitudeLabel(1500f) == "1.5 km", "altitude labels");
    }
}
