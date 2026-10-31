using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>Map-layer BVR (drop pods, local SAM midcourse, CIWS terminal).</summary>
    public static class Tests_Map
    {
        public static IEnumerable<Wait> Environment(TestContext t)
        {
            t.Require(Enemy != null && Enemy.HostileTo(Player), "a hostile humanlike faction exists");
            foreach (string d in new[] { "DMSE_PhaseArrayRadar", "DMSE_AcousticEarlyWarningRadar", "DMSE_EarlyFireControlRadar",
                "DMSE_Building_LightMissileLauncher", "DMSE_Turret_Vulcan_WingBack", "DMSE_Missile_Grison",
                "DMSE_SkyFaller_Interceptor", "DMSE_Incoming_Moskit", "DMSE_LaserDesignatorMark",
                "DMSE_Bullet_LaserDesignator", "DMSE_Gun_LaserDesignator" })
            {
                t.Check(DefDatabase<ThingDef>.GetNamedSilentFail(d) != null, $"ThingDef {d} loaded");
            }
            t.Check(DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_Interceptor") != null, "WorldObjectDef DMSE_Interceptor loaded");
            t.Check(DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_MissileBase") != null, "WorldObjectDef DMSE_MissileBase loaded");
            t.Check(WorldComponent_BVRNetwork.Current != null, "WorldComponent_BVRNetwork present");
            t.Check(HomeBVR != null, "MapComponent_BVRCombat present on home map");
            // The first test can start on the first unpaused frame (TicksGame still 0); give the game one tick.
            int start = Find.TickManager.TicksGame;
            var waitTick = new WaitUntil(() => Find.TickManager.TicksGame > start, 600);
            yield return waitTick;
            t.Check(!waitTick.TimedOut && Find.TickManager.TicksGame > 0, "game is ticking");
            yield return WaitPowered(); yield return new WaitTicks(2);
        }

        public static IEnumerable<Wait> DeviceRegistrationAndActive(TestContext t)
        {
            Thing radar = SpawnBuilding(PhaseArray, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompSearchRadar sr = radar.TryGetComp<CompSearchRadar>();
            CompFireControlRadar fc = radar.TryGetComp<CompFireControlRadar>();
            t.Check(HomeBVR.searchRadars.Contains(sr), "search radar registered");
            t.Check(HomeBVR.fireControlRadars.Contains(fc), "fire-control radar registered");
            t.Check(sr.Active && fc.Active, "phased array is Active when powered and switched on");
            SetFlick(radar, false);
            yield return new WaitTicks(2);
            t.Check(!sr.Active && !fc.Active, "phased array is inactive when switched off");
            t.Check(HomeBVR.Channels.Capacity(Player) == 0, "switched-off radar gives no fire-control channels");
            SetFlick(radar, true);
            yield return WaitPowered();
            t.Check(HomeBVR.Channels.Capacity(Player) == fc.Props.maxTargets, $"channel capacity back to {fc.Props.maxTargets}");
            radar.Destroy(DestroyMode.Vanish);
            yield return new WaitTicks(2);
            t.Check(!HomeBVR.searchRadars.Contains(sr), "search radar unregistered after despawn");
        }

        public static IEnumerable<Wait> MannedFireControl(TestContext t)
        {
            Thing radar = SpawnBuilding(EarlyFireControl, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompFireControlRadar fc = radar.TryGetComp<CompFireControlRadar>();
            CompMannable man = radar.TryGetComp<CompMannable>();
            t.Require(man != null, "early fire-control radar has CompMannable");
            t.Check(!fc.Active, "unmanned early fire-control radar is inactive");
            Pawn colonist = Home.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            t.Require(colonist != null, "a colonist exists");
            man.ManForATick(colonist);
            t.Check(fc.Active, "manned early fire-control radar is active");
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(!fc.Active, "radar becomes inactive again once nobody mans it");
        }

        public static IEnumerable<Wait> DropPodWindowAndLanding(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            int expected = HomeBVR.ComputeWarningWindow(null, 1, Player);
            List<Pawn> pawns = DropEnemyPod(1);
            int registeredAt = Find.TickManager.TicksGame;
            yield return new WaitTicks(1);
            t.Require(HomeBVR.Waves.Count == 1 && HomeBVR.Waves[0].targets.Count == 1, "enemy pod captured into one BVR wave");
            BVRWave wave = HomeBVR.Waves[0];
            t.Check(Mathf.Abs(wave.tickToImpact - registeredAt - expected) <= 2,
                $"warning window {wave.tickToImpact - registeredAt} matches formula {expected}");
            t.Check(expected == 6400, $"phased array window for a drop pod is 6400 (got {expected})");
            t.Check(!Home.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming).Any(), "incoming pod is held off-map");
            t.Check(wave.defenderFaction == Player, "player is the defender");
            var waitLand = new WaitUntil(() => pawns[0].Spawned, expected + 1500);
            yield return waitLand;
            t.Check(!waitLand.TimedOut, "pod lands and the raider spawns when nothing intercepts it");
            t.Check(HomeBVR.Waves.Count == 0, "wave cleared after landing");
        }

        public static IEnumerable<Wait> MidcourseSamIntercept(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompMissileLauncher_Interceptor sam = launcher.TryGetComp<CompMissileLauncher_Interceptor>();
            t.Require(sam != null && sam.Active, "loaded light launcher is an active SAM");
            List<Pawn> pawns = DropEnemyPod(1);
            yield return new WaitTicks(1);
            t.Require(HomeBVR.Waves.Count == 1, "pod captured");
            BVRTarget target = HomeBVR.Waves[0].targets[0];

            var waitLock = new WaitUntil(() => target.lockUntil >= 0 || target.midcourseEngagedUntil > 0, 2000);
            yield return waitLock;
            t.Check(!waitLock.TimedOut, "fire-control radar locks the pod");

            IInterceptorShot shot = null;
            var waitShot = new WaitUntil(() => (shot = InterceptorShotAt(target.id)) != null, 2000);
            yield return waitShot;
            t.Require(!waitShot.TimedOut, "SAM launches an interceptor at the locked pod");
            t.Check(launcher.StoredCount == 3, $"one interceptor missile consumed (stored {launcher.StoredCount})");
            t.Check(sam.OnCooldown(Find.TickManager.TicksGame), "launcher enters reload cooldown");
            shot.HitChance = 1f;

            var waitKill = new WaitUntil(() => HomeBVR.Waves.Count == 0, 2000);
            yield return waitKill;
            t.Check(!waitKill.TimedOut, "midcourse hit removes the target");
            yield return new WaitTicks(60);
            t.Check(!pawns[0].Spawned, "raider never lands");
            t.Check(!Home.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
                .Any(c => c.InnerPawn == pawns[0]), "midcourse kill leaves no debris corpse");
        }

        public static IEnumerable<Wait> TerminalCiwsDebris(TestContext t)
        {
            SpawnBuilding(AcousticRadar, 2, 2);
            Thing ciws = SpawnBuilding(WingBack, 12, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompProperties_TerminalCIWS props = ForceWingBack(1f, 1f);
            CompRefuelable fuel = ciws.TryGetComp<CompRefuelable>();
            float fuelBefore = fuel.Fuel;
            int steelBefore = Home.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(s => s.stackCount);

            // A burst lasts ~40 ticks, shorter than one frame can advance here: watch it per tick and
            // pause the game the moment it starts so the screenshot catches it mid-burst.
            CompTerminalCIWS comp = ciws.TryGetComp<CompTerminalCIWS>();
            t.allowPause = true;
            TickWatch burst = AcceptanceDriver.Watch(() =>
            {
                bool firing = Field<int>(comp, "burstShotsLeft") > 0;
                if (firing) { Find.TickManager.CurTimeSpeed = TimeSpeed.Paused; }
                return firing;
            });

            List<Pawn> pawns = DropEnemyPod(2, withItem: true);
            yield return new WaitTicks(1);
            t.Require(HomeBVR.Waves.Count == 1 || burst.Seen, "pod captured by the acoustic radar");
            int registered = Find.TickManager.TicksGame;
            int window = HomeBVR.Waves.Count == 1 ? HomeBVR.Waves[0].tickToImpact - registered : 0;
            IntVec3 podLanding = HomeBVR.Waves.Count == 1 ? HomeBVR.Waves[0].targets[0].position : IntVec3.Invalid;
            t.Check(window <= props.terminalWindowTicks, $"acoustic window {window} is inside the CIWS terminal window");

            var waitBurst = new WaitUntil(() => burst.Seen, 600);
            yield return waitBurst;
            t.Check(!waitBurst.TimedOut, $"CIWS starts a burst right away (tick {burst.FirstTick}, captured {registered})");
            CameraJumper.TryJump(new GlobalTargetInfo(ciws));
            yield return new WaitFrames(30);
            t.Screenshot("ciws_burst");
            yield return new WaitFrames(3);
            t.allowPause = false;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;

            var waitKill = new WaitUntil(() => HomeBVR.Waves.Count == 0, window + 200);
            yield return waitKill;
            t.Check(!waitKill.TimedOut, "CIWS intercepts the pod in the terminal window");
            t.Check(Mathf.Abs(fuelBefore - fuel.Fuel - props.shotsPerBurst * props.fuelPerShot) < 0.01f,
                $"one burst used {props.shotsPerBurst * props.fuelPerShot} barrel durability (used {fuelBefore - fuel.Fuel})");
            t.Check(pawns.All(p => p.Dead), "all pawns in the pod are killed");
            // The corpse pod lands next to the predicted landing cell of the pod's debris (docs/攔截殘骸特效_實作與技術美術計畫.md §1-4).
            IntVec3 debrisCell = Home.GetComponent<MapComponent_InterceptEffects>().LastDebrisLandingCell;
            var waitCorpses = new WaitUntil(() => pawns.All(p => p.Corpse != null && p.Corpse.Spawned), 1200);
            yield return waitCorpses;
            t.Check(!waitCorpses.TimedOut, "both corpses land in one debris pod");
            if (t.Check(debrisCell.IsValid, $"the intercept predicted a debris landing cell ({debrisCell})"))
            {
                float far = pawns.Where(p => p.Corpse != null && p.Corpse.Spawned).Select(p => p.Corpse.Position.DistanceTo(debrisCell)).DefaultIfEmpty(999f).Max();
                t.Note($"debris cell {debrisCell}, pod landing cell {podLanding}, corpses up to {far:0.#} cells from the debris cell");
                // Near the debris cell, or (when it has no drop spot, e.g. inside a mountain) on the way back to the landing cell.
                float landingFar = podLanding.IsValid ? podLanding.DistanceTo(debrisCell) : 0f;
                t.Check(far <= 9f || far < landingFar, $"corpses land at the pod-debris cell or nearer to it than the landing cell ({far:0.#} vs {landingFar:0.#} cells)");
            }
            int steelAfter = Home.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(s => s.stackCount);
            t.Check(steelAfter <= steelBefore, "the pod's other cargo is destroyed with it");
        }

        public static IEnumerable<Wait> CiwsMultiWavePriority(TestContext t)
        {
            SpawnBuilding(AcousticRadar, 2, 2);
            Thing ciws = SpawnBuilding(WingBack, 12, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompProperties_TerminalCIWS props = ForceWingBack(1f, 0f);
            SetFlick(ciws, false);

            List<Pawn> podA = DropEnemyPod(1);
            yield return new WaitTicks(1);
            int idA = HomeBVR.Waves.SelectMany(w => w.targets).First().id;
            yield return new WaitTicks(700);
            List<Pawn> podB = DropEnemyPod(1, at: new IntVec3(Pad.maxX + 8, 0, Pad.CenterCell.z + 6));
            yield return new WaitTicks(1);
            t.Require(HomeBVR.Waves.Count == 2, "two separate waves (700 ticks apart, beyond the 600-tick merge window)");
            int idB = HomeBVR.Waves.SelectMany(w => w.targets).Select(x => x.id).First(id => id != idA);

            CompTerminalCIWS comp = ciws.TryGetComp<CompTerminalCIWS>();
            int firstTarget = -1;
            TickWatch burst = AcceptanceDriver.Watch(() =>
            {
                int id = Field<int>(comp, "burstTargetId");
                if (id >= 0 && firstTarget < 0) { firstTarget = id; }
                return id >= 0;
            });
            SetFlick(ciws, true);
            var waitBurst = new WaitUntil(() => burst.Seen, 400);
            yield return waitBurst;
            t.Require(!waitBurst.TimedOut, "CIWS starts a burst once powered");
            t.Check(firstTarget == idA, $"first burst targets the wave that impacts first (target {firstTarget}, A {idA}, B {idB})");

            int killedA = -1, killedB = -1;
            var waitBoth = new WaitUntil(() =>
            {
                var ids = HomeBVR.Waves.SelectMany(w => w.targets).Select(x => x.id).ToList();
                if (killedA < 0 && !ids.Contains(idA)) killedA = Find.TickManager.TicksGame;
                if (killedB < 0 && !ids.Contains(idB)) killedB = Find.TickManager.TicksGame;
                return killedA >= 0 && killedB >= 0;
            }, 2000);
            yield return waitBoth;
            t.Check(!waitBoth.TimedOut, "both waves are cleared");
            t.Check(killedA >= 0 && killedB >= 0 && killedA <= killedB, $"earlier wave cleared first (A {killedA}, B {killedB})");
            yield return new WaitTicks(30);
            t.Check(!podA[0].Spawned && !podB[0].Spawned, "neither raider lands");
        }

        public static IEnumerable<Wait> NoRadarLandsNormally(TestContext t)
        {
            yield return new WaitTicks(2);
            t.Require(!HomeBVR.searchRadars.Any(r => r.Active), "no active search radar on the map");
            List<Pawn> pawns = DropEnemyPod(1);
            yield return new WaitTicks(1);
            t.Check(HomeBVR.Waves.Count == 0, "pod is not captured without a radar");
            var waitLand = new WaitUntil(() => pawns[0].Spawned, 1200);
            yield return waitLand;
            t.Check(!waitLand.TimedOut, "pod lands normally");
        }

        public static IEnumerable<Wait> SharedChannels(TestContext t)
        {
            Thing radar = SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack sam = SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompFireControlRadar fc = radar.TryGetComp<CompFireControlRadar>();
            int capacity = HomeBVR.Channels.Capacity(Player);
            t.Require(capacity == 2, $"phased array provides 2 channels (got {capacity})");
            t.Require(HoldInReload(sam), "the SAM is armed but reloading");

            // Two pods far enough apart to form two waves; both lock (the SAM never becomes ready, so locks persist).
            DropEnemyPod(1);
            yield return new WaitTicks(650);
            DropEnemyPod(1, at: new IntVec3(Pad.maxX + 8, 0, Pad.CenterCell.z + 6));
            var waitLocks = new WaitUntil(() => HomeBVR.CountEngaged(Player) >= 2, 1500);
            yield return waitLocks;
            t.Check(!waitLocks.TimedOut, "both pods hold a channel");
            t.Check(HomeBVR.Channels.Free(Player) == 0, $"no free channel left (free {HomeBVR.Channels.Free(Player)})");
            t.Check(fc.FreeArtilleryChannels == 0, "artillery sees no free channel while BVR uses both");

            // A world missile cannot lock while the pool is full.
            PlanetTile source = TileAtDistance(14f, 16f);
            t.Require(source.Valid, "a tile 15 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);
            yield return new WaitTicks(120);
            t.Check(wo.State.detectedByFaction == Player, "world missile is detected");
            t.Check(wo.State.lockUntil < 0 && wo.State.interceptor == null, "world missile cannot lock while channels are full");
            t.Check(HomeBVR.Channels.World(Player) == 0, "no world channel booked");
        }
    }
}
