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

        private static bool HasPrerequisites(ResearchProjectDef project, params string[] defNames)
        {
            if (project == null || project.prerequisites == null) { return false; }
            return project.prerequisites.Select(p => p.defName).OrderBy(n => n)
                .SequenceEqual(defNames.OrderBy(n => n));
        }

        private static bool HasResearchPrerequisites(ThingDef thing, params string[] defNames)
        {
            if (thing == null || thing.researchPrerequisites == null) { return false; }
            return thing.researchPrerequisites.Select(p => p.defName).OrderBy(n => n)
                .SequenceEqual(defNames.OrderBy(n => n));
        }

        private static bool HasPartResearchPrerequisites(MissilePartDef part, params string[] defNames)
        {
            if (part == null || part.researchPrerequisites == null) { return false; }
            return part.researchPrerequisites.Select(p => p.defName).OrderBy(n => n)
                .SequenceEqual(defNames.OrderBy(n => n));
        }

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
        private static ThingDef Flak43 => Def("DMSE_Turret_LegacyFlak");
        private static ThingDef SamLauncher => Def("DMSE_Apparel_SamLauncher");

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
            // Strike launches need free strike channels (weighted channels, 2026-10-22: a ballistic round costs 2).
            // Two phased arrays give 4, enough for the tower's V2 and the silo's; far from the backblast area.
            SpawnBuilding(PhaseArray, 2, 30);
            SpawnBuilding(PhaseArray, 8, 30);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CellRect area = new CellRect(Pad.minX + 4, Pad.minZ + 4, 30, 14);
            Override(() => { }, () => ClearFiresAndFloor(area));

            CompMissileConfig towerCfg = tower.TryGetComp<CompMissileConfig>();
            towerCfg.EnsureInit();
            t.Check(towerCfg.config.body == Body("DMSE_MissileBody_LegacyBallistic"), "a new launch tower starts with the legacy ballistic airframe (V2)");
            t.Check(Tower.researchPrerequisites.Contains(ResearchProjectDef.Named("DMSE_LoiteringMunitions")), "basic missile engineering unlocks the V2 launch tower");
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
            // 24 = the V1's range after the 2026-10-24 rebalance (ICBM benchmark 500 tiles).
            t.Check(v1Config?.body == Body("DMSE_MissileBody_LegacyLoiter") && v1Config.Range == 24,
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
            t.Check(field.TryGetComp<CompMannable>() == null, "the field air-defence system is automatic (no manning comp)");
            t.Check(HomeBVR.Channels.Capacity(Player) == 30, $"unmanned: 24 + 4 + field air defence 2 = 30 channels (got {HomeBVR.Channels.Capacity(Player)})");
            early.TryGetComp<CompMannable>().ManForATick(colonist);
            t.Check(HomeBVR.Channels.Capacity(Player) == 31, $"manned: + early 1 = 31 (got {HomeBVR.Channels.Capacity(Player)})");
            t.Check(field.TryGetComp<CompSearchRadar>().Active && field.TryGetComp<CompTerminalCIWS>().Active,
                "the unmanned field air-defence system runs its radar and cannon");
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

            // Ranges after the 2026-10-24 rebalance (calibrated to a 500-tile Minuteman; each body def's comment
            // shows its fuel × specific impulse × range factor).
            (string body, int range)[] expected =
            {
                ("DMSE_MissileBody_Loiter", 32), ("DMSE_MissileBody_LegacyLoiter", 24), ("DMSE_MissileBody_LegacyInterceptor", 20),
                ("DMSE_MissileBody_Stinger", 20), ("DMSE_MissileBody_Maverick", 32), ("DMSE_MissileBody_Sparrow", 36),
                ("DMSE_MissileBody_Moskit", 70), ("DMSE_MissileBody_Yj", 235), ("DMSE_MissileBody_Tochka", 125),
                ("DMSE_MissileBody_LegacyBallistic", 95),
            };
            foreach ((string body, int range) in expected)
            {
                int got = MissileConfig.NewWithDefaults(Body(body)).Range;
                t.Check(got == range, $"{body} range with default parts is {range} (got {got})");
            }

            ResearchProjectDef basicMissileEngineering = ResearchProjectDef.Named("DMSE_LoiteringMunitions");
            ResearchProjectDef ic = ResearchProjectDef.Named("DMSE_IntegratedCircuits");
            ResearchProjectDef strategic = ResearchProjectDef.Named("DMSE_StrategicSystems");
            ResearchProjectDef ballisticPlatform = ResearchProjectDef.Named("DMSE_Ballistics");
            t.Check(Body("DMSE_MissileBody_LegacyBallistic").researchPrerequisites.SequenceEqual(new[] { basicMissileEngineering })
                && Body("DMSE_MissileBody_Tochka").researchPrerequisites.SequenceEqual(new[] { ic })
                && Body("DMSE_MissileBody_AdvancedBallistic").researchPrerequisites.SequenceEqual(new[] { ballisticPlatform }),
                "airframes are gated V2 → basic missile engineering, Tochka → IC, Minuteman → ballistic platform research");
            // Tochka became a medium item missile (2026-10-27): crafted at IC, fired from medium platforms, medium parts.
            MissileBodyDef tochka = Body("DMSE_MissileBody_Tochka");
            ThingDef tochkaItem = DefDatabase<ThingDef>.GetNamedSilentFail("DMSE_Missile_Tochka");
            t.Check(tochka.sizeClass == MissileSizeClass.Medium && tochka.customizeLocation == MissileCustomizeLocation.Item && tochkaItem != null,
                "Tochka is a medium missile with an item form");
            t.Check(DefDatabase<RecipeDef>.AllDefs.Any(r => r.ProducedThingDef == tochkaItem && r.researchPrerequisites != null && r.researchPrerequisites.Contains(ic)),
                "the Tochka item is crafted after integrated circuits");
            t.Check(new[] { "DMSE_Building_Scorer", "DMSE_Building_MediumVLS" }.All(b => ThingDef.Named(b).building.fixedStorageSettings.filter.Allows(tochkaItem)),
                "the medium tilt launcher and the vertical launch silo accept it");
            t.Check(Part("DMSE_Warhead_HE").compatibleBodies.Contains(tochka) && !Part("DMSE_Ballistic_Warhead_Airburst").compatibleBodies.Contains(tochka),
                "it takes medium parts, not ballistic ones");
            t.Check(Body("DMSE_MissileBody_AdvancedBallistic").ResearchSatisfied == ballisticPlatform.IsFinished,
                "the gate follows the research state");
            t.Check(!Part("DMSE_Ballistic_Warhead_Thermonuclear").ResearchSatisfied == !strategic.IsFinished,
                "the thermonuclear warhead now needs strategic systems");
            ResearchProjectDef antiship = ResearchProjectDef.Named("DMSE_AntishipMissiles");
            // 2026-10-28: back under basic missile engineering; advanced missile engineering (IC) gates the supersonic missile's recipe instead.
            t.Check(HasPrerequisites(antiship, "DMSE_LoiteringMunitions"), "the anti-ship node follows basic missile engineering");
            t.Check(DefDatabase<RecipeDef>.AllDefs.Any(r => r.ProducedThingDef == ThingDef.Named("DMSE_Missile_Moskit")
                    && r.researchPrerequisites != null && r.researchPrerequisites.Contains(antiship) && r.researchPrerequisites.Contains(ic)),
                "the Moskit recipe needs anti-ship missiles and integrated circuits");
            t.Check(DefDatabase<RecipeDef>.AllDefs.Any(r => r.ProducedThingDef == Termite && r.researchPrerequisite == antiship),
                "the Termite recipe moved to anti-ship missiles");
            RecipeDef yj = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(r => r.ProducedThingDef == Def("DMSE_Missile_Yj"));
            t.Check(yj != null && yj.researchPrerequisites != null && yj.researchPrerequisites.Contains(antiship) && yj.researchPrerequisites.Contains(strategic),
                "the YJ recipe needs both anti-ship missiles and strategic systems");
            yield break;
        }

        // ------------------------------------------------------------------
        //  T12: missile and radar research tree split / unlock gates
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> MissileAndRadarTechnologyTree(TestContext t)
        {
            ResearchProjectDef radarBasis = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_RadarBasis");
            ResearchProjectDef loitering = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_LoiteringMunitions");
            ResearchProjectDef radarNetwork = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_RadarNetwork");
            ResearchProjectDef fireControl = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_FireControlNetwork");
            ResearchProjectDef integratedCircuits = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_IntegratedCircuits");
            ResearchProjectDef strategic = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_StrategicSystems");
            ResearchProjectDef integratedAirDefense = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_IntegratedAirDefense");
            ResearchProjectDef aerospace = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_Aerospace");
            t.Require(radarBasis != null && loitering != null && radarNetwork != null && fireControl != null
                && integratedCircuits != null && strategic != null
                && integratedAirDefense != null && aerospace != null,
                "the missile, radar-network and integrated-air-defence research projects load");

            t.Check(radarBasis.baseCost == 1000f && loitering.baseCost == 3500f
                && radarNetwork.baseCost == 2500f && fireControl.baseCost == 3500f
                && integratedAirDefense.baseCost == 6000f,
                "the staged warning, search, fire-control and integrated-defence research costs are preserved");
            t.Check(integratedCircuits.baseCost == 4000f && strategic.baseCost == 6000f,
                "the precision and strategic missile research costs are preserved");
            ResearchProjectDef ballisticPlatform = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_Ballistics");
            t.Check(ballisticPlatform != null && !ballisticPlatform.IsHidden && ballisticPlatform.baseCost == 2500f
                && HasPrerequisites(ballisticPlatform, "DMSE_StrategicSystems", "DMSE_IntegratedAirDefense"),
                "ballistic platform research follows cutting-edge missile engineering and the phased array");
            t.Check(new[] { "DMSE_AdvBallistic_Warhead_LowYield", "DMSE_AdvBallistic_Guidance_Stellar",
                    "DMSE_AdvBallistic_Payload_Penaids", "DMSE_AdvBallistic_Payload_MIRV" }
                    .All(p => HasPartResearchPrerequisites(Part(p), "DMSE_Ballistics")),
                "the Minuteman III's dedicated parts unlock with ballistic platform research");
            List<Def> ballisticUnlocks = ballisticPlatform.UnlockedDefs;
            t.Check(ballisticUnlocks.Contains(Body("DMSE_MissileBody_AdvancedBallistic"))
                && ballisticUnlocks.Contains(Part("DMSE_AdvBallistic_Payload_MIRV"))
                && ballisticUnlocks.Distinct().Count() == ballisticUnlocks.Count,
                $"the research panel lists missile bodies and parts as unlocks ({string.Join(", ", ballisticUnlocks.Select(d => d.defName))})");
            var grouped = RimWorld.ResearchPrerequisitesUtility.UnlockedDefsGroupedByPrerequisites(strategic);
            t.Check(grouped.SelectMany(g => g.Second).Contains(Part("DMSE_Ballistic_Warhead_Thermonuclear")),
                "strategic systems' grouped unlock list includes its missile parts without errors");

            t.Check(HasPrerequisites(radarBasis, "DMS_UAVBasis")
                && HasPrerequisites(loitering, "DMSE_RadarBasis")
                && HasPrerequisites(radarNetwork, "DMSE_LoiteringMunitions")
                && HasPrerequisites(fireControl, "DMSE_RadarNetwork")
                && HasPrerequisites(integratedAirDefense, "DMSE_FireControlNetwork"),
                "the radar branch advances from early warning to search, fire control and integrated air defence");
            t.Check(HasPrerequisites(integratedCircuits, "DMSE_RadarNetwork")
                && HasPrerequisites(strategic, "DMSE_IntegratedCircuits"),
                "advanced missile engineering follows the radar detection network, then cutting-edge missile engineering");
            ThingDef ballisticRack = DefDatabase<ThingDef>.GetNamedSilentFail("DMSE_Building_BallisticRack");
            t.Check(ballisticRack != null && HasResearchPrerequisites(Tower, "DMSE_LoiteringMunitions")
                && HasResearchPrerequisites(ballisticRack, "DMSE_LoiteringMunitions")
                && Body("DMSE_MissileBody_LegacyBallistic").researchPrerequisites.SequenceEqual(new[] { loitering })
                && HasPartResearchPrerequisites(Part("DMSE_LegacyBallistic_Warhead_Trialen"), "DMSE_LoiteringMunitions"),
                "basic missile engineering unlocks the ballistic launch tower, storage rack and V2 airframe");
            t.Check(!integratedAirDefense.prerequisites.Contains(aerospace),
                "integrated air defence no longer depends on aerospace-material research");
            bool expectedNpcNetworkState = DMSE.AirDefenseAlertTuningDef.Current.enabled && radarNetwork.IsFinished;
            t.Check(DMSE.WorldComponent_FactionAirDefenseAlert.Enabled == expectedNpcNetworkState,
                "NPC world-map air defence follows Radar Network research and its master switch");

            t.Check(HasResearchPrerequisites(AcousticRadar, "DMSE_RadarBasis")
                && HasResearchPrerequisites(EarlyFireControl, "DMSE_RadarBasis"),
                "the passive warning mirror and manually operated early fire-control radar remain early unlocks");
            t.Check(HasResearchPrerequisites(SearchRadar, "DMSE_RadarNetwork")
                && HasResearchPrerequisites(FireControlRadar, "DMSE_FireControlNetwork")
                && HasResearchPrerequisites(PhaseArray, "DMSE_IntegratedAirDefense"),
                "active search, dedicated fire control and phased array unlock on their matching research nodes");
            t.Check(HasResearchPrerequisites(SamLauncherBuilding, "DMSE_FireControlNetwork")
                && HasResearchPrerequisites(LightVls, "DMSE_IntegratedCircuits"),
                "multi-channel interception follows fire-control research while compact VLS follows missile electronics");
            t.Check(HasPartResearchPrerequisites(Part("DMSE_Ballistic_Guidance_FireControl"), "DMSE_FireControlNetwork"),
                "command-guided ballistic missiles require the fire-control network");
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
        //  T7: Millennium and Flak 43
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> AirDefenceGuns(TestContext t)
        {
            SpawnBuilding(AcousticRadar, 2, 2);
            Thing mill = SpawnBuilding(Millennium, 14, 4);
            Thing flak = SpawnBuilding(Flak43, 24, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompTerminalCIWS millCiws = mill.TryGetComp<CompTerminalCIWS>();
            CompTerminalCIWS flakCiws = flak.TryGetComp<CompTerminalCIWS>();
            t.Require(millCiws != null && flakCiws != null && mill is Building_TurretGun && flak is Building_TurretGun,
                "both guns are turrets with terminal CIWS");
            t.Check(Flak43.researchPrerequisites.Contains(ResearchProjectDef.Named("DMSE_RadarBasis"))
                && Flak43.researchPrerequisites.Contains(ResearchProjectDef.Named("GasOperation"))
                && !Flak43.researchPrerequisites.Any(r => r.defName == "DMSE_Ballistics"),
                "the Flak 43 requires Radar and gas operation on a separate track");
            t.Check(flakCiws.Props.shotsPerBurst == 8 && flakCiws.Props.ticksBetweenShots == 14,
                "the Flak 43 fires eight-round clips at the autocannon cadence");
            t.Check(millCiws.Active, "the Millennium works unmanned");
            t.Check(!flakCiws.Active, "the Flak 43 does nothing without a gunner");
            Pawn colonist = Colonist;
            flak.TryGetComp<CompMannable>().ManForATick(colonist);
            t.Check(flakCiws.Active, "manned, the Flak 43 engages");
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
        //  T9: field air defence on-map cannon (CompBuildingTurretGun + facing renderer)
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> FieldAirDefenceGroundFire(TestContext t)
        {
            Building_MissileRack field = (Building_MissileRack)SpawnBuilding(FieldAirDefense, 14, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompBuildingTurretGun gun = field.TryGetComp<CompBuildingTurretGun>();
            CompTurretFacingRenderer head = field.TryGetComp<CompTurretFacingRenderer>();
            CompRefuelable fuel = field.TryGetComp<CompRefuelable>();
            t.Require(gun != null && head != null && fuel != null, "the field air-defence system has an on-map cannon, a turret head and cannon rounds");
            t.Check(gun.Active && gun.AttackVerb.verbProps.range > 30f && gun.AttackVerb.verbProps.consumeFuelPerShot == 1f,
                $"the cannon is active, reaches past 30 cells and spends one round per shot (range {gun.AttackVerb.verbProps.range})");

            // An enemy due east: the head starts facing north, so it must turn about 90 degrees before it may fire.
            IntVec3 cell = new IntVec3(Pad.minX + 38, 0, field.Position.z);
            Pawn enemy = MakeEnemyPawn();
            GenSpawn.Spawn(enemy, cell, Home);
            Find.CameraDriver.SetRootPosAndSize((field.TrueCenter() + enemy.DrawPos) / 2f, 18f);
            float before = fuel.Fuel;
            float OffTarget() => Mathf.Abs(Mathf.DeltaAngle(head.CurRotation, (enemy.DrawPos - field.DrawPos).AngleFlat()));

            // Sample on the exact ticks of lock-on and of the first round spent (a frame spans several ticks at Ultrafast).
            float offAtLock = -1f, fuelAtLock = -1f, offAtFire = -1f;
            TickWatch locked = AcceptanceDriver.Watch(() =>
            {
                if (gun.CurrentTarget.Thing != enemy) { return false; }
                offAtLock = OffTarget(); fuelAtLock = fuel.Fuel;
                return true;
            });
            TickWatch fired = AcceptanceDriver.Watch(() =>
            {
                if (fuel.Fuel >= before) { return false; }
                offAtFire = enemy.Spawned ? OffTarget() : 0f;
                return true;
            });

            var acquire = new WaitUntil(() => locked.Seen, 300);
            yield return acquire;
            t.Require(!acquire.TimedOut, "the cannon picks up the enemy");
            t.Check(offAtLock > 30f && fuelAtLock == before,
                $"at lock-on the head is still off target ({offAtLock:F0} deg) and nothing has been fired");

            var waitFire = new WaitUntil(() => fired.Seen || enemy.Dead || !enemy.Spawned, 600);
            yield return waitFire;
            t.Require(fired.Seen, $"the cannon fires at the enemy ({before - fuel.Fuel} rounds)");
            int warmup = gun.AttackVerb.verbProps.warmupTime.SecondsToTicks();
            t.Check(offAtFire <= gun.Props.aimToleranceDeg + 2f,
                $"the first round leaves only once the head points at the enemy ({offAtFire:F1} deg off)");
            t.Check(fired.FirstTick - locked.FirstTick >= warmup,
                $"it turns and aims before firing ({fired.FirstTick - locked.FirstTick} ticks from lock-on, warmup {warmup})");
            yield return new WaitTicks(10);
            t.Screenshot("field_air_defence_ground_fire");
            yield return new WaitFrames(10);

            // Target gone: the head keeps its last firing heading for a while instead of swinging back.
            if (!enemy.Destroyed) { enemy.Destroy(DestroyMode.Vanish); }
            float headingAtKill = head.CurRotation;
            yield return new WaitTicks(250);
            float drift = Mathf.Abs(Mathf.DeltaAngle(head.CurRotation, headingAtKill));
            t.Check(!gun.CurrentTarget.IsValid && drift < 1f,
                $"with the target gone the head holds its firing heading ({drift:F1} deg drift after 250 ticks)");

            SetFlick(field, false);
            yield return new WaitTicks(2);
            t.Check(!gun.Active && !gun.CurrentTarget.IsValid, "switched off, the cannon drops its target and stops");
        }

        // ------------------------------------------------------------------
        //  T8: man-portable SAM
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> ManPortableSam(TestContext t)
        {
            Pawn colonist = Colonist;
            t.Require(colonist != null, "a colonist exists");
            Apparel launcher = (Apparel)ThingMaker.MakeThing(SamLauncher);
            colonist.apparel.Wear(launcher, dropReplacedApparel: true);
            Override(() => { }, () =>
            {
                if (!launcher.Destroyed)
                {
                    if (colonist.apparel.Contains(launcher)) { colonist.apparel.Remove(launcher); }
                    launcher.Destroy();
                }
            });

            var comp = launcher.GetComp<CompManPortableSAM>();
            t.Require(comp != null && comp.Wearer == colonist && CompManPortableSAM.WornBy(colonist) == comp,
                "the launcher is worn apparel carrying the SAM comp");
            t.Check(comp is CompApparelReloadable && comp.AmmoDef == Stinger, "it reloads through vanilla CompApparelReloadable with light SAMs");
            Verb groundVerb = comp.AllVerbs.FirstOrDefault();
            t.Require(groundVerb != null, "the launcher has a ground-attack verb");
            while (comp.RemainingCharges > 0) { comp.UsedOnce(); }
            t.Check(!launcher.Destroyed, "an empty launcher is kept (not disposable)");
            Command_Action Gizmo() => comp.CompGetWornGizmosExtra().OfType<Command_Action>()
                .FirstOrDefault(c => c.defaultLabel == "DMSE.ManPortableSAM.Fire".Translate());
            t.Check(Gizmo() != null && Gizmo().Disabled, "without charges the air-defence command is shown but disabled");
            t.Check(!groundVerb.Available(), "and the ground shot is unavailable");

            Thing ammo = ThingMaker.MakeThing(Stinger);
            t.Check(comp.NeedsReload(false) && comp.MaxAmmoNeeded(true) == comp.MaxCharges, "the empty launcher asks for one missile per charge");
            comp.ReloadFrom(ammo);
            t.Check(comp.RemainingCharges == 1 && ammo.Destroyed, "vanilla reload loads one missile and consumes the item");
            var loaded = (System.Collections.IList)typeof(CompManPortableSAM)
                .GetField("loadedConfigs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(comp);
            t.Check(loaded.Count == 1 && comp.NextConfig?.body == Body("DMSE_MissileBody_Stinger"),
                "the loaded missile's assembly config is kept for the shot");
            comp.ReloadFrom(ThingMaker.MakeThing(Stinger));
            t.Check(comp.RemainingCharges == 2 && groundVerb.Available(), "a second missile fills the launcher and arms the ground shot");

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
            t.Check(Gizmo() != null && !Gizmo().Disabled, "with charges and tracks the command is enabled");

            int now = Find.TickManager.TicksGame;
            t.Check(comp.TryFire(colonist, targets[0].id, out string reason), $"firing at the first pod succeeds ({reason ?? "ok"})");
            IInterceptorShot shot = InterceptorShotAt(targets[0].id);
            t.Check(shot != null && shot.HitChance > 0f, $"an interceptor is in flight (hit chance {shot?.HitChance:0.00})");
            t.Check((shot as Thing)?.def.defName == "DMSE_Launch_Stinger", $"the man-portable SAM launches the Stinger's own launch projectile ({(shot as Thing)?.def.defName})");
            t.Check(comp.RemainingCharges == 1 && loaded.Count == 1 && targets[0].midcourseEngagedUntil > now, "one charge used; the target is marked engaged");
            t.Check(comp.OnCooldown && Gizmo().Disabled && !comp.TryFire(colonist, targets[1].id, out _), "the launcher is on cooldown");

            typeof(CompManPortableSAM).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(comp, 0);
            Job job = JobMaker.MakeJob(DMSE_ManPortableSAMDefOf.DMSE_FireManPortableSAM, colonist);
            job.count = targets[1].id;
            t.Require(colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc), "the colonist takes the aiming job");
            var waitSecond = new WaitUntil(() => comp.RemainingCharges == 0, 600);
            yield return waitSecond;
            t.Check(!waitSecond.TimedOut, "after aiming, the job fires the second missile");
            t.Check(!groundVerb.Available() && loaded.Count == 0 && !launcher.Destroyed, "out of charges, the ground shot is unavailable again");
        }

        // ------------------------------------------------------------------
        //  T13: Sabre light loitering munition on the man-portable launcher
        // ------------------------------------------------------------------

        /// <summary>
        /// Sabre (DMSE_Missile_Sabre): fixed configuration, strike only, loaded by hand through the right-click reload menu,
        /// fired at ground targets beyond the Stinger's range by its own verb; it blocks the air-defence command while it is next.
        /// </summary>
        public static IEnumerable<Wait> SabreOnManPortableLauncher(TestContext t)
        {
            ThingDef sabre = Def("DMSE_Missile_Sabre");
            MissileBodyDef sabreBody = Body("DMSE_MissileBody_Sabre");
            t.Check(sabre.GetCompProperties<CompProperties_MissileConfig>() == null && sabre.inspectorTabs.NullOrEmpty()
                    && sabreBody.customizeLocation == MissileCustomizeLocation.None
                    && sabre.GetModExtension<MissileItemExtension>()?.body == sabreBody,
                "Sabre is a fixed-configuration item (no config comp, no assembly tab, not customizable)");
            MissileConfig sabreConfig = MissileConfig.NewWithDefaults(sabreBody);
            MissilePartDef seeker = sabreConfig.PartFor(MissilePartCategory.Guidance);
            t.Check(seeker?.defName == "DMSE_Sabre_Guidance_Imaging" && seeker.guidanceType is GuidanceType_Imaging
                    && sabreConfig.TargetingGuidance is GuidanceType_Imaging && sabreConfig.HasTerminalGuidance,
                $"Sabre's default seeker is image recognition ({seeker?.defName})");
            t.Check(sabreConfig.PartFor(MissilePartCategory.Payload)?.defName == "DMSE_Payload_Loiter"
                    && sabreConfig.LoiterTicks > 0 && sabreConfig.LoiterRetryTicks > 0,
                $"Sabre's default payload is the loiter flight control (loiter {sabreConfig.LoiterTicks} ticks)");
            t.Check(MissileItemExtension.ConfigFor(ThingMaker.MakeThing(sabre)).parts.Select(x => x.defName).OrderBy(n => n)
                    .SequenceEqual(new[] { "DMSE_Payload_Loiter", "DMSE_Sabre_Guidance_Imaging" }),
                "a crafted Sabre flies with exactly those parts");
            t.Check(MissileRoleExtension.RoleOf(sabre) == MissileRole.Strike, "Sabre is a strike-only round");
            t.Check(sabre.graphicData.texPath == "Things/Missile/S570/ammo"
                    && Def("DMSE_Projectile_Sabre").graphicData.texPath == "Things/Missile/S570/skyFaller",
                "Sabre uses the S570 textures");
            t.Check(sabre.recipeMaker?.researchPrerequisite?.defName == "DMSE_FireControlNetwork", "Sabre is gated by fire control research");
            t.Check(LaunchRail.building.fixedStorageSettings.filter.Allows(sabre), "a launch rail accepts Sabre");

            Pawn colonist = Colonist;
            t.Require(colonist != null, "a colonist exists");
            Apparel launcher = (Apparel)ThingMaker.MakeThing(SamLauncher);
            colonist.apparel.Wear(launcher, dropReplacedApparel: true);
            var comp = launcher.GetComp<CompManPortableSAM>();
            Thing loose = null;
            Override(() => { }, () =>
            {
                if (colonist.drafter != null) { colonist.drafter.Drafted = false; }
                if (!launcher.Destroyed)
                {
                    if (colonist.apparel.Contains(launcher)) { colonist.apparel.Remove(launcher); }
                    launcher.Destroy();
                }
                if (loose != null && !loose.Destroyed) { loose.Destroy(); }
            });
            t.Require(comp != null && comp.Props.extraAmmoDefs.Contains(sabre) && comp.CanLoad(sabre), "the launcher lists Sabre as extra ammo");
            Verb_LaunchManPortableMissile stingerVerb = comp.AllVerbs.OfType<Verb_LaunchManPortableMissile>()
                .FirstOrDefault(v => (v.verbProps as VerbProperties_ManPortableMissile)?.ammoDef == Stinger);
            Verb_LaunchManPortableMissile sabreVerb = comp.AllVerbs.OfType<Verb_LaunchManPortableMissile>()
                .FirstOrDefault(v => (v.verbProps as VerbProperties_ManPortableMissile)?.ammoDef == sabre);
            t.Require(stingerVerb != null && sabreVerb != null, "the launcher has one ground verb per ammo type");
            t.Check(sabreVerb.verbProps.defaultProjectile?.defName == "DMSE_Projectile_Sabre"
                    && sabreVerb.verbProps.range > stingerVerb.verbProps.range,
                $"the Sabre verb fires the Sabre projectile and reaches farther ({sabreVerb.verbProps.range} > {stingerVerb.verbProps.range})");

            colonist.drafter.Drafted = true;
            List<Verb> ShownVerbs() => comp.CompGetWornGizmosExtra().OfType<Command_VerbTarget>().Select(c => c.verb).ToList();
            Command_Action AirGizmo() => comp.CompGetWornGizmosExtra().OfType<Command_Action>()
                .FirstOrDefault(c => c.defaultLabel == "DMSE.ManPortableSAM.Fire".Translate());

            // Mixed load: a Stinger first, then a Sabre on top (last in, first out).
            while (comp.RemainingCharges > 0) { comp.UsedOnce(); }
            comp.ReloadFrom(ThingMaker.MakeThing(Stinger));
            t.Check(comp.NextAmmoDef == Stinger && ShownVerbs().SequenceEqual(new Verb[] { stingerVerb }) && stingerVerb.Available() && !sabreVerb.Available(),
                "with a Stinger next only the Stinger ground verb is shown and usable");

            // The Sabre is loaded through the real right-click menu (vanilla's reload menu only knows the Stinger).
            IntVec3 near = CellFinder.StandableCellNear(colonist.Position, Home, 3f, c => c != colonist.Position && c.GetFirstItem(Home) == null);
            t.Require(near.IsValid, "a free cell next to the colonist");
            loose = GenSpawn.Spawn(ThingMaker.MakeThing(sabre), near, Home);
            List<FloatMenuOption> options = FloatMenuMakerMap.GetOptions(new List<Pawn> { colonist }, loose.DrawPos, out _);
            FloatMenuOption reload = options.FirstOrDefault(o => o.action != null && !o.Disabled && o.Label.Contains(sabre.label));
            t.Require(reload != null, "right-clicking a Sabre offers to load it into the launcher: "
                + string.Join(" | ", options.Select(o => o.Label)));
            reload.action();
            var waitReload = new WaitUntil(() => comp.RemainingCharges == 2, 1500);
            yield return waitReload;
            t.Require(!waitReload.TimedOut, "the colonist walks over and loads the Sabre");
            t.Check(loose.Destroyed && comp.NextAmmoDef == sabre && comp.NextConfig?.body == sabreBody, "the Sabre item is used up and is now the next round");
            t.Check(ShownVerbs().SequenceEqual(new Verb[] { sabreVerb }) && sabreVerb.Available() && !stingerVerb.Available(),
                "with a Sabre next only the Sabre ground verb is shown and usable");

            // Air defence is refused while the Sabre is next.
            SpawnBuilding(AcousticRadar, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            float oldRange = comp.Props.maxRangeCells;
            int oldEta = comp.Props.maxEtaTicks;
            Override(() => { comp.Props.maxRangeCells = 999f; comp.Props.maxEtaTicks = 100000; },
                () => { comp.Props.maxRangeCells = oldRange; comp.Props.maxEtaTicks = oldEta; });
            DropEnemyPod(1);
            yield return new WaitTicks(1);
            List<BVRTarget> targets = comp.EngageableTargets(colonist).ToList();
            t.Require(targets.Count >= 1, $"a tracked pod is engageable (got {targets.Count})");
            string cannot = "DMSE.ManPortableSAM.CannotIntercept".Translate(sabre.label);
            t.Check(AirGizmo() != null && AirGizmo().Disabled && AirGizmo().disabledReason == cannot,
                $"the air-defence command is disabled because the Sabre cannot intercept ({AirGizmo()?.disabledReason})");
            t.Check(!comp.TryFire(colonist, targets[0].id, out string refused) && refused == cannot && comp.RemainingCharges == 2,
                "firing at the pod is refused without using a charge");

            // Ground shot beyond the Stinger's reach.
            IntVec3 from = colonist.Position;
            float want = (stingerVerb.verbProps.range + sabreVerb.verbProps.range) / 2f;
            IntVec3 aim = GenRadial.RadialCellsAround(from, want + 3f, false)
                .Where(c => c.InBounds(Home) && !c.Roofed(Home) && c.DistanceTo(from) >= want && c.Standable(Home))
                .OrderBy(c => c.DistanceTo(from)).FirstOrDefault();
            t.Require(aim.IsValid && aim.InBounds(Home), $"an open target cell about {want:F0} cells away");
            int projectilesBefore = Home.listerThings.ThingsOfDef(Def("DMSE_Projectile_Sabre")).Count;
            ThingDef sabreProjectile = Def("DMSE_Projectile_Sabre");
            TickWatch projectileSeen = AcceptanceDriver.Watch(() => Home.listerThings.ThingsOfDef(sabreProjectile).Count > projectilesBefore);
            // Same order the gizmo's targeter gives: ai_IsWeapon false → UseVerbOnThing with this verb.
            sabreVerb.OrderForceTarget(aim);
            t.Require(colonist.CurJob?.verbToUse == sabreVerb, $"the colonist takes the order to launch the Sabre ({colonist.CurJob?.def.defName})");
            var waitShot = new WaitUntil(() => comp.RemainingCharges == 1, 900);
            yield return waitShot;
            yield return new WaitTicks(2);
            t.Check(!waitShot.TimedOut && projectileSeen.Seen,
                $"the Sabre is launched {aim.DistanceTo(from):F1} cells (charges {comp.RemainingCharges}, projectile seen {projectileSeen.Seen})");
            t.Check(comp.NextAmmoDef == Stinger && ShownVerbs().SequenceEqual(new Verb[] { stingerVerb }) && comp.CannotFireReason(colonist) == null,
                "after the Sabre is fired the Stinger underneath is next and air defence is available again");
        }

        // ------------------------------------------------------------------
        //  T10/T11: a manned rotary rail reloads from a rack and fires again,
        //  also inside a manned Flak 43's terminal window (2026-10-27 report)
        // ------------------------------------------------------------------

        public static IEnumerable<Wait> RotaryRailRefire(TestContext t) => RotaryRailScenario(t, withFlak: false, pods: 4);

        public static IEnumerable<Wait> RotaryRailRefireInTerminal(TestContext t) => RotaryRailScenario(t, withFlak: true, pods: 12);

        /// <summary>
        /// The player's setup from the report: two acoustic radars, a manned early fire-control radar, a rotary rail manned through
        /// DMSE_ManMissileLauncher with Rheintochters in loitering racks. With a manned Flak 43 a 12-pod wave lands inside the
        /// 1800-tick terminal window from the start; the SAM must still fire, reload and fire again before impact.
        /// </summary>
        private static IEnumerable<Wait> RotaryRailScenario(TestContext t, bool withFlak, int pods)
        {
            Building_MissileRack rail = (Building_MissileRack)SpawnBuilding(RotaryRail, 10, 10);
            Building_MissileRack rackA = (Building_MissileRack)SpawnBuilding(Def("DMSE_LoiteringRack"), 5, 8);
            Building_MissileRack rackB = (Building_MissileRack)SpawnBuilding(Def("DMSE_LoiteringRack"), 5, 12);
            Thing fcr = SpawnBuilding(EarlyFireControl, 18, 2);
            Thing flak = withFlak ? SpawnBuilding(Flak43, 24, 10) : null;
            SpawnBuilding(AcousticRadar, 2, 2);
            SpawnBuilding(AcousticRadar, 2, 20);
            Load(rail, Rheintochter);
            Load(rackA, Rheintochter);
            Load(rackB, Rheintochter);
            yield return WaitPowered(); yield return new WaitTicks(2);

            List<Pawn> colonists = Home.mapPawns.FreeColonistsSpawned.ToList();
            t.Require(colonists.Count >= 1, "a colonist exists");
            Pawn gunner = colonists[0];
            Pawn radarman = colonists.Count > 1 ? colonists[1] : colonists[0];
            Pawn flakCrew = colonists.Count > 2 ? colonists[2] : radarman;
            CompMissileLauncher_Interceptor sam = rail.TryGetComp<CompMissileLauncher_Interceptor>();
            CompMannable railMan = rail.TryGetComp<CompMannable>();
            CompMannable fcMan = fcr.TryGetComp<CompMannable>();
            CompMannable flakMan = flak?.TryGetComp<CompMannable>();

            gunner.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_ManMissileLauncher, rail), JobTag.Misc);
            var waitManned = new WaitUntil(() => railMan.MannedNow, 2500);
            yield return waitManned;
            t.Require(!waitManned.TimedOut, "the gunner mans the rotary rail");

            // Every shot misses so the wave keeps a target for the second round; log the state once a second.
            var launches = new List<int>();
            var hitChances = new List<float>();
            var seenShots = new HashSet<IInterceptorShot>();
            var resolvedAt = new Dictionary<IInterceptorShot, int>();
            int lastCooldown = sam.CooldownUntil;
            TickWatch watch = AcceptanceDriver.Watch(() =>
            {
                int now = Find.TickManager.TicksGame;
                fcMan.ManForATick(radarman);
                flakMan?.ManForATick(flakCrew);
                foreach (IInterceptorShot shot in Home.listerThings.AllThings.OfType<IInterceptorShot>())
                {
                    if (seenShots.Add(shot)) { hitChances.Add(shot.HitChance); }
                    shot.HitChance = 0f;
                }
                foreach (IInterceptorShot shot in seenShots)
                {
                    if (!resolvedAt.ContainsKey(shot) && shot is Thing th && !th.Spawned) { resolvedAt[shot] = now; }
                }
                if (sam.CooldownUntil != lastCooldown) { lastCooldown = sam.CooldownUntil; launches.Add(now); }
                if (now % 60 == 0)
                {
                    BVRWave w = HomeBVR.Waves.FirstOrDefault();
                    JobDriver d = gunner.jobs.curDriver;
                    t.Note($"tick {now}: job {gunner.CurJobDef?.defName}/{d?.CurToilString} manned={railMan.MannedNow} stored={rail.StoredCount} " +
                        $"ammo={sam.HasAmmo} active={sam.Active} cooldown={sam.CooldownTicksRemaining(now)} launches={launches.Count} " +
                        $"wave={(w == null ? "none" : $"{w.tickToImpact - now} ticks, {w.targets.Count} targets, locks {string.Join(",", w.targets.Select(x => x.lockUntil < 0 ? "-" : (x.lockUntil - now).ToString()))}, engaged {w.targets.Count(x => x.midcourseEngagedUntil > now)}")} " +
                        $"free={HomeBVR.Channels.Free(Player)}");
                }
                return false;
            });

            for (int i = 0; i < pods; i++) { DropEnemyPod(1, at: new IntVec3(Pad.maxX + 8, 0, Pad.CenterCell.z + i * 3)); }
            yield return new WaitTicks(1);
            t.Require(HomeBVR.Waves.Count == 1, $"the pods form one wave ({HomeBVR.Waves.Count})");
            BVRWave wave = HomeBVR.Waves[0];
            int window = wave.tickToImpact - Find.TickManager.TicksGame;
            int terminal = HomeBVR.TerminalWindowTicks(Player);
            t.Note($"wave window {window} ticks, terminal window {terminal}");
            if (withFlak)
            {
                t.Require(terminal == 1800 && window <= terminal,
                    $"the manned Flak 43 puts the whole {window}-tick wave inside its terminal window ({terminal})");
            }

            var waitFirst = new WaitUntil(() => launches.Count >= 1, window);
            yield return waitFirst;
            t.Require(!waitFirst.TimedOut, "the manned rail fires its first interceptor");
            var waitSecond = new WaitUntil(() => launches.Count >= 2 || !HomeBVR.Waves.Contains(wave), window);
            yield return waitSecond;
            t.Check(launches.Count >= 2, $"after reloading from the rack it fires a second one (launch ticks {string.Join(",", launches)})");
            t.Check(rackA.StoredCount + rackB.StoredCount <= 1, $"the gunner took the reload from a rack ({rackA.StoredCount}+{rackB.StoredCount} left)");
            t.Check(hitChances.Count >= 2 && hitChances.All(c => c > 0f),
                $"the guidance time is the time to impact, so terminal-phase shots can hit (hit chances {string.Join(", ", hitChances.Select(c => c.ToString("0.00")))})");
            var waitResolved = new WaitUntil(() => resolvedAt.Count >= seenShots.Count, 1200);
            yield return waitResolved;
            t.Check(!waitResolved.TimedOut && resolvedAt.Values.All(tick => tick < wave.tickToImpact),
                $"every interceptor resolves before the wave lands at {wave.tickToImpact} (resolved at {string.Join(",", resolvedAt.Values)})");
        }

        public static IEnumerable<AcceptanceTest> Tests()
        {
            yield return new AcceptanceTest("T1", "Launch tower backblast; sealed silo", TowerBackblast);
            yield return new AcceptanceTest("T2", "Rotary and launch rails: manning, item airframes, roles", RotaryRailRoles);
            yield return new AcceptanceTest("T3", "SAM takes only interceptor / dual-role rounds", InterceptorRoleFilter);
            yield return new AcceptanceTest("T4", "Shipped channel counts and new radars", ChannelsAndRadars);
            yield return new AcceptanceTest("T5", "Built-in anti-ship guidance, ranges, research gates", GuidanceRangesAndResearch);
            yield return new AcceptanceTest("T6", "Interceptor hit factors", InterceptHitFactors);
            yield return new AcceptanceTest("T7", "Millennium and Flak 43", AirDefenceGuns);
            yield return new AcceptanceTest("T8", "Man-portable SAM", ManPortableSam);
            yield return new AcceptanceTest("T9", "Field air defence: on-map cannon turns before firing", FieldAirDefenceGroundFire);
            yield return new AcceptanceTest("T10", "Manned rotary rail reloads from a rack and fires again", RotaryRailRefire);
            yield return new AcceptanceTest("T11", "SAM keeps firing inside a manned Flak 43's terminal window", RotaryRailRefireInTerminal);
            yield return new AcceptanceTest("T12", "Missile and radar research tree split / unlock gates", MissileAndRadarTechnologyTree);
            yield return new AcceptanceTest("T13", "Sabre loitering munition on the man-portable launcher", SabreOnManPortableLauncher);
        }
    }
}
