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

            // A dense salvo with the same seeker spreads: the second lock skips the building the first one claimed.
            GuidanceType.ClearClaims();
            MissileConfig emCfg = CruiseConfig("DMSE_Guidance_EMSignal");
            IntVec3 salvo1 = GuidanceType.ArrivalCell(emCfg, Home, Enemy, out _);
            IntVec3 salvo2 = GuidanceType.ArrivalCell(emCfg, Home, Enemy, out _);
            t.Check(salvo1 == phased.Position && salvo2 == earlyFc.Position,
                $"EM salvo spreads (1st {salvo1} = phased array, 2nd {salvo2} = next biggest draw, early FC radar)");
            t.Check(Pick("DMSE_Guidance_EMSignal") == phased.Position, "a plain query still reports the best target");
            GuidanceType.ClearClaims();

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

            // Active radar prioritizes physical footprint, independently of HP or power draw.
            int maxArea = GuidanceType.HostileBuildings(Home, Enemy).Max(x => x.def.size.x * x.def.size.z);
            IntVec3 activeRadar = Pick("DMSE_Guidance_ActiveRadar");
            t.Check(GuidanceType.HostileBuildings(Home, Enemy).Any(x =>
                x.def.size.x * x.def.size.z == maxArea && x.Position == activeRadar),
                "active radar locks the largest hostile building by footprint");
            t.Check(Pick("DMSE_AdvLight_Guidance_MMW") == activeRadar,
                "MMW and general active radar use the same size-based targeting");

            // Inertial and unattended TV use the map-center Gaussian, regardless of enemy positions.
            Rand.PushState(742);
            IntVec3 inertial = Pick("DMSE_Guidance_Inertial");
            Rand.PopState();
            Rand.PushState(742);
            IntVec3 tvFallback = Pick("DMSE_Guidance_Precision");
            Rand.PopState();
            t.Check(inertial == tvFallback, "enemy TV missile uses the same inertial distribution");
            int radius = Mathf.Min(Mathf.Min(Home.Center.x, Home.Size.x - 1 - Home.Center.x),
                Mathf.Min(Home.Center.z, Home.Size.z - 1 - Home.Center.z));
            t.Check(inertial.InBounds(Home) && (inertial - Home.Center).LengthHorizontalSquared <= radius * radius,
                "inertial target lies inside the map-center circle");

            // Who chooses the target.
            t.Check(CruiseConfig("DMSE_Guidance_Precision").PlayerChoosesTarget, "TV guidance: player chooses");
            t.Check(!CruiseConfig("DMSE_Guidance_EMSignal").PlayerChoosesTarget, "EM seeker: automatic");
            t.Check(!CruiseConfig("DMSE_Guidance_Inertial").PlayerChoosesTarget, "inertial: automatic");
            MissileConfig loiter = new MissileConfig(DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_LegacyLoiter"));
            t.Check(loiter.PlayerChoosesTarget, "legacy loitering munition (built-in TV) lets the player choose");
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
        //  Loiter flight control
        // ------------------------------------------------------------------

        /// <summary>
        /// Loiter flight control: with nothing to lock the drone waits overhead instead of striking blind, re-runs
        /// its seeker on the configured interval, dives the moment a target appears, and only falls back to the
        /// inertial impact point when the loiter time runs out. Loitering targets stay terminal-only, so CIWS can
        /// engage them for the whole wait.
        /// </summary>
        public static IEnumerable<Wait> LoiterFlightControl(TestContext t)
        {
            MissileBodyDef shahed = DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Shahed");
            MissilePartDef loiter = DefDatabase<MissilePartDef>.GetNamed("DMSE_Payload_Loiter");
            MissilePartDef laser = DefDatabase<MissilePartDef>.GetNamed("DMSE_Guidance_Laser");

            // Fitting rules: loitering airframes only, and the airframe really has a payload slot.
            t.Check(loiter.requiresLoiteringBody, "loiter flight control is restricted to loitering airframes");
            t.Check(shahed.slots.Contains(MissilePartCategory.Payload), "the Shahed airframe has a payload slot");
            t.Check(loiter.CompatibleWith(shahed), "so the flight control fits it");
            MissileBodyDef cruiseBody = DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Moskit");
            t.Check(!loiter.CompatibleWith(cruiseBody), "a cruise missile airframe cannot take it");

            MissileConfig cfg = MissileConfig.NewWithDefaults(shahed);
            cfg.SetPart(MissilePartCategory.Guidance, laser);
            cfg.SetPart(MissilePartCategory.Payload, loiter);
            t.Check(cfg.LoiterTicks == loiter.loiterTicks && cfg.LoiterTicks > 0,
                $"fitted: {cfg.LoiterTicks} ticks of loiter, re-seek every {cfg.LoiterRetryTicks} ticks");
            MissileConfig forced = new MissileConfig(cruiseBody);
            forced.SetPart(MissilePartCategory.Payload, loiter);
            t.Check(forced.LoiterTicks == 0, "force-fitted to a cruise body it still never loiters");

            // Nothing to lock: drop this faction's designations, then the laser seeker reports no target
            // and arrival asks for a loiter instead of an inertial strike.
            foreach (LaserDesignatorMark leftover in Home.listerThings.AllThings.OfType<LaserDesignatorMark>().ToList())
            {
                if (leftover.IsActiveFor(Player)) { leftover.Destroy(); }
            }
            yield return new WaitTicks(2);
            t.Check(!GuidanceType.TryPickTarget(cfg, Home, Player, out _), "the seeker has nothing to lock on arrival");
            IntVec3 arrival = GuidanceType.ArrivalCell(cfg, Home, Player, out bool loiterOnArrival);
            t.Check(loiterOnArrival && arrival.InBounds(Home), "arrival asks for a loiter (the inertial point stays the fallback)");

            // Held in a loitering wave: the missile is put away, the wave is terminal-only.
            MapComponent_BVRCombat comp = Home.GetComponent<MapComponent_BVRCombat>();
            Thing missile = MissileProjectileMaker.SpawnIncoming(ThingDef.Named("DMSE_Incoming_Shahed"), arrival, Home);
            Projectile_MissileIncoming inc = (Projectile_MissileIncoming)missile;
            inc.config = cfg; inc.attacker = Player; inc.bvrHandled = true;
            missile.DeSpawn();
            BVRWave wave = comp.RegisterLoiter(missile, arrival, Enemy, cfg.LoiterTicks, 30);
            t.Require(wave != null && wave.targets.Count == 1, "the missile is held in a loitering wave");
            t.Check(wave.loitering, "the wave is marked as loitering");
            t.Check(wave.TerminalOnly, "loitering targets are terminal-only, so CIWS can engage them the whole time");
            t.Check(!missile.Spawned, "the missile itself is put away while it waits");

            yield return new WaitTicks(5);
            t.Check(wave.loitering, "still waiting before the first seek interval elapses");

            // A target appears: the next seek locks it and the wave starts its dive.
            IntVec3 designated = new IntVec3(Pad.minX + 44, 0, Pad.minZ + 44);
            LaserDesignatorMark mark = LaserDesignatorUtility.PlaceMark(designated, Home, Player, null, 5000);
            Track(mark);
            var waitLock = new WaitUntil(() => !wave.loitering, 200);
            yield return waitLock;
            t.Check(!waitLock.TimedOut, "the drone locks the new target on its next seek");
            t.Check(wave.targets[0].position.DistanceTo(designated) <= 3f,
                $"and dives at the designated cell ({wave.targets[0].position} vs {designated})");
            t.Check(wave.tickToImpact - Find.TickManager.TicksGame <= BVRTuning.LoiterDiveTicks,
                "with only the short dive window left before impact");
            // Clean up: the projectile would otherwise land in the test map later.
            foreach (BVRTarget target in wave.targets) { target.Discard(); }
            comp.Waves.Remove(wave);

            // Time runs out with nothing to lock: fall back to the inertial impact point and dive.
            // (Remove the designation, and keep the seek interval longer than the loiter time, so only the
            //  expiry path can end this one.)
            mark.Destroy();
            IntVec3 start = new IntVec3(Pad.minX + 48, 0, Pad.minZ + 48);
            Thing second = MissileProjectileMaker.SpawnIncoming(ThingDef.Named("DMSE_Incoming_Shahed"), start, Home);
            Projectile_MissileIncoming inc2 = (Projectile_MissileIncoming)second;
            inc2.config = cfg; inc2.attacker = Player; inc2.bvrHandled = true;
            second.DeSpawn();
            BVRWave timed = comp.RegisterLoiter(second, start, Enemy, 10, 1000);
            t.Require(timed != null && timed.targets.Count == 1, "a second drone waits overhead");
            var waitExpire = new WaitUntil(() => !timed.loitering, 200);
            yield return waitExpire;
            t.Check(!waitExpire.TimedOut, "the loiter ends when its time runs out");
            t.Check(timed.targets[0].position.InBounds(Home) && timed.targets[0].position.DistanceTo(designated) > 3f,
                $"it falls back to an in-bounds inertial point, not the designation ({timed.targets[0].position})");
            t.Check(timed.tickToImpact - Find.TickManager.TicksGame <= BVRTuning.LoiterDiveTicks,
                "and starts the same short dive");
            foreach (BVRTarget target in timed.targets) { target.Discard(); }
            comp.Waves.Remove(timed);
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
            List<AcceptanceTest> tests = Mechanics();
            // Three-generation equipment (Tests_Generations) runs before the save/load tests.
            tests.InsertRange(tests.FindIndex(x => x.id == "L1"), Tests_Generations.Tests());
            // Skyfaller → Projectile migration parity (docs/Missile_Skyfaller轉Projectile_計畫.md §8.6).
            tests.InsertRange(tests.FindIndex(x => x.id == "L1"), Tests_MissileFlight.Tests());
            // Intercept debris FX screenshots (docs/攔截殘骸特效_實作與技術美術計畫.md).
            tests.InsertRange(tests.FindIndex(x => x.id == "L1"), Tests_InterceptFx.Tests());
            // Missile launch FX remake (docs/導彈發射特效_重製計畫.md).
            tests.InsertRange(tests.FindIndex(x => x.id == "L1"), Tests_LaunchFx.Tests());
            tests.Add(Tests_MissileFlight.OldSaveTest()); // reloads the game: last
            return tests;
        }

        private static List<AcceptanceTest> Mechanics()
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
                new AcceptanceTest("S4", "Loiter flight control: wait, re-seek, dive, fall back", Tests_Seekers.LoiterFlightControl),
                new AcceptanceTest("W1", "World detection, ETA, landing without CIWS", Tests_World.DetectionEtaAndLanding),
                new AcceptanceTest("W2", "World interception hit", Tests_World.WorldInterceptHit),
                new AcceptanceTest("W3", "World interception miss and re-lock", Tests_World.WorldInterceptMissThenRelock),
                new AcceptanceTest("W4", "Terminal-only handoff with CIWS", Tests_World.TerminalHandoffWithCiws),
                new AcceptanceTest("W5", "Radar loss during engagement", Tests_World.RadarLossDuringEngagement),
                new AcceptanceTest("W6", "NPC abstract defense, map handoff, EM seeker", Tests_World.NpcAbstractDefenseAndSeeker),
                new AcceptanceTest("W7", "TV guidance manual target", Tests_World.TvGuidanceManualTarget),
                new AcceptanceTest("W8", "World airburst", Tests_WorldEdge.WorldAirburst),
                new AcceptanceTest("W9", "Moving target: reroute, lock reset, re-engagement", Tests_WorldEdge.TerminalUpdateReroute),
                new AcceptanceTest("W10", "Terminal CIWS power loss", Tests_WorldEdge.TerminalCiwsPowerLoss),
                new AcceptanceTest("W11", "ETA at player speeds and view switches", Tests_WorldEdge.EtaAtPlayerSpeeds),
                new AcceptanceTest("D1", "Datalink and area defense (crossing target)", Tests_P4.AreaDefense),
                new AcceptanceTest("D2", "Synthetic settlement air defense and radar suppression", Tests_P4.SettlementDefense),
                new AcceptanceTest("D3", "Undetected tracks are hidden", Tests_P4.HiddenTracks),
                new AcceptanceTest("D4", "Drop-pod ghost track and abstract-node degradation", Tests_P4.GhostTrackAndAbstractNode),
                new AcceptanceTest("A1", "Alert stage 3 lets transit pass, stage 4 engages it", Tests_AirAlert.PassiveThenActive),
                new AcceptanceTest("X1", "Developer debug actions", Tests_DevTools.DebugActions),
                new AcceptanceTest("A2","Alert pressure, regional spread, blockade notice, zone and routes", Tests_AirAlert.PressureNoticeAndBlockade),
                new AcceptanceTest("K1", "Strike rounds, reservation, per-round and ripple launch", Tests_Strike.RoundsReservationAndLaunch),
                new AcceptanceTest("K2", "Reserved rounds skip assembly; silo as a round", Tests_Strike.ReservedSkipAssembly),
                new AcceptanceTest("K3", "World defense engages the soonest arrival first", Tests_Strike.SoonestArrivalFirst),
                new AcceptanceTest("K4", "Decoy waves against NPC abstract stock (plan B)", Tests_Strike.DecoyWavesDrainAbstractStock),
                new AcceptanceTest("K5", "No interceptor ammo, no lock; release when the last one is fired", Tests_Strike.NoAmmoNoLock),
                new AcceptanceTest("K6", "Auto strike console: plan, reserve, compute, ripple waves, guidance period, cancel", Tests_Strike.AutoConsoleWaves),
                new AcceptanceTest("K7", "Strike mission save/load mid-ripple, then completion", Tests_Strike.StrikeSaveLoadAndCompletion),
                new AcceptanceTest("K8", "Strike window draws in planning and execution modes", Tests_Strike.StrikeDialogRenders),
                new AcceptanceTest("K9", "Manual planning table: research work, wave-by-wave solving, alert", Tests_Strike.ManualPlanningTable),
                new AcceptanceTest("K10", "Adjust waves during execution; incremental computation", Tests_Strike.AdjustWavesIncremental),
                new AcceptanceTest("K11", "Arrival timeline, wave-ready alert, alert grouping, results letter", Tests_Strike.TimelineReportsAlerts),
                new AcceptanceTest("K12", "Timed launch waits to land after the earlier wave", Tests_Strike.TimedLaunch),
                new AcceptanceTest("K13", "Firing solutions are recomputed after a large target move", Tests_Strike.RecomputeOnTargetMove),
                new AcceptanceTest("K14", "Decoy payload and threat assessment", Tests_Strike.DecoyThreatAssessment),
                new AcceptanceTest("K15", "NPC missile base salvo", Tests_Strike.NpcSalvo),
                new AcceptanceTest("N1", "Industrial field launch site: difficulty layout, garrison, radar-gated supply drops", Tests_FieldLaunchSite.LayoutCrewAndSupply),
                new AcceptanceTest("N2", "Field launch site incident: spawns in range, letter, one at a time", Tests_FieldLaunchSite.IncidentSpawnsSite),
                new AcceptanceTest("N3", "Missile site gunners man launchers, radars and guns; leave to eat when hungry", Tests_FieldLaunchSite.GunnersManStationsAndEat),
                new AcceptanceTest("N4", "Missile alerts: one letter, repeats within an hour become messages", Tests_FieldLaunchSite.AlertThrottle),
                new AcceptanceTest("G1", "God-mode instant missile assembly", Tests_Assembly.GodModeInstantAssembly),
                new AcceptanceTest("R1", "Empero long-range gun: defs, head, targeting rules, out of shells", Tests_Artillery.DefsAndTargeting),
                new AcceptanceTest("R2", "Empero unguided strike: warmup aim, departure, world track, map kept, edge entry near centre", Tests_Artillery.UnguidedStrike),
                new AcceptanceTest("R3", "Empero radar-guided strike: channel per gun, scatter 3 on a hostile building, release, full pool unguided", Tests_Artillery.RadarGuidedStrike),
                new AcceptanceTest("R4", "Empero local reduced-charge fire: head turns, shared shells, held during world fire", Tests_Artillery.LocalReducedChargeFire),
                new AcceptanceTest("R5", "Empero local fire controls: min range, hold fire, forced target, buttons", Tests_Artillery.LocalFireControls),
                new AcceptanceTest("AB1", "Assembly bench: facilities, info card, labor speed", Tests_AssemblyBench.BenchFacilitiesAndInfoCard),
                new AcceptanceTest("AB2", "Assembly bench: colonist hauls and assembles an order", Tests_AssemblyBench.BenchProducesWithColonist),
                new AcceptanceTest("AB3", "Assembly bench: cancel, send to storage, designs, window", Tests_AssemblyBench.BenchOutputCancelAndDesigns),
                new AcceptanceTest("L1", "Save/load mid-engagement", Tests_Seekers.SaveLoadMidEngagement),
                new AcceptanceTest("L2", "Save/load after a reroute", Tests_WorldEdge.SaveLoadAfterReroute),
            };
        }
    }
}
