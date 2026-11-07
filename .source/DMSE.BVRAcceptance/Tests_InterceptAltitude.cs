using System.Collections.Generic;
using System.Linq;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>Intercept altitude envelopes on the real scan and combat paths, plus the altitude track drawing.</summary>
    public static class Tests_InterceptAltitude
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("AE1", "World layer: a track outside the envelope is seen but not engaged, and is engaged once it enters it", WorldEnvelope),
            new AcceptanceTest("AE2", "Map layer: a target outside the envelope takes no lock; inside it does", MapEnvelope),
            new AcceptanceTest("AE3", "Shipped envelopes give each generation its reach", ShippedValues),
            new AcceptanceTest("AE4", "Altitude tracks draw on the globe", TrackScreenshots),
        };

        private static CompProperties_FireControlRadar PhaseFc => PhaseArray.GetCompProperties<CompProperties_FireControlRadar>();
        private static CompProperties_FireControlRadar Fc(string defName) => ThingDef.Named(defName).GetCompProperties<CompProperties_FireControlRadar>();
        private static float AltitudeOf(string incoming) => ThingDef.Named(incoming).GetModExtension<BVRTargetProps>().altitude;

        public static IEnumerable<Wait> WorldEnvelope(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            yield return new WaitTicks(20);
            float oldMin = PhaseFc.minInterceptAltitude;
            Override(() => PhaseFc.minInterceptAltitude = 100f, () => PhaseFc.minInterceptAltitude = oldMin);

            // The 60 m cruise missile is under the floor set above.
            ScorerProjectile_WorldObject low = LaunchPlayerMissile(site.Tile, site, CruiseConfig());
            t.Check(low.AltitudeAt(0) == 60f, "the cruise missile flies at 60 m");
            var waitSeen = new WaitUntil(() => low.Destroyed || (low.State.FactionSees(site.Faction, int.MinValue) && low.State.altitudeDenied), low.TicksToArrival);
            yield return waitSeen;
            t.Require(!waitSeen.TimedOut && !low.Destroyed, "the base sees it and reports it as out of reach");
            t.Check(low.State.StatusKey == "DMSE.BVR.World.AltitudeDenied", $"the status says so ({low.State.StatusKey})");
            t.Check(low.GetInspectString().Contains(MissileWorldMotion.AltitudeLabel(60f)), "the inspect text shows the altitude");
            yield return new WaitTicks(300);
            t.Check(!low.Destroyed && low.State.interceptor == null && low.State.lockUntil < 0 && low.State.interceptAttempts == 0, "no lock, no launch");
            low.Destroy();

            // Starts under the floor, climbs into the envelope: engaged only after the climb.
            MissileBodyDef body = CruiseBody;
            body.behaviors = new List<MissileBehaviorProps>
            {
                new MissileBehaviorProps_FlightProfile
                {
                    phases = new List<MissileFlightPhase>
                    {
                        new MissileFlightPhase { untilProgress = 0.3f, altitude = 50f },
                        new MissileFlightPhase { untilProgress = 1f, altitude = 5000f },
                    },
                },
            };
            try
            {
                ScorerProjectile_WorldObject climbing = LaunchPlayerMissile(site.Tile, site, CruiseConfig());
                t.Check(climbing.AltitudeAt(0) == 50f && climbing.AltitudeAt(climbing.TicksToArrival - 1) == 5000f, "the profile predicts the later altitude");
                var waitEngage = new WaitUntil(() => climbing.Destroyed || climbing.State.interceptor != null, climbing.TicksToArrival);
                yield return waitEngage;
                t.Require(!climbing.Destroyed && climbing.State.interceptor != null, "the base engages after the altitude change");
                t.Check(climbing.RouteProgress >= 0.3f, $"not before it reached the envelope (route {climbing.RouteProgress:0.00})");
                t.Check(!climbing.State.altitudeDenied, "it is no longer reported as out of reach");
                WorldObject_Interceptor shot = climbing.State.interceptor;
                t.Check(shot.MeetLift > 0f == BVRWorldDrawer.KnownToPlayer(climbing), "the interceptor climbs only toward a track the player knows");
                t.Check(climbing.Destination == site.Tile && climbing.PositionAt(0).magnitude < climbing.DrawPos.magnitude, "the missile is drawn above its ground position");
                climbing.Destroy();
            }
            finally { body.behaviors = null; }
        }

        public static IEnumerable<Wait> MapEnvelope(TestContext t)
        {
            float oldMin = PhaseFc.minInterceptAltitude;
            SpawnBuilding(PhaseArray, 10, 10);
            SpawnLoadedLauncher(14, 10, 4);
            yield return WaitPowered();
            yield return new WaitTicks(2);
            MapComponent_BVRCombat bvr = HomeBVR;

            Override(() => PhaseFc.minInterceptAltitude = 100f, () => PhaseFc.minInterceptAltitude = oldMin);
            t.Require(bvr.RegisterSalvo(CruiseIncoming, 1, CruiseConfig(), Enemy, Player), "a salvo is registered");
            BVRTarget target = bvr.Waves[bvr.Waves.Count - 1].targets[0];
            t.Check(target.altitude == 60f, "the target carries its altitude");
            yield return new WaitTicks(120);
            t.Check(target.lockUntil < 0 && bvr.CountEngaged(Player) == 0, "under the floor: no lock");

            PhaseFc.minInterceptAltitude = oldMin;
            var waitLock = new WaitUntil(() => target.lockUntil >= 0 || target.midcourseEngagedUntil > 0, 120);
            yield return waitLock;
            t.Check(!waitLock.TimedOut, "inside the envelope the radar locks");
            bvr.Waves.Clear();
        }

        public static IEnumerable<Wait> ShippedValues(TestContext t)
        {
            UseShippedRadarValues();
            CompProperties_FireControlRadar early = Fc("DMSE_EarlyFireControlRadar"), mid = Fc("DMSE_FireControlRadar"), phase = PhaseFc;
            float moskit = AltitudeOf("DMSE_Incoming_Moskit"), tochka = AltitudeOf("DMSE_Incoming_Tochka"), ballistic = AltitudeOf("DMSE_Incoming_AdvancedBallisticMissile");
            t.Check(!early.InAltitudeEnvelope(moskit) && mid.InAltitudeEnvelope(moskit) && phase.InAltitudeEnvelope(moskit), "a sea skimmer is under the early radar only");
            t.Check(!early.InAltitudeEnvelope(tochka) && !mid.InAltitudeEnvelope(tochka) && phase.InAltitudeEnvelope(tochka), "only the phased array reaches a high ballistic missile");
            t.Check(phase.InAltitudeEnvelope(ballistic) && phase.AltitudeHitFactor(ballistic) > 0.9f, "the phased array reaches the highest shipped missile");
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.defName.StartsWith("DMSE_Incoming_") && d.GetModExtension<BVRTargetProps>() != null))
            {
                float altitude = def.GetModExtension<BVRTargetProps>().altitude;
                t.Check(phase.InAltitudeEnvelope(altitude), $"{def.defName} ({altitude} m) can be intercepted by something");
            }
            t.Check(!phase.ConfigErrors(PhaseArray).Any() && !early.ConfigErrors(EarlyFireControl).Any(), "shipped envelopes are valid");
            yield break;
        }

        public static IEnumerable<Wait> TrackScreenshots(TestContext t)
        {
            PlanetTile destination = TileAtDistance(24f, 30f);
            t.Require(destination.Valid, "a far tile exists");
            ScorerProjectile_WorldObject Launch(PlanetTile to, MissileBodyDef body)
            {
                // The fixture flies every player missile as the cruise incoming def, which is what supplies the altitude.
                ScorerProjectile_WorldObject wo = LaunchPlayerMissile(to, null, MissileConfig.NewWithDefaults(body));
                wo.skyfallerIncoming = body.incomingSkyfaller;
                return wo;
            }
            MissileWorldObject[] tracks =
            {
                Launch(destination, CruiseBody),
                Launch(TileAtDistance(19f, 22f), DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Sparrow")),
                Launch(TileAtDistance(14f, 17f), BallisticBody),
            };
            // Mid-flight, so the icons are off the ground and away from the launch point.
            yield return new WaitTicks(450);
            t.Note("altitudes: " + string.Join(", ", tracks.Select(x => MissileWorldMotion.AltitudeLabel(x.AltitudeAt(0)))));
            t.Check(tracks.Select(x => AirTrackDisplay.Band(x.AltitudeAt(0))).Distinct().Count() == 3, "three altitude bands are in the air");
            foreach (MissileWorldObject track in tracks)
            {
                float drawn = track.DrawPos.magnitude - track.PositionAt(0).magnitude;
                t.Check(UnityEngine.Mathf.Abs(drawn - AirTrackDisplay.CurrentLift(track)) < 0.01f, $"the world object draws at its altitude ({drawn:0.00} above the ground)");
            }
            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            CameraJumper.TryShowWorld();
            CameraJumper.TryJump(new GlobalTargetInfo(tracks[0]));
            Find.WorldSelector.Select(tracks[0]);
            yield return new WaitFrames(180);
            t.Screenshot("altitude_tracks");
            yield return new WaitFrames(3); // the shot is taken at the end of a later frame
            // The lift only reads away from the screen centre: the world camera looks straight down.
            CameraJumper.TryJump(new GlobalTargetInfo(TileAtDistance(11f, 13f)));
            yield return new WaitFrames(180);
            t.Screenshot("altitude_tracks_off_center");
            yield return new WaitFrames(3);
            CameraJumper.TryHideWorld();
            t.allowPause = false;
            foreach (MissileWorldObject track in tracks) { track.Destroy(); }
        }
    }
}
