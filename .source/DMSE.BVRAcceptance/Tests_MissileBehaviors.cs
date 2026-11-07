using System.Collections.Generic;
using System.Linq;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>Missile behavior framework (.source/DMSE/MissileFramework): hooks run on real tracks, seekers and detonations.</summary>
    public static class Tests_MissileBehaviors
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("MB1", "A flight profile changes speed and altitude, and the ETA predicts the real arrival", FlightProfile),
            new AcceptanceTest("MB2", "Children release mid-flight from the parent's position; a self-releasing body stops at the depth limit", ReleaseChildren),
            new AcceptanceTest("MB3", "Lifecycle, seek and detonation hooks fire with their context", Hooks),
            new AcceptanceTest("MB4", "Behavior state and manoeuvres survive save and load", SaveLoad),
        };

        // Bodies are shared defs; each test puts its behaviors on the body and the runner's next test sees them cleared.
        private static void SetBehaviors(MissileBodyDef body, params MissileBehaviorProps[] behaviors)
            => body.behaviors = behaviors.Length == 0 ? null : behaviors.ToList();

        private static List<MissileWorldObject> LiveTracks
            => Find.WorldObjects.AllWorldObjects.OfType<MissileWorldObject>().Where(x => !x.Destroyed).ToList();

        public static IEnumerable<Wait> FlightProfile(TestContext t)
        {
            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            WorldObject_IncomingMissile baseline = LaunchEnemyMissile(source);
            SetBehaviors(CruiseBody, new MissileBehaviorProps_FlightProfile
            {
                phases = new List<MissileFlightPhase>
                {
                    new MissileFlightPhase { untilProgress = 0.4f, speedFactor = 2f, altitude = 30000f },
                    new MissileFlightPhase { untilProgress = 1f, speedFactor = 1f, altitude = 50f, evasiveEntry = true },
                },
            });
            try
            {
                WorldObject_IncomingMissile profiled = LaunchEnemyMissile(source);
                t.Check(profiled.VariableSpeed && !baseline.VariableSpeed, "only the profiled missile has variable speed");
                int baselineEta = baseline.TicksToArrival;
                int eta = profiled.TicksToArrival;
                t.Note($"ETA baseline {baselineEta}, profiled {eta}");
                t.Check(System.Math.Abs(eta - baselineEta * 0.8f) < baselineEta * 0.03f, "double speed over 40% of the route takes 20% off the ETA");
                t.Check(profiled.TargetProps.altitude == 30000f, "the first phase sets the high altitude");
                int predictedArrival = Find.TickManager.TicksGame + eta;
                int revision = profiled.RouteRevision;

                var waitPhase = new WaitUntil(() => profiled.Destroyed || profiled.RouteProgress >= 0.45f, eta);
                yield return waitPhase;
                t.Require(!profiled.Destroyed, "the missile reaches its second phase");
                t.Check(profiled.TargetProps.altitude == 50f, "the second phase sets the low altitude");
                t.Check(profiled.RouteRevision == revision + 1, "an evasive phase entry counts as a manoeuvre");
                int midEta = profiled.TicksToArrival;
                t.Check(System.Math.Abs(Find.TickManager.TicksGame + midEta - predictedArrival) <= 90, "the mid-flight ETA agrees with the launch prediction");

                var waitArrival = new WaitUntil(() => profiled.Destroyed, midEta + 300);
                yield return waitArrival;
                t.Check(!waitArrival.TimedOut, "the profiled missile arrives");
                int error = Find.TickManager.TicksGame - predictedArrival;
                t.Note($"arrival error {error} ticks");
                t.Check(System.Math.Abs(error) <= 90, "the launch ETA predicts the real arrival");
            }
            finally { SetBehaviors(CruiseBody); }
        }

        public static IEnumerable<Wait> ReleaseChildren(TestContext t)
        {
            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            int before = LiveTracks.Count;
            SetBehaviors(CruiseBody, new MissileBehaviorProps_ReleaseChildren { atProgress = 0.05f, count = 3, retireParent = true, childBody = BallisticBody });
            try
            {
                WorldObject_IncomingMissile bus = LaunchEnemyMissile(source);
                PlanetTile destination = bus.Destination;
                var waitRelease = new WaitUntil(() => bus.Destroyed, bus.TicksToArrival);
                yield return waitRelease;
                t.Require(!waitRelease.TimedOut, "the bus is retired after the release");
                List<MissileWorldObject> children = LiveTracks.Where(x => x.config?.body == BallisticBody).ToList();
                foreach (MissileWorldObject child in children) { Track(child); }
                t.Check(children.Count == 3, $"three children fly on (saw {children.Count})");
                t.Check(children.All(c => c.Destination == destination && c.Attacker == Enemy && c.targetParent == Home.Parent), "children keep destination, faction and target");
                t.Check(children.All(c => c.RouteProgress > 0.03f && c.RouteProgress < 0.2f), "children continue from the release point, not from the launch site");
                t.Check(children.All(c => c.State.interceptor == null && c.TicksToArrival > 0), "children are independent tracks");
                foreach (MissileWorldObject child in children) { child.Destroy(); }
            }
            finally { SetBehaviors(CruiseBody); }

            // The parent's own body as the child would recurse forever without the depth limit.
            int live = LiveTracks.Count;
            SetBehaviors(CruiseBody, new MissileBehaviorProps_ReleaseChildren { atProgress = 0.01f, count = 1 });
            try
            {
                WorldObject_IncomingMissile root = LaunchEnemyMissile(source);
                yield return new WaitTicks(root.TicksToArrival / 3);
                List<MissileWorldObject> family = LiveTracks.Skip(live).ToList();
                foreach (MissileWorldObject m in family) { Track(m); }
                t.Check(family.Count == 4, $"a self-releasing body stops at parent plus 3 generations (saw {family.Count})");
                foreach (MissileWorldObject m in family) { m.Destroy(); }
            }
            finally { SetBehaviors(CruiseBody); }
        }

        private class RecordingProps : MissileBehaviorProps
        {
            public IntVec3 forcedCell = IntVec3.Invalid;
            public MissileSeekContext lastSeek;
            public MissileImpactContext lastImpact;
            public bool consumeArrival;
            public RecordingProps() { behaviorClass = typeof(RecordingBehavior); }
            public override void ModifySeekResult(MissileSeekContext context, ref IntVec3 cell)
            {
                lastSeek = context;
                if (forcedCell.IsValid) { cell = forcedCell; }
            }
            public override void PostDetonate(MissileImpactContext context) => lastImpact = context;
        }

        private class RecordingBehavior : MissileBehavior
        {
            public static int launches, ticks, intercepted, arrivals;
            public static void Reset() { launches = ticks = intercepted = arrivals = 0; }
            public override void PostLaunch() => launches++;
            public override void FlightTick(int elapsedTicks) => ticks += elapsedTicks;
            public override void OnIntercepted(Faction by) => intercepted++;
            public override bool PreArrive() { arrivals++; return ((RecordingProps)props).consumeArrival; }
        }

        public static IEnumerable<Wait> Hooks(TestContext t)
        {
            PlanetTile source = TileAtDistance(5f, 7f);
            t.Require(source.Valid, "a tile ~6 tiles away exists");
            RecordingBehavior.Reset();
            RecordingProps props = new RecordingProps { consumeArrival = true };
            SetBehaviors(CruiseBody, props);
            try
            {
                WorldObject_IncomingMissile shot = LaunchEnemyMissile(source);
                t.Check(RecordingBehavior.launches == 1, "PostLaunch runs once");
                yield return new WaitTicks(120);
                t.Check(RecordingBehavior.ticks > 0, "FlightTick receives the elapsed time");
                shot.Notify_Intercepted(Player);
                t.Check(RecordingBehavior.intercepted == 1 && shot.Destroyed, "OnIntercepted runs before the track dies");

                int things = Home.listerThings.AllThings.Count(x => x.def == CruiseIncoming);
                WorldObject_IncomingMissile consumed = LaunchEnemyMissile(source);
                var waitArrival = new WaitUntil(() => consumed.Destroyed, consumed.TicksToArrival + 120);
                yield return waitArrival;
                t.Check(!waitArrival.TimedOut && RecordingBehavior.arrivals == 1, "PreArrive runs on arrival");
                t.Check(Home.listerThings.AllThings.Count(x => x.def == CruiseIncoming) == things, "a consumed arrival spawns nothing on the map");

                // Seek: the context reaches the behavior, and the behavior can overrule the seeker.
                IntVec3 forced = Home.Center + new IntVec3(7, 0, 7);
                props.forcedCell = forced;
                MissileConfig config = CruiseConfig("DMSE_Guidance_ActiveRadar");
                IntVec3 picked = GuidanceType.ArrivalCell(config, Home, Enemy, out bool loiter, approachHeading: 123f);
                t.Check(picked == forced && !loiter, "a behavior overrules the seeker's cell");
                t.Check(props.lastSeek != null && props.lastSeek.Map == Home && props.lastSeek.Attacker == Enemy
                    && props.lastSeek.ApproachHeading == 123f && props.lastSeek.ClaimKey != null && props.lastSeek.Loadout == config,
                    "the seek context carries map, attacker, heading, claim and loadout");
                props.forcedCell = IntVec3.Invalid;

                // Detonate: same for the impact context.
                IntVec3 ground = Home.Center + new IntVec3(40, 0, 40);
                MissileImpactUtility.Detonate(config, ground, Home, Enemy, applyScatter: false, approachHeading: 45f);
                t.Check(props.lastImpact != null && props.lastImpact.Center == ground && props.lastImpact.ApproachHeading == 45f
                    && props.lastImpact.Attacker == Enemy && props.lastImpact.Loadout == config, "the impact context carries center, heading, attacker and loadout");
            }
            finally { SetBehaviors(CruiseBody); }
        }

        public static IEnumerable<Wait> SaveLoad(TestContext t)
        {
            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            SetBehaviors(CruiseBody, new MissileBehaviorProps_ReleaseChildren { atProgress = 0.02f, count = 1, childBody = BallisticBody });
            WorldObject_IncomingMissile parent = LaunchEnemyMissile(source);
            var waitRelease = new WaitUntil(() => LiveTracks.Any(x => x.config?.body == BallisticBody), parent.TicksToArrival);
            yield return waitRelease;
            t.Require(!waitRelease.TimedOut, "the child is released before saving");
            int parentId = parent.ID;
            parent.NotifyManoeuvre();
            int revision = parent.RouteRevision;
            RememberEnemyForReload();

            Game before = Current.Game;
            GameDataSaveLoader.SaveGame(TestSaveName);
            yield return new WaitFrames(10);
            GameDataSaveLoader.LoadGame(TestSaveName);
            var waitReload = new WaitUntil(() => Current.Game != before && Current.ProgramState == ProgramState.Playing, 999999);
            yield return waitReload;
            yield return new WaitFrames(5);

            MissileWorldObject restored = LiveTracks.FirstOrDefault(x => x.ID == parentId);
            t.Require(restored != null, "the parent is restored");
            foreach (MissileWorldObject m in LiveTracks) { Track(m); }
            t.Check(restored.Behaviors.Count == 1, "behaviors are rebuilt from the loadout");
            t.Check(restored.RouteRevision == revision, "the manoeuvre count is restored");
            int children = LiveTracks.Count(x => x.config?.body == BallisticBody);
            t.Check(children == 1, $"the child is restored (saw {children})");
            yield return new WaitTicks(120);
            t.Check(LiveTracks.Count(x => x.config?.body == BallisticBody) == children, "the restored 'released' flag prevents a second release");
            SetBehaviors(CruiseBody);
            foreach (MissileWorldObject m in LiveTracks) { m.Destroy(); }
        }
    }
}
