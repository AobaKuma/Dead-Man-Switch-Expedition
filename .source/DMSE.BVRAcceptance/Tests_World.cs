using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>World-layer BVR (tracks, detection, world interception, terminal handoff, NPC abstract defense).</summary>
    public static class Tests_World
    {
        private static Vector3 HomeCenter => MissileWorldUtility.TileCenter(Home.Tile);

        private static float DistanceToHome(MissileWorldObject wo)
            => MissileWorldUtility.SurfaceTilesBetween(HomeCenter, wo.PositionAt(0));

        public static IEnumerable<Wait> DetectionEtaAndLanding(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            PlanetTile source = TileAtDistance(29f, 31f);
            t.Require(source.Valid, "a tile ~30 tiles away exists");

            int lettersBefore = LetterCount("DMSE.BVR.World.DetectedLetter");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);
            int launchTick = Find.TickManager.TicksGame;
            int predictedArrival = launchTick + wo.TicksToArrival;
            float startDistance = DistanceToHome(wo);
            float expectedRange = MissileWorldMotion.DetectionRange(12f, 2, wo.TargetProps);
            t.Note($"start distance {startDistance:0.00} tiles, predicted flight {predictedArrival - launchTick} ticks");
            t.Check(Mathf.Abs(expectedRange - 22.5f) < 0.01f, $"phased array range vs cruise missile is 22.5 tiles (got {expectedRange:0.00})");
            t.Check(Mathf.Abs((predictedArrival - launchTick) - startDistance * 150f) < 30f,
                "flight time follows 150 ticks per tile at speed 1");

            yield return new WaitTicks(20);
            // Datalink / settlement nodes may see it first; the player's ring must still be outside it.
            t.Check(wo.State.detectedByFaction != Player, "track outside the coverage ring is not detected by the player");

            float detectedAt = -1f;
            var waitDetect = new WaitUntil(() =>
            {
                if (wo.State.detectedByFaction == Player) { detectedAt = DistanceToHome(wo); return true; }
                return wo.Destroyed;
            }, 6000);
            yield return waitDetect;
            t.Require(detectedAt > 0f, "track becomes detected");
            t.Check(detectedAt <= expectedRange + 0.05f && detectedAt >= expectedRange - 0.6f,
                $"detected at {detectedAt:0.00} tiles (ring {expectedRange:0.00})");
            t.Check(LetterCount("DMSE.BVR.World.DetectedLetter") == lettersBefore + 1, "one detection letter");
            t.Check(wo.ExpandingIconColor == Color.red, "detected enemy track is drawn red");
            t.Check(new Alert_WorldIncoming().GetReport().active, "world incoming alert is active");

            Find.WorldSelector.ClearSelection();
            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            CameraJumper.TryShowWorld();
            CameraJumper.TryJump(new GlobalTargetInfo(wo));
            Find.WorldSelector.Select(Home.Parent);
            yield return new WaitFrames(180);
            t.Screenshot("world_detected_track");
            yield return new WaitFrames(3);

            // Coverage ring: drawn for the selected colony, or for every player node with "always show" on.
            System.Collections.IDictionary rings = CoverageRings;
            t.Check(rings != null && rings.Count > 0, "the selected colony draws its coverage ring");
            bool oldShow = ShowAllCoverage;
            Override(() => ShowAllCoverage = false, () => ShowAllCoverage = oldShow);
            Find.WorldSelector.ClearSelection();
            rings?.Clear();
            yield return new WaitFrames(10);
            t.Check(rings != null && rings.Count == 0, "no ring with nothing selected and always-show off");
            ShowAllCoverage = true;
            yield return new WaitFrames(10);
            t.Check(rings != null && rings.Count > 0, "always-show draws the player's ring with nothing selected");
            t.Screenshot("world_always_show_coverage");
            yield return new WaitFrames(3);
            ShowAllCoverage = false;
            CameraJumper.TryHideWorld();
            t.allowPause = false;
            yield return new WaitTicks(300);
            t.Check(LetterCount("DMSE.BVR.World.DetectedLetter") == lettersBefore + 1, "detection letter is not repeated");

            TickWatch arrived = AcceptanceDriver.Watch(() => wo.Destroyed);
            var waitArrive = new WaitUntil(() => arrived.Seen, predictedArrival - Find.TickManager.TicksGame + 300);
            yield return waitArrive;
            int arrival = arrived.FirstTick;
            t.Require(arrival > 0, "track arrives");
            t.Check(Mathf.Abs(arrival - predictedArrival) <= 15, $"arrival {arrival} within 15 ticks of predicted {predictedArrival} (map view)");
            bool incoming = Home.listerThings.ThingsOfDef(CruiseIncoming).Any();
            t.Check(incoming, "arrived missile becomes an incoming skyfaller on the home map");
            t.Check(HomeBVR.Waves.Count == 0, "without CIWS the arrival is not held for terminal defense");
            Projectile_MissileIncoming mi = Home.listerThings.ThingsOfDef(CruiseIncoming).OfType<Projectile_MissileIncoming>().FirstOrDefault();
            t.Check(mi != null && mi.HasEntryHeading,
                $"the arrival flies in from the map edge along the track's last leg (heading {(mi != null && mi.HasEntryHeading ? mi.EntryHeading.ToString("0.#") : "none")})");
            if (mi != null)
            {
                t.Check(mi.worldPhaseDone, "incoming is marked worldPhaseDone");
                t.Check(mi.Position.DistanceTo(SafeImpactCell) <= 1.5f, "laser seeker steered the impact to the designated cell");
            }
        }

        public static IEnumerable<Wait> WorldInterceptHit(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompMissileLauncher_Interceptor sam = launcher.TryGetComp<CompMissileLauncher_Interceptor>();
            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);

            var waitLock = new WaitUntil(() => wo.State.lockUntil >= 0 || wo.State.interceptor != null, 600);
            yield return waitLock;
            t.Require(!waitLock.TimedOut, "fire control locks the world track");
            if (wo.State.lockUntil >= 0)
            {
                t.Check(HomeBVR.Channels.World(Player) == 1, "the lock books one world channel");
                t.Check(HomeBVR.Channels.Free(Player) == 1, "one channel left");
            }

            var waitLaunch = new WaitUntil(() => wo.State.interceptor != null, 1500);
            yield return waitLaunch;
            t.Require(!waitLaunch.TimedOut, "a world interceptor is launched");
            WorldObject_Interceptor interceptor = wo.State.interceptor;
            int now = Find.TickManager.TicksGame;
            t.Check(launcher.StoredCount == 3, $"one real missile consumed (stored {launcher.StoredCount})");
            t.Check(sam.OnCooldown(now), "launcher enters reload cooldown");
            t.Check(interceptor.Faction == Player, "interceptor belongs to the player");
            t.Check(interceptor.interceptTick > now && interceptor.interceptTick < now + wo.TicksToArrival,
                "meeting happens before the missile arrives");
            t.Check(InterceptorShotAt(-1) != null, "visual-only interceptor launch on the map");
            t.Check(HomeBVR.Channels.World(Player) == 1, "the in-flight interceptor keeps its channel");
            t.Note($"hit chance {interceptor.hitChance:0.00}, meeting in {interceptor.interceptTick - now} ticks");
            interceptor.hitChance = 1f;
            int due = interceptor.interceptTick;
            TickWatch killed = AcceptanceDriver.Watch(() => wo.Destroyed);

            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            CameraJumper.TryShowWorld();
            CameraJumper.TryJump(new GlobalTargetInfo(interceptor));
            Find.WorldSelector.Select(Home.Parent);
            yield return new WaitFrames(180);
            t.Screenshot("world_interceptor_in_flight");
            yield return new WaitFrames(3);
            CameraJumper.TryHideWorld();
            t.allowPause = false;

            int flashesBefore = WorldFlashes?.Count ?? -1;
            var waitKill = new WaitUntil(() => killed.Seen, due - Find.TickManager.TicksGame + 60);
            yield return waitKill;
            int destroyedAt = killed.FirstTick;
            t.Require(destroyedAt > 0, "track destroyed by the interceptor");
            t.Check(flashesBefore >= 0 && WorldFlashes.Count > flashesBefore, "the kill adds a flash on the world map");
            t.Check(destroyedAt - due >= 0 && destroyedAt - due <= 1, $"resolved at the meeting tick ({destroyedAt} vs {due})");
            t.Check(interceptor.Destroyed, "interceptor removed after resolving");
            yield return new WaitTicks(30);
            t.Check(!Home.listerThings.ThingsOfDef(CruiseIncoming).Any(), "intercepted missile never reaches the map");
            t.Check(HomeBVR.Channels.World(Player) == 0, "world channel released");
        }

        public static IEnumerable<Wait> WorldInterceptMissThenRelock(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            PlanetTile source = TileAtDistance(21f, 22f);
            t.Require(source.Valid, "a tile ~21 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);

            var waitFirst = new WaitUntil(() => wo.State.interceptor != null, 2000);
            yield return waitFirst;
            t.Require(!waitFirst.TimedOut, "first interceptor launched");
            WorldObject_Interceptor first = wo.State.interceptor;
            first.hitChance = 0f;
            var waitResolve = new WaitUntil(() => first.Destroyed, first.interceptTick - Find.TickManager.TicksGame + 60);
            yield return waitResolve;
            t.Check(!wo.Destroyed, "missed missile keeps flying");
            t.Check(wo.State.interceptAttempts == 1, "attempt counted");

            var waitSecond = new WaitUntil(() => wo.State.interceptor != null && wo.State.interceptor != first, 2000);
            yield return waitSecond;
            t.Require(!waitSecond.TimedOut, "fire control re-locks and launches again while time remains");
            t.Check(wo.State.interceptAttempts == 2, "second attempt counted");
            t.Check(launcher.StoredCount == 2, $"two missiles consumed in total (stored {launcher.StoredCount})");
            wo.State.interceptor.hitChance = 1f;
            var waitKill = new WaitUntil(() => wo.Destroyed, 2000);
            yield return waitKill;
            t.Check(!waitKill.TimedOut, "second interceptor destroys the missile");
        }

        public static IEnumerable<Wait> TerminalHandoffWithCiws(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(10, 2, 4);
            Thing ciws = SpawnBuilding(WingBack, 14, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompProperties_TerminalCIWS props = WingBack.GetCompProperties<CompProperties_TerminalCIWS>();
            float oldChance = props.interceptChance, oldDebris = props.debrisChance;
            Override(() => { props.interceptChance = 1f; props.debrisChance = 0f; },
                () => { props.interceptChance = oldChance; props.debrisChance = oldDebris; });

            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);

            int arrival = -1;
            var waitArrive = new WaitUntil(() =>
            {
                WorldObject_Interceptor i = wo.Destroyed ? null : wo.State.interceptor;
                if (i != null && i.hitChance > 0f) { i.hitChance = 0f; }   // force every world shot to miss
                if (wo.Destroyed) { arrival = Find.TickManager.TicksGame; return true; }
                return false;
            }, 5000);
            yield return waitArrive;
            t.Require(arrival > 0, "missile survives the world layer and arrives");
            int storedAtArrival = launcher.StoredCount;
            t.Require(HomeBVR.Waves.Count == 1, "arrival is held in exactly one BVR wave");
            BVRWave wave = HomeBVR.Waves[0];
            t.Check(wave.TerminalOnly, "wave is terminal-only");
            int window = wave.tickToImpact - arrival;
            t.Check(Mathf.Abs(window - props.terminalWindowTicks) <= 20, $"terminal window {window} ≈ {props.terminalWindowTicks}");

            var waitDone = new WaitUntil(() => HomeBVR.Waves.Count == 0, window + 300);
            yield return waitDone;
            t.Check(!waitDone.TimedOut, "terminal wave resolves");
            t.Check(launcher.StoredCount == storedAtArrival, "SAM does not fire again during the terminal phase");
            yield return new WaitTicks(30);
            t.Check(!Home.listerThings.ThingsOfDef(CruiseIncoming).Any(), "CIWS destroyed the missile (no impact)");
            t.Check(ciws.TryGetComp<CompRefuelable>().Fuel < ciws.TryGetComp<CompRefuelable>().Props.fuelCapacity, "CIWS spent barrel durability");
        }

        public static IEnumerable<Wait> RadarLossDuringEngagement(TestContext t)
        {
            Thing radar = SpawnBuilding(PhaseArray, 2, 2);
            SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            PlanetTile source = TileAtDistance(19f, 21f);
            t.Require(source.Valid, "a tile ~20 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);
            var waitLaunch = new WaitUntil(() => wo.State.interceptor != null, 2000);
            yield return waitLaunch;
            t.Require(!waitLaunch.TimedOut, "interceptor launched");
            WorldObject_Interceptor interceptor = wo.State.interceptor;
            interceptor.hitChance = 1f;
            int due = interceptor.interceptTick;

            SetFlick(radar, false);
            yield return new WaitTicks(20);
            // With datalink / settlement defenses another faction may still see it, but the player no longer does.
            t.Check(wo.State.detectedByFaction != Faction.OfPlayer, "losing every player search radar clears player detection");
            t.Check(wo.State.lockUntil < 0, "no lock without fire control");
            TickWatch killed = AcceptanceDriver.Watch(() => wo.Destroyed);
            var waitKill = new WaitUntil(() => killed.Seen, due - Find.TickManager.TicksGame + 60);
            yield return waitKill;
            int destroyedAt = killed.FirstTick;
            t.Check(destroyedAt > 0 && destroyedAt - due >= 0 && destroyedAt - due <= 1,
                $"the interceptor already in flight still resolves on schedule ({destroyedAt} vs {due})");
        }

        public static IEnumerable<Wait> NpcAbstractDefenseAndSeeker(TestContext t)
        {
            Site site = SpawnMissileBase(8f, 14f);
            t.Require(site != null, "an NPC missile base spawned 8–14 tiles away");
            WorldObjectComp_AirDefense defense = site.GetComponent<WorldObjectComp_AirDefense>();
            t.Require(defense != null, "missile base has abstract air defense");
            yield return new WaitTicks(20);
            t.Check(defense.Available, "abstract defense active while the base has no map");
            int stockBefore = defense.Stock;
            t.Check(stockBefore == defense.Props.interceptorStock, $"full stock {stockBefore}");

            ScorerProjectile_WorldObject wo = LaunchPlayerMissile(site.Tile, site, CruiseConfig("DMSE_Guidance_EMSignal"));
            var waitLaunch = new WaitUntil(() => wo.Destroyed || wo.State.interceptor != null, 3000);
            yield return waitLaunch;
            t.Require(!wo.Destroyed && wo.State.interceptor != null, "the NPC base launches an interceptor at the player missile");
            t.Check(wo.State.detectedByFaction == Enemy, "detected by the enemy faction");
            t.Check(wo.State.interceptor.Faction == Enemy, "interceptor belongs to the base's faction");
            t.Check(defense.Stock == stockBefore - 1, $"abstract stock consumed ({stockBefore} → {defense.Stock})");
            t.Check(new Alerts_PlayerMissiles().GetReport().active, "player-missile alert shows the tracked missile");

            var waitArrive = new WaitUntil(() =>
            {
                WorldObject_Interceptor i = wo.Destroyed ? null : wo.State.interceptor;
                if (i != null && i.hitChance > 0f) { i.hitChance = 0f; }
                return wo.Destroyed;
            }, 4000);
            yield return waitArrive;
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Require(site.HasMap, "arrival generates the base map");
            Map baseMap = site.Map;
            t.Check(!defense.Available, "abstract defense hands over once the map exists");
            t.Check(!Find.Targeter.IsTargeting, "non-TV seeker does not ask the player for a target");
            MapComponent_BVRCombat baseBvr = baseMap.GetComponent<MapComponent_BVRCombat>();
            IntVec3 expected = GuidanceType.HighestPowerDrawTarget(baseMap, Player);
            List<IntVec3> landing = baseBvr.Waves.SelectMany(w => w.targets).Select(x => x.position)
                .Concat(baseMap.listerThings.ThingsOfDef(CruiseIncoming).Select(x => x.Position)).ToList();
            t.Check(landing.Count > 0, "the missile is on the base map (falling or held for terminal defense)");
            // The map was generated at the arrival tick, before its power net switched anything on (see S3).
            Thing radar = baseMap.listerThings.ThingsOfDef(PhaseArray).FirstOrDefault();
            t.Check(radar != null && expected == radar.Position, $"the phased array is the biggest power draw on the base ({expected})");
            t.Check(landing.Contains(expected), $"EM seeker locked it on arrival (landing {string.Join(",", landing)})");

            Current.Game.DeinitAndRemoveMap(baseMap, false);
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(defense.Available, "abstract defense resumes after the map is removed");
        }

        public static IEnumerable<Wait> TvGuidanceManualTarget(TestContext t)
        {
            Site site = SpawnMissileBase(8f, 14f);
            t.Require(site != null, "an NPC missile base spawned");
            yield return WaitPowered(); yield return new WaitTicks(2);
            ScorerProjectile_WorldObject wo = LaunchPlayerMissile(site.Tile, site, CruiseConfig("DMSE_Guidance_Precision"));
            t.Check(wo.config.PlayerChoosesTarget, "TV-guided config lets the player choose");

            var waitNear = new WaitUntil(() =>
            {
                WorldObject_Interceptor i = wo.Destroyed ? null : wo.State.interceptor;
                if (i != null && i.hitChance > 0f) { i.hitChance = 0f; }
                return wo.Destroyed || wo.TicksToArrival < 400;
            }, 4000);
            yield return waitNear;
            t.Require(!wo.Destroyed, "missile still flying just before arrival");
            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            var waitArrive = new WaitUntil(() =>
            {
                WorldObject_Interceptor i = wo.Destroyed ? null : wo.State.interceptor;
                if (i != null && i.hitChance > 0f) { i.hitChance = 0f; }
                return wo.Destroyed;
            }, 800);
            yield return waitArrive;
            yield return new WaitFrames(5);
            t.Check(site.HasMap, "arrival generated the base map");
            t.Check(Find.TickManager.Paused, "game pauses for the TV operator");
            t.Check(Find.Targeter.IsTargeting, "targeter is open for choosing the impact point");
            t.Screenshot("tv_manual_target");
            Find.Targeter.StopTargeting();
            yield return new WaitFrames(3);
            t.allowPause = false;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            yield return new WaitTicks(2);
            Map baseMap = site.Map;
            bool onMap = baseMap.GetComponent<MapComponent_BVRCombat>().Waves.Any()
                || baseMap.listerThings.ThingsOfDef(CruiseIncoming).Any();
            t.Check(onMap, "cancelling the targeter drops the missile at the map center");
        }
    }
}
