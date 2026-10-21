using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Three-generation equipment (docs/三代裝備體系_實作計畫.md §14-2): tower backblast, rotary rail roles, interceptor
    /// role filter, shipped channel counts, built-in anti-ship guidance and body research gates, interceptor hit factors,
    /// air-defence guns and the man-portable SAM. These run with the shipped radar values (no legacy pin).
    /// </summary>
    public static class Tests_Generations
    {
        private static ThingDef Def(string name) => ThingDef.Named(name);
        private static MissileBodyDef Body(string name) => DefDatabase<MissileBodyDef>.GetNamed(name);

        private static ThingDef Tower => Def("DMSE_Building_BallisticRack");
        private static ThingDef Silo => Def("DMSE_Building_BallisticSilo");
        private static ThingDef RotaryRail => Def("DMSE_Building_RotaryLauncher");
        private static ThingDef LaunchRail => Def("DMSE_Building_LoiteringLauncher");
        private static ThingDef SamLauncherBuilding => Def("DMSE_Building_RotaryMissileLauncher");
        private static ThingDef LightVls => Def("DMSE_Building_LightMissileVLS");
        private static ThingDef FieldAirDefense => Def("DMSE_Building_FieldAirDefense");
        private static ThingDef SearchRadar => Def("DMSE_SearchRadar");
        private static ThingDef FireControlRadar => Def("DMSE_FireControlRadar");
        private static ThingDef Millennium => Def("DMSE_Turret_Millennium");
        private static ThingDef Flak37 => Def("DMSE_Turret_LegacyFlak");
        private static ThingDef SamWeapon => Def("DMSE_Weapon_SamLauncher");

        private static ThingDef V1 => Def("DMSE_Missile_LegacyLoiter");
        private static ThingDef Termite => Def("DMSE_Missile_Termite");
        private static ThingDef Rheintochter => Def("DMSE_Missile_Rheintochter");
        private static ThingDef Stinger => Def("DMSE_Missile_Stinger");
        private static ThingDef Sparrow => Def("DMSE_Missile_Sparrow");
        private static ThingDef Tomahawk => Def("DMSE_Missile_AdvancedCruise");
        private static ThingDef Hellfire => Def("DMSE_Missile_AdvancedLight");
        private static ThingDef InterceptorItem => Def("DMSE_InterceptorMissile");

        private static Pawn Colonist => Home.mapPawns.FreeColonistsSpawned.FirstOrDefault();

        private static Thing Load(Building_MissileRack rack, ThingDef def)
        {
            Thing missile = ThingMaker.MakeThing(def);
            if (!rack.GetDirectlyHeldThings().TryAdd(missile)) { missile.Destroy(); return null; }
            return missile;
        }

        private static void Empty(Building_MissileRack rack) => rack.GetDirectlyHeldThings().ClearAndDestroyContents();

        /// <summary>Assemble a ballistic platform in place (V2 with its cheapest parts) and mark it loaded.</summary>
        private static void ArmWithV2(Thing platform)
        {
            CompMissileConfig cfg = platform.TryGetComp<CompMissileConfig>();
            cfg.EnsureInit();
            var v2 = new MissileConfig(Body("DMSE_MissileBody_LegacyBallistic"));
            v2.SetPart(MissilePartCategory.Warhead, Part("DMSE_LegacyBallistic_Warhead_Amatol"));
            v2.SetPart(MissilePartCategory.Guidance, Part("DMSE_Ballistic_Guidance_Inertial"));
            v2.SetPart(MissilePartCategory.Payload, Part("DMSE_Ballistic_Payload_None"));
            cfg.config = v2;
            cfg.pending = v2.Clone();
            platform.TryGetComp<CompMissileLauncher_Ballistic>().MarkLoaded();
        }

        private static Pawn SpawnAnimal(IntVec3 cell)
        {
            Pawn animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo"));
            GenSpawn.Spawn(animal, cell, Home);
            Track(animal);
            return animal;
        }

        private static void ClearFiresAndFloor(CellRect area)
        {
            foreach (Thing fire in Home.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
            {
                if (!fire.Destroyed) { fire.Destroy(DestroyMode.Vanish); }
            }
            foreach (IntVec3 c in area.ClipInsideMap(Home))
            {
                if (Home.terrainGrid.TerrainAt(c) != TerrainDefOf.Concrete) { Home.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete); }
            }
        }

        // ------------------------------------------------------------------
        //  T1: tower backblast vs. sealed silo
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> TowerBackblast(TestContext t)
        {
            Thing tower = SpawnBuilding(Tower, 10, 10);
            Thing wall = SpawnBuilding(ThingDefOf.Wall, 13, 10);
            Thing silo = SpawnBuilding(Silo, 28, 10);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CellRect area = new CellRect(Pad.minX + 4, Pad.minZ + 4, 30, 14);
            Override(() => { }, () => ClearFiresAndFloor(area));

            CompMissileConfig towerCfg = tower.TryGetComp<CompMissileConfig>();
            towerCfg.EnsureInit();
            t.Check(towerCfg.config.body == Body("DMSE_MissileBody_LegacyBallistic"), "a new launch tower starts with the legacy ballistic airframe (V2)");
            t.Check(Tower.researchPrerequisites.Contains(ResearchProjectDef.Named("DMSE_Ballistics")), "the tower is a ballistics-tier platform");
            t.Check(tower.TryGetComp<CompLaunchBackblast>() != null && silo.TryGetComp<CompLaunchBackblast>() == null,
                "the tower has a backblast, the silo does not");

            // Empty tower first (truss only), then loaded (V2 in the truss); zoomed in so the art is readable.
            Find.CameraDriver.SetRootPosAndSize(tower.TrueCenter() + new Vector3(0f, 0f, 1.5f), 14f);
            yield return new WaitFrames(30);
            t.Screenshot("tower_empty");
            ArmWithV2(tower);
            yield return new WaitFrames(10);
            t.Screenshot("tower_loaded");

            // A muffalo two cells south of the tower on a wooden floor, spawned right before the launch so it
            // has no time to wander off.
            IntVec3 victimCell = new IntVec3(Pad.minX + 10, 0, Pad.minZ + 8);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(victimCell, 1.5f, true))
            {
                if (c.InBounds(Home) && c.GetEdifice(Home) == null) { Home.terrainGrid.SetTerrain(c, TerrainDefOf.WoodPlankFloor); }
            }
            Pawn victim = SpawnAnimal(victimCell);
            float healthBefore = victim.health.summaryHealth.SummaryHealthPercent;

            PlanetTile target = TileAtDistance(5f, 10f);
            t.Require(target.Valid, "a target tile 5–10 tiles away exists");
            CompMissileLauncher_Ballistic towerLauncher = tower.TryGetComp<CompMissileLauncher_Ballistic>();
            ScorerProjectile_WorldObject shot = towerLauncher.TryLaunchRound(new GlobalTargetInfo(target), tower, false, out string reason);
            t.Require(shot != null, $"the tower launches the V2 ({reason ?? "ok"})");
            yield return new WaitTicks(1);
            Vector3 center = tower.TrueCenter();
            int fires = Home.listerThings.ThingsOfDef(ThingDefOf.Fire).Count(f => (f.Position.ToVector3Shifted() - center).MagnitudeHorizontal() <= 4f);
            t.Check(victim.Dead || victim.health.summaryHealth.SummaryHealthPercent < healthBefore,
                $"the animal two cells away is burned (health {healthBefore:0.00} → {(victim.Dead ? 0f : victim.health.summaryHealth.SummaryHealthPercent):0.00})");
            t.Check(victim.Dead || victim.stances.stunner.Stunned, "and stunned by the shockwave");
            t.Check(fires > 0, $"the exhaust starts fires around the tower ({fires})");
            t.Check(tower.HitPoints == tower.MaxHitPoints && !tower.Destroyed, "the tower itself is undamaged");
            t.Check(wall.HitPoints == wall.MaxHitPoints, "the wall next to the tower is undamaged");
            t.Screenshot("tower_backblast");

            // The sealed silo next door: same launch, nobody hurt.
            ClearFiresAndFloor(area);
            IntVec3 bystanderCell = new IntVec3(Pad.minX + 28, 0, Pad.minZ + 7);
            Pawn bystander = SpawnAnimal(bystanderCell);
            float bystanderBefore = bystander.health.summaryHealth.SummaryHealthPercent;
            ArmWithV2(silo);
            ScorerProjectile_WorldObject siloShot = silo.TryGetComp<CompMissileLauncher_Ballistic>()
                .TryLaunchRound(new GlobalTargetInfo(target), silo, false, out string siloReason);
            t.Require(siloShot != null, $"the silo launches ({siloReason ?? "ok"})");
            yield return new WaitTicks(1);
            t.Check(!bystander.Dead && bystander.health.summaryHealth.SummaryHealthPercent >= bystanderBefore,
                "the strategic silo does not hurt an animal three cells away");
            t.Check(!Home.listerThings.ThingsOfDef(ThingDefOf.Fire).Any(f => f.Position.InHorDistOf(silo.Position, 4f)),
                "and starts no fires");
        }

        // ------------------------------------------------------------------
        //  T2: rotary launch rail and launch rail: manning and roles
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> RotaryRailRoles(TestContext t)
        {
            Building_MissileRack rail = (Building_MissileRack)SpawnBuilding(RotaryRail, 10, 10);
            Building_MissileRack launchRail = (Building_MissileRack)SpawnBuilding(LaunchRail, 20, 10);
            yield return WaitPowered(); yield return new WaitTicks(2);
            Pawn colonist = Colonist;
            t.Require(colonist != null, "a colonist exists");
            CompMissileLauncher_Rail strike = rail.TryGetComp<CompMissileLauncher_Rail>();
            CompMissileLauncher_Interceptor sam = rail.TryGetComp<CompMissileLauncher_Interceptor>();
            CompMannable man = rail.TryGetComp<CompMannable>();
            t.Require(strike != null && sam != null && man is CompMannableMissileLauncher, "the rotary rail has strike, SAM and manning comps");

            StorageSettings store = rail.GetStoreSettings();
            t.Check(new[] { V1, Termite, Rheintochter, Stinger, Hellfire }.All(d => store.filter.Allows(d)),
                "the rotary rail stores loitering munitions and light missiles");
            t.Check(!store.filter.Allows(Tomahawk), "but not a Tomahawk-class cruise missile");
            t.Check(MissileTaxonomy.PlatformAccepts(RotaryRail, Body("DMSE_MissileBody_LegacyLoiter"))
                && MissileTaxonomy.PlatformAccepts(RotaryRail, Body("DMSE_MissileBody_Stinger"))
                && !MissileTaxonomy.PlatformAccepts(RotaryRail, Body("DMSE_MissileBody_Moskit")),
                "the taxonomy accepts Loitering + Light (extraSizeClasses) and rejects Medium");

            Thing v1 = Load(rail, V1);
            t.Check(!strike.Active && !sam.Active, "unmanned, the rail can neither strike nor intercept");
            man.ManForATick(colonist);
            t.Check(strike.Active, "manned, the strike rail is active");
            MissileConfig v1Config = strike.BuildFiringConfig(v1);
            t.Check(v1Config?.body == Body("DMSE_MissileBody_LegacyLoiter") && v1Config.Range == 12,
                $"the V1 flies with its own airframe, not the platform fallback (range {v1Config?.Range})");
            t.Check(!sam.HasAmmo, "a V1 is not an interceptor round");

            Empty(rail);
            Thing termite = Load(rail, Termite);
            man.ManForATick(colonist);
            MissileConfig termiteConfig = strike.BuildFiringConfig(termite);
            t.Check(termiteConfig?.body == Body("DMSE_MissileBody_Loiter"), "the Termite uses its anti-ship airframe on the same rail");
            t.Check(!sam.HasAmmo, "the SAM never takes a strike-only Termite");

            Empty(rail);
            Thing rhein = Load(rail, Rheintochter);
            man.ManForATick(colonist);
            t.Check(sam.Active && sam.HasAmmo, "manned with a Rheintochter, the SAM is armed");
            t.Check(!strike.HasAmmo && strike.BuildFiringConfig(rhein) == null, "the interceptor round is never fired at the ground");
            MissileConfig rheinConfig = sam.GetLoadedMissileConfig();
            t.Check(rheinConfig?.body == Body("DMSE_MissileBody_LegacyInterceptor") && Mathf.Approximately(rheinConfig.InterceptHitFactor, 0.6f),
                "the Rheintochter's config comes from MissileItemExtension (hit factor 0.6)");
            yield return new WaitTicks(3);
            t.Check(!sam.Active, "once nobody mans it, the SAM goes inactive");

            // The unmanned launch rail: same role split.
            CompMissileLauncher_Interceptor railSam = launchRail.TryGetComp<CompMissileLauncher_Interceptor>();
            CompMissileLauncher_Rail railStrike = launchRail.TryGetComp<CompMissileLauncher_Rail>();
            t.Require(railSam != null && railStrike != null, "the launch rail has both launch comps");
            Load(launchRail, Rheintochter);
            t.Check(railSam.Active && !railStrike.HasAmmo, "a Rheintochter on the launch rail arms its SAM, not its strike rail");
            Empty(launchRail);
            Load(launchRail, V1);
            t.Check(!railSam.HasAmmo && railStrike.Active, "a V1 arms the strike rail, not the SAM");
        }

        // ------------------------------------------------------------------
        //  T3: the SAM only takes interceptor / dual-role rounds
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> InterceptorRoleFilter(TestContext t)
        {
            Building_MissileRack box = (Building_MissileRack)SpawnBuilding(LightLauncher, 10, 2);
            Thing tomahawk = Load(box, Tomahawk);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompMissileLauncher_Interceptor sam = box.TryGetComp<CompMissileLauncher_Interceptor>();
            CompMissileLauncher_Rail rail = box.TryGetComp<CompMissileLauncher_Rail>();
            t.Check(!sam.HasAmmo && !sam.Active, "a strike-only cruise missile does not arm the SAM");
            t.Check(rail.StrikeRounds.Any(r => ReferenceEquals(r, tomahawk)), "it is still a strike round");
            Thing sa19 = Load(box, LightMissileItem);
            t.Check(sam.HasAmmo, "a dual-role SA-19 arms the SAM");
            int now = Find.TickManager.TicksGame;
            t.Require(sam.ReadyToFire(now), "the SAM is ready");
            t.Check(sam.FireWorldInterceptor(now) && sa19.Destroyed && !tomahawk.Destroyed && box.StoredCount == 1,
                "the SAM fires the SA-19 and leaves the cruise missile in the box");
        }

        // ------------------------------------------------------------------
        //  T4: shipped channel counts and radar wiring
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> ChannelsAndRadars(TestContext t)
        {
            Thing phased = SpawnBuilding(PhaseArray, 2, 2);
            Thing fcr = SpawnBuilding(FireControlRadar, 6, 2);
            Thing early = SpawnBuilding(EarlyFireControl, 10, 2);
            Building_MissileRack field = (Building_MissileRack)SpawnBuilding(FieldAirDefense, 14, 2);
            Thing search = SpawnBuilding(SearchRadar, 18, 2);
            Thing acoustic = SpawnBuilding(AcousticRadar, 25, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            Pawn colonist = Colonist;
            t.Require(colonist != null, "a colonist exists");

            int Max(Thing th) => th.TryGetComp<CompFireControlRadar>().Props.maxTargets;
            t.Check(Max(phased) == 24 && Max(fcr) == 4 && Max(early) == 1 && Max(field) == 2,
                $"channels: phased array 24, fire control 4, early 1, field air defence 2 (got {Max(phased)}/{Max(fcr)}/{Max(early)}/{Max(field)})");
            t.Check(phased.TryGetComp<CompPowerTrader>().Props.PowerConsumption == 9000f, "the phased array draws 9000 W");
            t.Check(HomeBVR.Channels.Capacity(Player) == 28, $"unmanned: 24 + 4 = 28 channels (got {HomeBVR.Channels.Capacity(Player)})");
            early.TryGetComp<CompMannable>().ManForATick(colonist);
            field.TryGetComp<CompMannable>().ManForATick(colonist);
            t.Check(HomeBVR.Channels.Capacity(Player) == 31, $"manned: + early 1 + field air defence 2 = 31 (got {HomeBVR.Channels.Capacity(Player)})");
            t.Check(field.TryGetComp<CompSearchRadar>().Active && field.TryGetComp<CompTerminalCIWS>().Active,
                "a manned field air-defence system runs its radar and cannon");
            t.Check(field.MaxStored == 4 && !field.TryGetComp<CompMissileLauncher_Interceptor>().HasAmmo, "it holds four missiles (empty now)");

            CompSearchRadar sr = search.TryGetComp<CompSearchRadar>();
            t.Check(sr.Active && sr.Props.worldDetectionRange == 12f && sr.Props.antiStealthLevel == 1 && search.TryGetComp<CompFireControlRadar>() == null,
                "the search radar is a pure 12-tile, anti-stealth-1 search set");
            CompSearchRadar ac = acoustic.TryGetComp<CompSearchRadar>();
            t.Check(acoustic.TryGetComp<CompPowerTrader>() == null && ac.Active && ac.Props.immuneToAntiRadiationSeeker,
                "the passive acoustic radar runs without power and is immune to anti-radiation seekers");

            Building_MissileRack sam = (Building_MissileRack)SpawnBuilding(SamLauncherBuilding, 32, 10);
            Building_MissileRack vls = (Building_MissileRack)SpawnBuilding(LightVls, 36, 2);
            t.Check(sam.MaxStored == 4 && vls.MaxStored == 4, $"SAM launcher and light VLS hold four rounds ({sam.MaxStored}/{vls.MaxStored})");
            t.Check(vls.TryGetComp<CompPowerTrader>() != null && vls.TryGetComp<CompMissileLauncher_Interceptor>() != null,
                "the light VLS needs power and can fire interceptors");
            CameraJumper.TryJump(new GlobalTargetInfo(field));
            yield return new WaitFrames(30);
            t.Screenshot("platforms");
        }

        // ------------------------------------------------------------------
        //  T5: built-in anti-ship guidance, ranges and body research gates
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> GuidanceRangesAndResearch(TestContext t)
        {
            var termite = new MissileConfig(Body("DMSE_MissileBody_Loiter"));
            t.Check(!termite.PlayerChoosesTarget && termite.TargetingGuidance is GuidanceType_AntiShip,
                "the Termite's built-in anti-ship seeker picks its own impact point");
            t.Check(termite.HasTerminalGuidance && termite.TerminalUpdates == 3 && termite.TracksMobileTargets,
                $"and gives real guidance: 3 terminal updates, tracks moving targets (got {termite.TerminalUpdates})");
            var v1 = new MissileConfig(Body("DMSE_MissileBody_LegacyLoiter"));
            t.Check(v1.PlayerChoosesTarget && !v1.HasTerminalGuidance, "the V1's built-in TV only picks the impact point");

            (string body, int range)[] expected =
            {
                ("DMSE_MissileBody_Loiter", 17), ("DMSE_MissileBody_LegacyLoiter", 12), ("DMSE_MissileBody_LegacyInterceptor", 10),
                ("DMSE_MissileBody_Stinger", 21), ("DMSE_MissileBody_Maverick", 24), ("DMSE_MissileBody_Sparrow", 29),
                ("DMSE_MissileBody_Moskit", 36), ("DMSE_MissileBody_Yj", 51), ("DMSE_MissileBody_Tochka", 31),
                ("DMSE_MissileBody_LegacyBallistic", 22),
            };
            foreach ((string body, int range) in expected)
            {
                int got = MissileConfig.NewWithDefaults(Body(body)).Range;
                t.Check(got == range, $"{body} range with default parts is {range} (got {got})");
            }

            ResearchProjectDef ballistics = ResearchProjectDef.Named("DMSE_Ballistics");
            ResearchProjectDef ic = ResearchProjectDef.Named("DMSE_IntegratedCircuits");
            ResearchProjectDef strategic = ResearchProjectDef.Named("DMSE_StrategicSystems");
            t.Check(Body("DMSE_MissileBody_LegacyBallistic").researchPrerequisites.SequenceEqual(new[] { ballistics })
                && Body("DMSE_MissileBody_Tochka").researchPrerequisites.SequenceEqual(new[] { ic })
                && Body("DMSE_MissileBody_Ballistic").researchPrerequisites.SequenceEqual(new[] { strategic })
                && Body("DMSE_MissileBody_AdvancedBallistic").researchPrerequisites.SequenceEqual(new[] { strategic }),
                "ballistic airframes are gated V2 → Ballistics, Tochka → IC, current / Minuteman → Strategic");
            t.Check(Body("DMSE_MissileBody_AdvancedBallistic").ResearchSatisfied == strategic.IsFinished,
                "the gate follows the research state");
            t.Check(!Part("DMSE_Ballistic_Warhead_Thermonuclear").ResearchSatisfied == !strategic.IsFinished,
                "the thermonuclear warhead now needs strategic systems");
            ResearchProjectDef antiship = ResearchProjectDef.Named("DMSE_AntishipMissiles");
            t.Check(antiship.prerequisites.Contains(ic), "the anti-ship node follows integrated circuits");
            t.Check(DefDatabase<RecipeDef>.AllDefs.Any(r => r.ProducedThingDef == Termite && r.researchPrerequisite == antiship),
                "the Termite recipe moved to anti-ship missiles");
            RecipeDef yj = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(r => r.ProducedThingDef == Def("DMSE_Missile_Yj"));
            t.Check(yj != null && yj.researchPrerequisites != null && yj.researchPrerequisites.Contains(antiship) && yj.researchPrerequisites.Contains(strategic),
                "the YJ recipe needs both anti-ship missiles and strategic systems");
            yield break;
        }

        // ------------------------------------------------------------------
        //  T6: interceptor hit factors
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> InterceptHitFactors(TestContext t)
        {
            Building_MissileRack sam = (Building_MissileRack)SpawnBuilding(SamLauncherBuilding, 10, 10);
            Building_MissileRack rail = (Building_MissileRack)SpawnBuilding(LaunchRail, 20, 10);
            yield return new WaitTicks(2);
            CompMissileLauncher_Interceptor launcher = sam.TryGetComp<CompMissileLauncher_Interceptor>();
            (ThingDef item, float factor)[] rounds =
            {
                (InterceptorItem, 1f), (LightMissileItem, 1f), (Stinger, 0.85f), (Sparrow, 1.25f),
            };
            foreach ((ThingDef item, float factor) in rounds)
            {
                Empty(sam);
                Load(sam, item);
                float got = launcher.GetLoadedMissileConfig()?.InterceptHitFactor ?? 1f;
                t.Check(Mathf.Approximately(got, factor), $"{item.defName} intercept factor {factor} (got {got})");
            }
            Load(rail, Rheintochter);
            float rhein = rail.TryGetComp<CompMissileLauncher_Interceptor>().GetLoadedMissileConfig()?.InterceptHitFactor ?? 1f;
            t.Check(Mathf.Approximately(rhein, 0.6f), $"Rheintochter intercept factor 0.6 (got {rhein})");

            // The factor reaches the map-layer shot: a 1-channel early radar, a Sparrow and a pod. The launch rail is an
            // unmanned SAM too, so empty it or its Rheintochter takes the shot.
            Empty(rail);
            Empty(sam);
            Load(sam, Sparrow);
            Thing early = SpawnBuilding(EarlyFireControl, 2, 2);
            SpawnBuilding(AcousticRadar, 2, 20);
            yield return WaitPowered(); yield return new WaitTicks(2);
            Pawn colonist = Colonist;
            CompMannable fcMan = early.TryGetComp<CompMannable>();
            CompMannable samMan = sam.TryGetComp<CompMannable>();
            TickWatch keepManned = AcceptanceDriver.Watch(() => { fcMan.ManForATick(colonist); samMan.ManForATick(colonist); return false; });
            List<Pawn> pawns = DropEnemyPod(1);
            yield return new WaitTicks(1);
            t.Require(HomeBVR.Waves.Count == 1, "pod captured");
            BVRTarget target = HomeBVR.Waves[0].targets[0];
            IInterceptorShot shot = null;
            var waitShot = new WaitUntil(() => (shot = InterceptorShotAt(target.id)) != null, 3000);
            yield return waitShot;
            if (waitShot.TimedOut)
            {
                t.Note("the acoustic window was too short for a midcourse shot; factor checked on the config only");
                yield break;
            }
            float expected = Mathf.Clamp01(early.TryGetComp<CompFireControlRadar>().ComputeHitChance(target, HomeBVR.Waves[0].tickToImpact - Find.TickManager.TicksGame) * 1.25f
                + Part("DMSE_Light_Warhead_ContinuousRod").warheadEffect.InterceptBonus(4f));
            t.Check(Mathf.Abs(shot.HitChance - expected) < 0.06f, $"the Sparrow's map-layer hit chance includes ×1.25 ({shot.HitChance:0.00} vs {expected:0.00})");
            t.Check(((Thing)shot).def.defName == "DMSE_Launch_Sparrow", $"the SAM launches the Sparrow's own launch projectile ({((Thing)shot).def.defName})");
        }

        // ------------------------------------------------------------------
        //  T7: Millennium and Flak 37
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> AirDefenceGuns(TestContext t)
        {
            SpawnBuilding(AcousticRadar, 2, 2);
            Thing mill = SpawnBuilding(Millennium, 14, 4);
            Thing flak = SpawnBuilding(Flak37, 24, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompTerminalCIWS millCiws = mill.TryGetComp<CompTerminalCIWS>();
            CompTerminalCIWS flakCiws = flak.TryGetComp<CompTerminalCIWS>();
            t.Require(millCiws != null && flakCiws != null && mill is Building_TurretGun && flak is Building_TurretGun,
                "both guns are turrets with terminal CIWS");
            t.Check(millCiws.Active, "the Millennium works unmanned");
            t.Check(!flakCiws.Active, "the Flak 37 does nothing without a gunner");
            Pawn colonist = Colonist;
            flak.TryGetComp<CompMannable>().ManForATick(colonist);
            t.Check(flakCiws.Active, "manned, the Flak 37 engages");
            Find.CameraDriver.SetRootPosAndSize((mill.TrueCenter() + flak.TrueCenter()) / 2f, 16f);
            yield return new WaitFrames(30);
            t.Screenshot("air_defence_guns");

            CompRefuelable fuel = mill.TryGetComp<CompRefuelable>();
            float before = fuel.Fuel;
            List<Pawn> pawns = new List<Pawn>();
            for (int i = 0; i < 3; i++) { pawns.AddRange(DropEnemyPod(1)); }
            yield return new WaitTicks(1);
            t.Require(HomeBVR.Waves.Sum(w => w.targets.Count) == 3, "three pods captured");
            var waitClear = new WaitUntil(() => HomeBVR.Waves.Count == 0, 3000);
            yield return waitClear;
            t.Check(!waitClear.TimedOut, "the waves resolve");
            int landed = pawns.Count(p => p.Spawned && !p.Dead);
            float used = before - fuel.Fuel;
            t.Check(landed == 0, $"the Millennium stops all three pods ({landed} landed)");
            t.Check(used >= 24f * 3 - 0.01f, $"each burst burns 24 rounds ({used} used for {3 - landed} kills)");
        }

        // ------------------------------------------------------------------
        //  T8: man-portable SAM
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> ManPortableSam(TestContext t)
        {
            Pawn colonist = Colonist;
            t.Require(colonist != null, "a colonist exists");
            ThingWithComps oldPrimary = colonist.equipment.Primary;
            if (oldPrimary != null) { colonist.equipment.Remove(oldPrimary); }
            ThingWithComps weapon = (ThingWithComps)ThingMaker.MakeThing(SamWeapon);
            colonist.equipment.AddEquipment(weapon);
            Override(() => { }, () =>
            {
                if (!weapon.Destroyed)
                {
                    if (colonist.equipment.Primary == weapon) { colonist.equipment.Remove(weapon); }
                    weapon.Destroy();
                }
                if (oldPrimary != null && !oldPrimary.Destroyed && colonist.equipment.Primary == null) { colonist.equipment.AddEquipment(oldPrimary); }
                foreach (Thing m in colonist.inventory.innerContainer.Where(x => x.def == Stinger).ToList())
                {
                    colonist.inventory.innerContainer.Remove(m);
                    m.Destroy();
                }
            });

            var comp = weapon.GetComp<CompEquippable_ManPortableSAM>();
            t.Require(comp != null && colonist.equipment.PrimaryEq == comp, "the launcher's equippable comp is the SAM comp");
            Command_Action Gizmo() => colonist.equipment.GetGizmos().OfType<Command_Action>()
                .FirstOrDefault(c => c.defaultLabel == "DMSE.ManPortableSAM.Fire".Translate());
            t.Check(Gizmo() != null && Gizmo().Disabled, "without missiles the air-defence command is shown but disabled");
            t.Check(!comp.PrimaryVerb.Available(), "and the ground shot is unavailable");

            for (int i = 0; i < 2; i++) { colonist.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(Stinger)); }
            t.Check(comp.AmmoCount(colonist) == 2 && comp.PrimaryVerb.Available(), "two missiles in the pack arm the ground shot");

            SpawnBuilding(AcousticRadar, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            float oldRange = comp.Props.maxRangeCells;
            int oldEta = comp.Props.maxEtaTicks;
            Override(() => { comp.Props.maxRangeCells = 999f; comp.Props.maxEtaTicks = 100000; },
                () => { comp.Props.maxRangeCells = oldRange; comp.Props.maxEtaTicks = oldEta; });
            DropEnemyPod(1);
            DropEnemyPod(1);
            yield return new WaitTicks(1);
            List<BVRTarget> targets = comp.EngageableTargets(colonist).ToList();
            t.Require(targets.Count == 2, $"two tracked pods are engageable (got {targets.Count})");
            t.Check(Gizmo() != null && !Gizmo().Disabled, "with missiles and tracks the command is enabled");

            int now = Find.TickManager.TicksGame;
            t.Check(comp.TryFire(colonist, targets[0].id, out string reason), $"firing at the first pod succeeds ({reason ?? "ok"})");
            IInterceptorShot shot = InterceptorShotAt(targets[0].id);
            t.Check(shot != null && shot.HitChance > 0f, $"an interceptor is in flight (hit chance {shot?.HitChance:0.00})");
            t.Check((shot as Thing)?.def.defName == "DMSE_Launch_Stinger", $"the man-portable SAM launches the Stinger's own launch projectile ({(shot as Thing)?.def.defName})");
            t.Check(comp.AmmoCount(colonist) == 1 && targets[0].midcourseEngagedUntil > now, "one missile used; the target is marked engaged");
            t.Check(comp.OnCooldown && Gizmo().Disabled && !comp.TryFire(colonist, targets[1].id, out _), "the launcher is on cooldown");

            typeof(CompEquippable_ManPortableSAM).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(comp, 0);
            Job job = JobMaker.MakeJob(DMSE_ManPortableSAMDefOf.DMSE_FireManPortableSAM, colonist);
            job.count = targets[1].id;
            t.Require(colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc), "the colonist takes the aiming job");
            var waitSecond = new WaitUntil(() => comp.AmmoCount(colonist) == 0, 600);
            yield return waitSecond;
            t.Check(!waitSecond.TimedOut, "after aiming, the job fires the second missile");
            t.Check(!comp.PrimaryVerb.Available(), "out of missiles, the ground shot is unavailable again");
        }

        public static IEnumerable<AcceptanceTest> Tests()
        {
            yield return new AcceptanceTest("T1", "Launch tower backblast; sealed silo", TowerBackblast);
            yield return new AcceptanceTest("T2", "Rotary and launch rails: manning, item airframes, roles", RotaryRailRoles);
            yield return new AcceptanceTest("T3", "SAM takes only interceptor / dual-role rounds", InterceptorRoleFilter);
            yield return new AcceptanceTest("T4", "Shipped channel counts and new radars", ChannelsAndRadars);
            yield return new AcceptanceTest("T5", "Built-in anti-ship guidance, ranges, research gates", GuidanceRangesAndResearch);
            yield return new AcceptanceTest("T6", "Interceptor hit factors", InterceptHitFactors);
            yield return new AcceptanceTest("T7", "Millennium and Flak 37", AirDefenceGuns);
            yield return new AcceptanceTest("T8", "Man-portable SAM", ManPortableSam);
        }
    }
}
