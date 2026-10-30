using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>Industrial field launch site: difficulty-scaled layout and garrison, radar-gated timed supply drops.</summary>
    public static class Tests_FieldLaunchSite
    {
        private static readonly FieldInfo NextSupplyDrop = typeof(MapComponent_MissileBase)
            .GetField("nextSupplyDropTick", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NextRackSupply = typeof(MapComponent_MissileBase)
            .GetField("nextRackSupplyTick", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Packed-dirt road cells, how many world launchers have a road cell within 20 cells (launchers deep inside an 18-cell layout sit far from its doorstep), and how many separate
        /// road networks (8-connected components) those nearest road cells belong to: one network = all linked.
        /// </summary>
        private static (int roadCells, int launchersNearRoad, int launchers, int networks) RoadNetwork(Map map, Faction faction)
        {
            TerrainDef road = TerrainDef.Named("PackedDirt");
            List<IntVec3> roadCells = map.AllCells.Where(c => c.GetTerrain(map) == road).ToList();
            Dictionary<IntVec3, int> component = new Dictionary<IntVec3, int>();
            int next = 0;
            foreach (IntVec3 seed in roadCells)
            {
                if (component.ContainsKey(seed)) { continue; }
                Queue<IntVec3> open = new Queue<IntVec3>();
                open.Enqueue(seed); component[seed] = next;
                while (open.Count > 0)
                {
                    IntVec3 c = open.Dequeue();
                    foreach (IntVec3 n in GenAdj.AdjacentCells.Select(d => c + d))
                    {
                        // Water is left unpaved on purpose (no bridges), so a road crossing a stream still counts as one network.
                        if (n.InBounds(map) && !component.ContainsKey(n) && (n.GetTerrain(map) == road || n.GetTerrain(map).IsWater)) { component[n] = next; open.Enqueue(n); }
                    }
                }
                next++;
            }
            List<Building> launchers = map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == faction && b.TryGetComp<CompMissileLauncher_WorldTargeting>() != null).ToList();
            HashSet<int> used = new HashSet<int>();
            int near = 0;
            foreach (Building b in launchers)
            {
                IntVec3 nearest = roadCells.Where(c => c.DistanceTo(b.Position) <= 20f).OrderBy(c => c.DistanceToSquared(b.Position)).FirstOrFallback(IntVec3.Invalid);
                if (!nearest.IsValid) { continue; }
                near++;
                used.Add(component[nearest]);
            }
            return (roadCells.Count, near, launchers.Count, used.Count);
        }

        /// <summary>
        /// Share of the site's own building cells that sit on Concrete/PavedTile, the floors the FFF layouts would lay.
        /// Only the site's cells count: vanilla ruins and ancient structures on the same map bring their own floors.
        /// </summary>
        private static float PavedShare(Map map, Faction faction)
        {
            TerrainDef concrete = TerrainDef.Named("Concrete"), paved = TerrainDef.Named("PavedTile");
            List<IntVec3> cells = map.listerBuildings.allBuildingsNonColonist.Where(b => b.Faction == faction)
                .SelectMany(b => b.OccupiedRect().Cells).Distinct().ToList();
            if (cells.Count == 0) { return 0f; }
            return cells.Count(c => { TerrainDef t = c.GetTerrain(map); return t == concrete || t == paved; }) / (float)cells.Count;
        }

        private static Site SpawnFieldSite(Faction faction, float points)
        {
            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamed("DMSE_FieldLaunchSite");
            SitePartDef part = DefDatabase<SitePartDef>.GetNamed("DMSE_FieldLaunchSitePart");
            PlanetTile home = Home.Tile;
            PlanetTile tile = PlanetTile.Invalid;
            for (int i = 0; i < Find.WorldGrid.TilesCount && !tile.Valid; i++)
            {
                PlanetTile t = new PlanetTile(i, home.Layer);
                float d = MissileWorldUtility.RangeDistance(home, t);
                if (d < 6f || d > 14f) { continue; }
                Tile info = Find.WorldGrid[t];
                if (!info.PrimaryBiome.canBuildBase || info.hilliness == Hilliness.Impassable) { continue; }
                if (Find.WorldObjects.AnyWorldObjectAt(t) || Find.WorldObjects.AnySettlementBaseAtOrAdjacent(t)) { continue; }
                tile = t;
            }
            if (!tile.Valid) { return null; }
            Site site = (Site)WorldObjectMaker.MakeWorldObject(def);
            site.Tile = tile;
            site.SetFaction(faction);
            site.AddPart(new SitePart(site, part, new SitePartParams { points = points, threatPoints = points }));
            Find.WorldObjects.Add(site);
            Track(site);
            return site;
        }

        private struct Layout { public int buildings, launchers, searchRadars, beds, crew, plants, animals, rock; public Map map; }

        private static Layout GenerateAt(TestContext t, Faction faction, float threatScale)
        {
            Difficulty difficulty = Find.Storyteller.difficulty;
            float saved = difficulty.threatScale;
            difficulty.threatScale = threatScale;
            Map map;
            try
            {
                Site site = SpawnFieldSite(faction, 1000f);
                t.Require(site != null, $"a field launch site spawned (threatScale {threatScale})");
                map = MissileWorldUtility.ResolveArrivalMap(site.Tile, site, true);
            }
            finally { difficulty.threatScale = saved; }
            t.Require(map != null, "the field site map is generated");
            GameComponent_MissileEngage.Comp.times.SetOrAdd(map, Find.TickManager.TicksGame + GenDate.TicksPerDay * 2);

            List<Building> mine = map.listerBuildings.allBuildingsNonColonist.Where(b => b.Faction == faction).ToList();
            Layout l = new Layout
            {
                map = map,
                buildings = mine.Count,
                launchers = mine.Count(b => b.TryGetComp<CompMissileLauncher_WorldTargeting>() != null),
                searchRadars = mine.Count(b => b.def.defName == "DMSE_SearchRadar"),
                beds = mine.Count(b => b is Building_Bed),
                crew = map.mapPawns.SpawnedPawnsInFaction(faction).Count(p => p.RaceProps.Humanlike),
                plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).Count,
                animals = map.mapPawns.AllPawnsSpawned.Count(p => p.Faction == null && p.RaceProps.Animal),
                rock = map.AllCells.Count(c => c.GetEdifice(map)?.def.building?.isNaturalRock == true),
            };
            t.Note($"threatScale {threatScale}: {l.buildings} buildings, {l.launchers} world launchers, {l.searchRadars} search radars, {l.beds} beds, {l.crew} crew; natural map: {l.plants} plants, {l.animals} wild animals, {l.rock} rock cells, biome {Find.WorldGrid[map.Tile].PrimaryBiome.defName}, hilliness {Find.WorldGrid[map.Tile].hilliness}");
            return l;
        }

        public static IEnumerable<Wait> LayoutCrewAndSupply(TestContext t)
        {
            Faction industrial = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel == TechLevel.Industrial && f.def.humanlikeFaction);
            t.Require(industrial != null, "an industrial humanlike faction exists");
            SitePartDef part = DefDatabase<SitePartDef>.GetNamed("DMSE_FieldLaunchSitePart");
            t.Check(part.FactionCanOwn(industrial), "the field site part accepts an industrial faction");
            Faction spacer = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.Hidden && f.def.techLevel >= TechLevel.Spacer);
            if (spacer != null) { t.Check(!part.FactionCanOwn(spacer), $"the field site part rejects {spacer.def.techLevel} faction {spacer.Name}"); }
            t.Check(!part.FactionCanOwn(null), "the field site part requires a faction");

            // threatPoints 1000 / 100 = 10 → clamped into tier 0 (3~5) = 5 and tier 3 (10~14) = 10.
            Layout low = GenerateAt(t, industrial, 0.3f);
            Layout high = GenerateAt(t, industrial, 2.2f);
            t.Check(low.searchRadars == 1 && high.searchRadars == 1, "one search radar at the centre on both difficulties");
            // Launchers per launch position vary (A: 2, B/D: 1, C: 0), so only the guaranteed first position is checked.
            t.Check(low.launchers >= 1 && high.launchers >= 1, "both sites have a world-targeting launcher");
            t.Check(high.buildings > low.buildings, "the high-difficulty site has more structures");
            float pavedLow = PavedShare(low.map, industrial), pavedHigh = PavedShare(high.map, industrial);
            t.Check(pavedLow < 0.05f && pavedHigh < 0.05f, $"no structure floors laid under the site: natural terrain kept ({pavedLow:P0} / {pavedHigh:P0} of building cells paved)");
            foreach (Layout l in new[] { low, high })
            {
                var roads = RoadNetwork(l.map, industrial);
                t.Check(roads.roadCells > 40 && roads.launchersNearRoad == roads.launchers && roads.networks == 1,
                    $"packed-dirt roads link every launcher into one network ({roads.roadCells} road cells, {roads.launchersNearRoad}/{roads.launchers} launchers by a road, {roads.networks} network(s))");
            }
            // The map generator inherits vanilla MapCommonBase: vegetation and wildlife are generated (before, both were missing).
            t.Check(low.plants > 200 && high.plants > 200, $"vegetation generated ({low.plants} / {high.plants} plants)");
            t.Check(low.animals + high.animals > 0, $"wild animals generated ({low.animals} / {high.animals})");

            // Every rack is loaded at generation (light launchers used to spawn empty: their auto-fill pool was rejected by their own filter).
            foreach (Layout l in new[] { low, high })
            {
                List<IMissileRack> racks = MissileBaseRackSupplyUtility.RacksOf(l.map, industrial);
                string empty = string.Join(", ", racks.Where(r => r.StoredCount == 0).Select(r => r.RackThing.def.defName));
                t.Note($"racks: {string.Join(", ", racks.GroupBy(r => r.RackThing.def.defName).Select(g => $"{g.Key}×{g.Count()} ({g.Sum(r => r.StoredCount)})"))}");
                t.Check(racks.Count > 0 && empty.Length == 0, $"every rack spawns loaded{(empty.Length > 0 ? " (empty: " + empty + ")" : "")}");
            }
            // Overview screenshot of the high-difficulty site (roads, scattered structures, natural terrain).
            Thing hub = high.map.listerThings.ThingsOfDef(ThingDef.Named("DMSE_SearchRadar")).FirstOrDefault();
            if (hub != null)
            {
                CameraJumper.TryJump(new GlobalTargetInfo(hub.Position, high.map));
                yield return new WaitFrames(5);
                Find.CameraDriver.SetRootPosAndSize(hub.Position.ToVector3Shifted(), 100f);
                yield return new WaitFrames(60);
                t.Screenshot("site_overview");
                CameraJumper.TryJump(new GlobalTargetInfo(Home.Center, Home));
                yield return new WaitFrames(5);
            }
            t.Check(low.crew == 5, $"low-difficulty garrison is 5 (got {low.crew})");
            t.Check(high.crew == 10, $"high-difficulty garrison is 10 (got {high.crew})");

            // ── Supply drop on the high-difficulty map ──
            Map map = high.map;
            MapComponent_MissileBase comp = map.GetComponent<MapComponent_MissileBase>();
            t.Require(comp != null && NextSupplyDrop != null, "MapComponent_MissileBase with nextSupplyDropTick");
            List<Building> radars = map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == industrial && (b.TryGetComp<CompSearchRadar>() != null || b.TryGetComp<CompFireControlRadar>() != null)).ToList();
            t.Require(radars.Count > 0, $"the site has radars ({radars.Count})");

            var waitOn = new WaitUntil(() => radars.Any(r => r.TryGetComp<CompSearchRadar>()?.Active == true || r.TryGetComp<CompFireControlRadar>()?.Active == true), 2500);
            yield return waitOn;
            t.Require(!waitOn.TimedOut, "a radar powers on");

            int Meals() => map.listerThings.ThingsOfDef(ThingDefOf.MealSurvivalPack).Sum(x => x.stackCount);
            int before = Meals();
            NextSupplyDrop.SetValue(comp, Find.TickManager.TicksGame + 1);
            yield return new WaitTicks(1200);
            int afterDrop = Meals();
            // 4 per pawn; leave room for meals the garrison eats meanwhile.
            t.Check(afterDrop >= before + high.crew * 2, $"a timed drop delivered survival meals ({before} → {afterDrop})");
            t.Check((int)NextSupplyDrop.GetValue(comp) > Find.TickManager.TicksGame + GenDate.TicksPerDay / 2,
                "the next drop is scheduled about a day later");
            t.Screenshot("supply_drop");

            foreach (Building r in radars) { SetFlick(r, false); }
            yield return new WaitTicks(3);
            int off = Meals();
            NextSupplyDrop.SetValue(comp, Find.TickManager.TicksGame + 1);
            // Also past the crew LordJob's own 5000-tick low-stock check, which this site must not use.
            yield return new WaitTicks(5600);
            int later = Meals();
            t.Check(map.listerThings.AllThings.OfType<Skyfaller>().Count() == 0 && later <= off,
                $"no supply drop once every radar is off ({off} → {later})");

            MissileBaseRackSupply supply = (map.Parent as Site)?.GetComponent<WorldObjectComp_MissileBase>()?.Props.rackSupply;
            t.Require(supply != null, "the site has a rackSupply");
            List<IMissileRack> all = MissileBaseRackSupplyUtility.RacksOf(map, industrial);
            foreach (IMissileRack r in all.Where(r => r.StoredCount == 0))
                MissileBaseRackSupplyUtility.LoadDirect(r, MissileBaseRackSupplyUtility.MakeLoad(r, supply));

            // ── Crew reload: an emptied launcher is refilled by a crew member carrying missiles out of an ammo storage ──
            IMissileRack launcher = all.FirstOrDefault(r => MissileBaseRackSupplyUtility.IsLauncher(r)
                && all.Any(s => !MissileBaseRackSupplyUtility.IsLauncher(s) && s.StoredCount > 0 && MissileBaseRackSupplyUtility.HasMissileFor(s, r)));
            t.Require(launcher != null, "a launcher with a compatible stocked ammo storage exists");
            int storedBefore = all.Where(r => !MissileBaseRackSupplyUtility.IsLauncher(r)).Sum(r => r.StoredCount);
            launcher.GetDirectlyHeldThings().ClearAndDestroyContents();
            bool sawJob = false;
            var waitHaul = new WaitUntil(() =>
            {
                sawJob |= map.mapPawns.SpawnedPawnsInFaction(industrial).Any(p => p.CurJobDef == DMSE_MissileJobDefOf.DMSE_LoadMissileLauncher);
                return launcher.StoredCount > 0;
            }, 5000);
            yield return waitHaul;
            t.Check(sawJob, "a crew member took the load-launcher job");
            t.Check(!waitHaul.TimedOut, $"the crew loaded {launcher.RackThing.def.defName} from storage ({launcher.StoredCount}/{launcher.MaxStored})");
            int storedAfter = all.Where(r => !MissileBaseRackSupplyUtility.IsLauncher(r)).Sum(r => r.StoredCount);
            t.Check(storedAfter < storedBefore, $"the missiles came out of storage ({storedBefore} → {storedAfter})");
            t.Screenshot("crew_reload");

            // ── Resupply: an emptied ammo storage gets a pod every 2 days; the crew loads it after loadDelayTicks ──
            IMissileRack storage = all.FirstOrDefault(r => r.RackThing.def.defName == "DMSE_FieldStructure_AmmoStorage")
                ?? all.First(r => !MissileBaseRackSupplyUtility.IsLauncher(r));
            storage.GetDirectlyHeldThings().ClearAndDestroyContents();
            t.Require(NextRackSupply != null, "nextRackSupplyTick exists");
            NextRackSupply.SetValue(comp, Find.TickManager.TicksGame + 1);
            var waitPod = new WaitUntil(() => map.listerThings.AllThings.Any(x => x.def == ThingDefOf.DropPodIncoming || x is Skyfaller), 600);
            yield return waitPod;
            t.Check(!waitPod.TimedOut, "a resupply pod is dropped for the empty storage");
            t.Check((int)NextRackSupply.GetValue(comp) > Find.TickManager.TicksGame + GenDate.TicksPerDay * 3 / 2, "the next resupply is about two days later");
            t.Check(storage.StoredCount == 0, "the storage is not refilled instantly");
            var waitLoad = new WaitUntil(() => storage.StoredCount > 0, 4500);
            yield return waitLoad;
            t.Check(!waitLoad.TimedOut, $"the crew stows the dropped missiles ({storage.RackThing.def.defName}: {storage.StoredCount}/{storage.MaxStored})");
        }
    

        /// <summary>The storyteller incident deploys one field site in range of the colony and refuses a second.</summary>
        public static IEnumerable<Wait> IncidentSpawnsSite(TestContext t)
        {
            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("DMSE_FieldLaunchSiteDeployed");
            t.Require(def != null, "IncidentDef DMSE_FieldLaunchSiteDeployed loaded");
            MethodInfo canFireSub = typeof(IncidentWorker).GetMethod("CanFireNowSub", BindingFlags.Instance | BindingFlags.NonPublic);
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, Home);
            parms.points = 1000f;

            List<WorldObject> before = Find.WorldObjects.AllWorldObjects.ToList();
            t.Require(IncidentWorker_FieldLaunchSite.ActiveSiteCount() == 0, "no NPC missile site exists yet");

            // Gated on the player's radar network research: un-finish it for one check, then finish it.
            ResearchProjectDef radarNetwork = DefDatabase<ResearchProjectDef>.GetNamed("DMSE_RadarNetwork");
            var progress = (Dictionary<ResearchProjectDef, float>)typeof(ResearchManager)
                .GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Find.ResearchManager);
            float savedProgress = Find.ResearchManager.GetProgress(radarNetwork);
            progress[radarNetwork] = 0f;
            t.Check(!radarNetwork.IsFinished && !(bool)canFireSub.Invoke(def.Worker, new object[] { parms }),
                "the incident cannot fire before DMSE_RadarNetwork is researched");
            progress[radarNetwork] = savedProgress;
            EnsureNpcWorldNetwork();
            t.Check((bool)canFireSub.Invoke(def.Worker, new object[] { parms }), "after the research, with no site present, the incident can fire");
            t.Check(def.Worker.TryExecute(parms), "the incident executes");

            Site site = Find.WorldObjects.Sites.FirstOrDefault(s => !before.Contains(s) && s.def.defName == "DMSE_FieldLaunchSite");
            t.Require(site != null, "a DMSE_FieldLaunchSite world object was added");
            Track(site);
            t.Check(site.Faction != null && site.Faction.def.techLevel == TechLevel.Industrial && site.Faction.HostileTo(Player),
                $"owned by a hostile industrial faction ({site.Faction?.Name})");
            float dist = MissileWorldUtility.RangeDistance(Home.Tile, site.Tile);
            t.Check(dist <= 15f, $"within the site's 15-tile strike range ({dist:0.0})");
            t.Check(site.parts.Any(p => p.def.defName == "DMSE_FieldLaunchSitePart" && p.parms.threatPoints == 1000f), "the site part carries the incident's threat points");
            t.Check(Find.LetterStack.LettersListForReading.Any(l => l.lookTargets?.targets?.Any(x => x.WorldObject == site) == true), "a letter points at the site");
            t.Check(!(bool)canFireSub.Invoke(def.Worker, new object[] { parms }), "a second site is refused while one exists");
            yield break;
        }

        /// <summary>Spawns <paramref name="def"/> for <paramref name="faction"/> on clear standable ground near <paramref name="near"/>.</summary>
        private static Thing SpawnStation(Map map, string defName, Faction faction, IntVec3 near)
        {
            ThingDef def = ThingDef.Named(defName);
            bool Clear(IntVec3 c) => GenAdj.OccupiedRect(c, Rot4.North, def.size).ExpandedBy(2).Cells
                .All(x => x.InBounds(map) && x.Standable(map) && x.GetEdifice(map) == null && !x.Fogged(map));
            if (!CellFinder.TryFindRandomCellNear(near, map, 30, Clear, out IntVec3 cell)) { return null; }
            Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(faction);
            GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
            thing.TryGetComp<CompRefuelable>()?.Refuel(thing.TryGetComp<CompRefuelable>().Props.fuelCapacity);
            return thing;
        }

        /// <summary>
        /// NPC gunners man every kind of mannable station (missile launcher, radar, gun turret), leave the post
        /// to eat once hungry, and go back afterwards.
        /// </summary>
        public static IEnumerable<Wait> GunnersManStationsAndEat(TestContext t)
        {
            Faction industrial = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel == TechLevel.Industrial && f.def.humanlikeFaction);
            t.Require(industrial != null, "an industrial humanlike faction exists");
            Layout site = GenerateAt(t, industrial, 1f);
            Map map = site.map;
            Thing hub = map.listerThings.ThingsOfDef(ThingDef.Named("DMSE_SearchRadar")).FirstOrDefault();
            IntVec3 center = hub?.Position ?? map.Center;

            List<Thing> generated = map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == industrial && b.def.hasInteractionCell && b.TryGetComp<CompMannable>() != null).Cast<Thing>().ToList();
            t.Note($"generated mannable stations: {string.Join(", ", generated.Select(x => x.def.defName))}");

            List<Thing> stations = new[] { "DMSE_Building_RotaryMissileLauncher", "DMSE_EarlyFireControlRadar", "DMSE_Turret_LegacyFlak" }
                .Select(d => SpawnStation(map, d, industrial, center)).ToList();
            t.Require(stations.All(x => x != null), "a missile launcher, a radar and a gun turret were placed");

            // Three extra gunners so the test stations are covered even if the generated gunners stay on their own stations.
            List<Pawn> gunners = new List<Pawn>();
            for (int i = 0; i < 3; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(industrial.RandomPawnKind(), industrial,
                    context: PawnGenerationContext.NonPlayer, tile: map.Tile, mustBeCapableOfViolence: true));
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(stations[i].Position, map, 4), map);
                p.needs.food.CurLevelPercentage = 0.9f;
                gunners.Add(p);
            }
            LordMaker.MakeNewLord(industrial, new LordJob_MissileStationCrew(center), map, gunners);

            bool AllManned() => stations.All(x => x.TryGetComp<CompMannable>().MannedNow);
            var waitMan = new WaitUntil(AllManned, 3000);
            yield return waitMan;
            t.Check(!waitMan.TimedOut, "gunners man the missile launcher, the radar and the gun turret: "
                + string.Join(", ", stations.Select(x => $"{x.def.defName}={x.TryGetComp<CompMannable>().MannedNow}")));
            t.Check(stations.All(x => x.TryGetComp<CompMannable>().ManningPawn?.GetLord()?.LordJob is LordJob_MissileStationCrew), "they are manned by station crew gunners");

            // Food within reach; one gunner goes hungry.
            Thing meals = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);
            meals.stackCount = 10;
            GenPlace.TryPlaceThing(meals, center, map, ThingPlaceMode.Near);
            Thing post = stations.First(x => x.TryGetComp<CompMannable>().MannedNow);
            Pawn hungry = post.TryGetComp<CompMannable>().ManningPawn;
            List<Pawn> others = stations.Select(x => x.TryGetComp<CompMannable>().ManningPawn).Where(p => p != null && p != hungry).ToList();
            hungry.needs.food.CurLevelPercentage = 0.2f;
            var waitEat = new WaitUntil(() => hungry.CurJobDef == JobDefOf.Ingest, 2600);
            yield return waitEat;
            t.Check(!waitEat.TimedOut, $"the hungry gunner leaves {post.def.defName} to eat (job {hungry.CurJobDef?.defName})");
            var waitBack = new WaitUntil(() => hungry.needs.food.CurLevelPercentage > 0.3f
                && stations.Any(x => x.TryGetComp<CompMannable>().ManningPawn == hungry), 4000);
            yield return waitBack;
            t.Check(!waitBack.TimedOut, $"after eating ({hungry.needs.food.CurLevelPercentage:P0}) the gunner mans a station again");
            t.Check(others.All(p => stations.Any(x => x.TryGetComp<CompMannable>().ManningPawn == p)),
                "the fed gunners stayed at their posts");
        }

        /// <summary>
        /// The first missile alert of a kind is a letter; repeats within the cooldown become messages (no pause);
        /// a different kind and the same kind after the cooldown are letters again.
        /// </summary>
        public static IEnumerable<Wait> AlertThrottle(TestContext t)
        {
            MissileAlertThrottle.Reset();
            int Letters(TaggedString label) => Find.LetterStack.LettersListForReading.Count(l => l.Label.ToString() == label.ToString());
            List<Message> live = (List<Message>)typeof(Messages).GetField("liveMessages", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            TaggedString a = "DMSE test alert A", b = "DMSE test alert B";
            int startA = Letters(a), startB = Letters(b);
            LookTargets look = new TargetInfo(Home.Center, Home);

            MissileAlertThrottle.Notify("TestA", a, "first", LetterDefOf.ThreatBig, look);
            t.Check(Letters(a) == startA + 1, "the first alert is a letter");
            MissileAlertThrottle.Notify("TestA", a, "second", LetterDefOf.ThreatBig, look);
            MissileAlertThrottle.Notify("TestA", a, "third", LetterDefOf.ThreatBig, look);
            t.Check(Letters(a) == startA + 1, "repeats within the cooldown send no letter");
            t.Check(live.Count(m => m.text.Contains("DMSE test alert A")) >= 2, "repeats are shown as messages");
            MissileAlertThrottle.Notify("TestB", b, "other kind", LetterDefOf.ThreatBig, look);
            t.Check(Letters(b) == startB + 1, "a different alert kind still gets its letter");

            yield return new WaitTicks(MissileAlertThrottle.CooldownTicks + 10);
            MissileAlertThrottle.Notify("TestA", a, "after cooldown", LetterDefOf.ThreatBig, look);
            t.Check(Letters(a) == startA + 2, "after the cooldown the alert is a letter again");
            foreach (Letter l in Find.LetterStack.LettersListForReading.Where(l => l.Label.ToString() == a.ToString() || l.Label.ToString() == b.ToString()).ToList())
                Find.LetterStack.RemoveLetter(l);
        }
    }
}
