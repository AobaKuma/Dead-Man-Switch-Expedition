using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>Seeker target selection, laser designation, save/load.</summary>
    public static class Tests_Seekers
    {
        private static IntVec3 Pick(string guidance)
        {
            MissileConfig cfg = CruiseConfig(guidance);
            return GuidanceType.PickCell(cfg, Home, Enemy);
        }

        public static IEnumerable<Wait> SeekerTargetSelection(TestContext t)
        {
            Thing phased = SpawnBuilding(PhaseArray, 2, 2);
            Thing acoustic = SpawnBuilding(AcousticRadar, 8, 2);
            Thing earlyFc = SpawnBuilding(EarlyFireControl, 16, 2);
            SpawnLoadedLauncher(22, 2, 1);
            SpawnBuilding(WingBack, 26, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);

            t.Check(Pick("DMSE_Light_Guidance_AntiRadiation") == phased.Position,
                "anti-radiation locks the active radar with the highest power level (phased array)");
            t.Check(Pick("DMSE_Guidance_EMSignal") == phased.Position, "EM seeker locks the building drawing the most power (phased array, 5 kW)");

            SetFlick(phased, false);
            yield return new WaitTicks(3);
            IntVec3 arFallback = Pick("DMSE_Light_Guidance_AntiRadiation");
            t.Check(arFallback != acoustic.Position, "immune acoustic array is never picked by anti-radiation");
            IntVec3 emExpected = GuidanceType.HighestPowerDrawTarget(Home, Enemy);
            t.Check(arFallback == emExpected, $"with no eligible radar, anti-radiation falls back to EM ({emExpected})");
            t.Check(emExpected == earlyFc.Position, "unmanned but powered early FC radar is now the biggest power draw");
            SetFlick(phased, true);
            yield return new WaitTicks(3);

            // Heat: hottest room only if it clears the outdoor threshold, else the biggest creature.
            float outdoor = Home.mapTemperature.OutdoorTemp;
            Room hotRoom = Home.regionGrid.AllRooms
                .Where(r => !r.PsychologicallyOutdoors && !r.UsesOutdoorTemperature && r.Temperature >= outdoor + 15f)
                .OrderByDescending(r => r.Temperature).FirstOrDefault();
            IntVec3 heat = Pick("DMSE_Light_Guidance_IR");
            if (hotRoom != null)
            {
                t.Check(hotRoom.Cells.Contains(heat), $"heat seeker hits the hot room ({hotRoom.Temperature:0}°C vs outside {outdoor:0}°C)");
            }
            else
            {
                List<Pawn> eligible = Home.mapPawns.AllPawnsSpawned
                    .Where(p => !p.Dead && (p.Faction == null || p.Faction.HostileTo(Enemy))).ToList();
                float maxSize = eligible.Max(p => p.BodySize);
                t.Check(eligible.Any(p => p.BodySize == maxSize && p.Position == heat),
                    $"no room hot enough → heat seeker hits the biggest creature (size {maxSize})");
            }

            // Laser: only the attacker's newest valid mark.
            IntVec3 a = new IntVec3(Pad.minX + 30, 0, Pad.minZ + 30);
            IntVec3 b = new IntVec3(Pad.minX + 34, 0, Pad.minZ + 30);
            LaserDesignatorMark markA = LaserDesignatorUtility.PlaceMark(a, Home, Enemy, null, 5000);
            Track(markA);
            yield return new WaitTicks(2);
            t.Check(Pick("DMSE_Guidance_Laser") == a, "laser seeker locks the attacker's newest mark");
            LaserDesignatorMark markB = LaserDesignatorUtility.PlaceMark(b, Home, Player, null, 5000);
            Track(markB);
            yield return new WaitTicks(2);
            t.Check(Pick("DMSE_Guidance_Laser") == a, "another faction's newer mark is ignored");

            // Inertial and TV (enemy) pick a hostile building or pawn.
            HashSet<IntVec3> hostile = new HashSet<IntVec3>(GuidanceType.HostileBuildings(Home, Enemy).Select(x => x.Position));
            t.Check(hostile.Contains(Pick("DMSE_Guidance_Inertial")), "inertial hits a hostile building");
            t.Check(hostile.Contains(Pick("DMSE_Guidance_Precision")), "enemy TV missile falls back to inertial");

            // Who chooses the target.
            t.Check(CruiseConfig("DMSE_Guidance_Precision").PlayerChoosesTarget, "TV guidance: player chooses");
            t.Check(!CruiseConfig("DMSE_Guidance_EMSignal").PlayerChoosesTarget, "EM seeker: automatic");
            t.Check(!CruiseConfig("DMSE_Guidance_Inertial").PlayerChoosesTarget, "inertial: automatic");
            MissileConfig loiter = new MissileConfig(DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Loiter"));
            t.Check(loiter.PlayerChoosesTarget, "loitering munition (built-in TV) lets the player choose");
            t.Check(!loiter.HasTerminalGuidance, "built-in TV gives no terminal guidance");
            t.Check(CruiseConfig("DMSE_Guidance_Precision").HasTerminalGuidance, "TV part still provides terminal guidance");
        }

        /// <summary>
        /// A missile arriving at a base without a map generates it and picks the impact cell on that same tick,
        /// before the power net has switched any consumer on (vanilla does that every 30–200 ticks).
        /// </summary>
        public static IEnumerable<Wait> SeekersOnFreshMap(TestContext t)
        {
            Site site = SpawnMissileBase(8f, 14f);
            t.Require(site != null, "an NPC missile base spawned");
            Map map = MissileWorldUtility.ResolveArrivalMap(site.Tile, site, true);   // the call a missile arrival makes
            t.Require(map != null, "the base map is generated");
            // OnArrived keeps the map for two days (Patch_ShouldBeRemoved); without it vanilla removes the empty site map.
            GameComponent_MissileEngage.Comp.times.SetOrAdd(map, Find.TickManager.TicksGame + GenDate.TicksPerDay * 2);
            Thing radar = map.listerThings.ThingsOfDef(PhaseArray).FirstOrDefault();
            t.Require(radar != null, "the base has a phased array radar");
            CompPowerTrader power = radar.TryGetComp<CompPowerTrader>();
            CompSearchRadar search = radar.TryGetComp<CompSearchRadar>();
            IntVec3 PickOn(string guidance) => GuidanceType.PickCell(CruiseConfig(guidance), map, Player);
            string NetState()
            {
                PowerNet net = power.PowerNet;
                if (net == null) { return "radar has no power net"; }
                string batteries = string.Join(", ", map.listerThings.AllThings
                    .Select(x => x.TryGetComp<CompPowerBattery>()).Where(b => b != null)
                    .Select(b => $"{b.StoredEnergy:0.0}{(b.PowerNet == net ? "" : " (other net)")}"));
                return $"radar net: stored {net.CurrentStoredEnergy():0.0} Wd, gain {net.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick:0} W, "
                    + $"on {net.powerComps.Count(c => c.PowerOn)}/{net.powerComps.Count} "
                    + $"[{string.Join(", ", net.powerComps.Select(c => c.parent.def.defName + (c.PowerOn ? "+" : "-")))}]; "
                    + $"batteries on map: {batteries}; nets on map: {map.powerNetManager.AllNetsListForReading.Count}";
            }

            t.Check(!power.PowerOn, "the power net has not switched the radar on yet");
            t.Check(GuidanceType.PowerStarting(power), "the radar counts as starting up");
            t.Check(!search.Active && search.ActiveForSeeker, "radar is not Active for BVR yet, but is for seekers");
            t.Check(PickOn("DMSE_Light_Guidance_AntiRadiation") == radar.Position, "anti-radiation locks the radar that is still powering up");
            t.Check(PickOn("DMSE_Guidance_EMSignal") == radar.Position, "EM seeker locks the biggest nominal draw (phased array, 5 kW)");

            var waitOn = new WaitUntil(() => power.PowerOn, 2000);
            yield return waitOn;
            if (waitOn.TimedOut) { t.Note(NetState()); }
            t.Check(!waitOn.TimedOut, "the power net switches the radar on");
            t.Check(PickOn("DMSE_Light_Guidance_AntiRadiation") == radar.Position, "same anti-radiation pick once powered");
            t.Check(PickOn("DMSE_Guidance_EMSignal") == radar.Position, "same EM pick once powered");

            SetFlick(radar, false);
            yield return new WaitTicks(3);
            t.Check(!power.PowerOn && !GuidanceType.PowerStarting(power), "a switched-off radar is neither on nor starting");
            t.Check(PickOn("DMSE_Light_Guidance_AntiRadiation") != radar.Position, "anti-radiation ignores the switched-off radar");
            t.Check(PickOn("DMSE_Guidance_EMSignal") != radar.Position, "EM seeker ignores the switched-off radar");
        }

        public static IEnumerable<Wait> LaserDesignatorMarkLifecycle(TestContext t)
        {
            Pawn colonist = Home.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            t.Require(colonist != null, "a colonist exists");
            ThingDef gun = ThingDef.Named("DMSE_Gun_LaserDesignator");
            t.Check(gun.Verbs.Count > 0 && !gun.Verbs[0].ai_IsWeapon, "designator is not an AI weapon");
            t.Check(gun.Verbs[0].targetParams.canTargetLocations, "designator can target the ground");
            t.Check(gun.weaponTags.NullOrEmpty(), "designator has no weapon tags (never in faction loadouts)");

            IntVec3 target = colonist.Position + new IntVec3(4, 0, 0);
            if (!target.InBounds(Home) || !target.Standable(Home)) { target = colonist.Position + new IntVec3(0, 0, 4); }
            Projectile beam = (Projectile)GenSpawn.Spawn(ThingDef.Named("DMSE_Bullet_LaserDesignator"), colonist.Position, Home);
            beam.Launch(colonist, colonist.DrawPos, new LocalTargetInfo(target), new LocalTargetInfo(target), ProjectileHitFlags.IntendedTarget);

            LaserDesignatorMark mark = null;
            var waitMark = new WaitUntil(() =>
            {
                mark = Home.listerThings.AllThings.OfType<LaserDesignatorMark>().FirstOrDefault(m => m.owner == Player && !m.Destroyed);
                return mark != null;
            }, 300);
            yield return waitMark;
            t.Require(mark != null, "the designation beam leaves a player-owned mark");
            Track(mark);
            t.Check(mark.Position.DistanceTo(target) <= 1.5f, $"mark at the aimed cell ({mark.Position} vs {target})");
            t.Check(Mathf.Abs(mark.TicksLeft - 5000) <= 60, $"mark lasts ~5000 ticks (left {mark.TicksLeft})");
            t.Check(LaserDesignatorUtility.LatestMarkFor(Home, Player) == mark, "player laser missiles would lock this mark");

            CameraJumper.TryJump(new GlobalTargetInfo(mark.Position, Home));
            yield return new WaitFrames(5);
            t.Screenshot("laser_mark");

            GenExplosion.DoExplosion(mark.Position, Home, 3.9f, DamageDefOf.Bomb, null);
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(!mark.Destroyed, "explosions cannot destroy the mark");
            var waitExpire = new WaitUntil(() => mark.Destroyed, 5200);
            yield return waitExpire;
            t.Check(!waitExpire.TimedOut, "mark disappears when its time runs out");
        }

        // ------------------------------------------------------------------
        //  Save/load (must run last: it replaces the game)
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> SaveLoadMidEngagement(TestContext t)
        {
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(10, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            PlanetTile source = TileAtDistance(21f, 22f);
            t.Require(source.Valid, "a tile ~21 tiles away exists");
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(source);
            var waitLaunch = new WaitUntil(() => wo.State.interceptor != null, 2000);
            yield return waitLaunch;
            t.Require(!waitLaunch.TimedOut, "interceptor in flight before saving");
            wo.State.interceptor.hitChance = 0f;

            int trackId = wo.ID;
            int interceptorId = wo.State.interceptor.ID;
            int dueTick = wo.State.interceptor.interceptTick;
            int attempts = wo.State.interceptAttempts;
            int letters = LetterCount("DMSE.BVR.World.DetectedLetter");
            int stored = launcher.StoredCount;
            bool notified = wo.State.playerNotified;
            RememberEnemyForReload();
            t.Note($"saving with track #{trackId}, interceptor #{interceptorId} due {dueTick}, attempts {attempts}, stored {stored}");

            Game before = Current.Game;
            GameDataSaveLoader.SaveGame(TestSaveName);
            yield return new WaitFrames(10);
            GameDataSaveLoader.LoadGame(TestSaveName);
            var waitReload = new WaitUntil(() => Current.Game != before && Current.ProgramState == ProgramState.Playing, 999999);
            yield return waitReload;
            yield return new WaitFrames(5);

            MissileWorldObject track = Find.WorldObjects.AllWorldObjects.OfType<MissileWorldObject>().FirstOrDefault(x => x.ID == trackId);
            t.Require(track != null, "track restored after load");
            Building_MissileRack restoredLauncher = Home.listerThings.AllThings.OfType<Building_MissileRack>()
                .FirstOrDefault(r => r.def == LightLauncher);
            foreach (Thing th in Home.listerThings.AllThings.Where(x => x.def == PhaseArray || x.def == LightLauncher).ToList()) { Track(th); }
            Track(track);
            t.Check(track.State.detectedByFaction == Player, "detection state restored");
            t.Check(track.State.playerNotified == notified, "letter flag restored");
            t.Check(track.State.interceptAttempts == attempts, "attempt count restored");
            t.Check(track.State.interceptor != null && track.State.interceptor.ID == interceptorId, "in-flight interceptor reference restored");
            t.Check(track.State.interceptor != null && track.State.interceptor.interceptTick == dueTick, "meeting tick restored");
            t.Check(restoredLauncher != null && restoredLauncher.StoredCount == stored, "launcher stock unchanged by loading");
            yield return new WaitTicks(60);
            t.Check(LetterCount("DMSE.BVR.World.DetectedLetter") == letters, "no duplicate detection letter after load");
            t.Check(track.State.interceptAttempts == attempts, "no free re-launch while the saved interceptor is still flying");
            var waitResolve = new WaitUntil(() => track.State.interceptor == null || track.State.interceptor.ID != interceptorId,
                dueTick - Find.TickManager.TicksGame + 60);
            yield return waitResolve;
            t.Check(!waitResolve.TimedOut, "saved interceptor resolves at its meeting tick after load");
            t.Check(!track.Destroyed, "the forced miss is honoured after load");
        }
    }

    public static class AcceptanceSuite
    {
        public static List<AcceptanceTest> Build()
        {
            return new List<AcceptanceTest>
            {
                new AcceptanceTest("M0", "Environment and defs", Tests_Map.Environment),
                new AcceptanceTest("M1", "Device registration and Active", Tests_Map.DeviceRegistrationAndActive),
                new AcceptanceTest("M2", "Manned fire-control radar", Tests_Map.MannedFireControl),
                new AcceptanceTest("M3", "Drop pod capture, window, landing", Tests_Map.DropPodWindowAndLanding),
                new AcceptanceTest("M4", "Map midcourse SAM intercept", Tests_Map.MidcourseSamIntercept),
                new AcceptanceTest("M5", "CIWS terminal intercept with multi-pawn debris", Tests_Map.TerminalCiwsDebris),
                new AcceptanceTest("M6", "CIWS multi-wave priority", Tests_Map.CiwsMultiWavePriority),
                new AcceptanceTest("M7", "No radar: pod lands", Tests_Map.NoRadarLandsNormally),
                new AcceptanceTest("M8", "Shared fire-control channels (local + world)", Tests_Map.SharedChannels),
                new AcceptanceTest("S1", "Seeker target selection", Tests_Seekers.SeekerTargetSelection),
                new AcceptanceTest("S2", "Laser designator mark lifecycle", Tests_Seekers.LaserDesignatorMarkLifecycle),
                new AcceptanceTest("S3", "Seekers on a map generated at arrival", Tests_Seekers.SeekersOnFreshMap),
                new AcceptanceTest("W1", "World detection, ETA, landing without CIWS", Tests_World.DetectionEtaAndLanding),
                new AcceptanceTest("W2", "World interception hit", Tests_World.WorldInterceptHit),
                new AcceptanceTest("W3", "World interception miss and re-lock", Tests_World.WorldInterceptMissThenRelock),
                new AcceptanceTest("W4", "Terminal-only handoff with CIWS", Tests_World.TerminalHandoffWithCiws),
                new AcceptanceTest("W5", "Radar loss during engagement", Tests_World.RadarLossDuringEngagement),
                new AcceptanceTest("W6", "NPC abstract defense, map handoff, EM seeker", Tests_World.NpcAbstractDefenseAndSeeker),
                new AcceptanceTest("W7", "TV guidance manual target", Tests_World.TvGuidanceManualTarget),
                new AcceptanceTest("L1", "Save/load mid-engagement", Tests_Seekers.SaveLoadMidEngagement),
            };
        }
    }
}
