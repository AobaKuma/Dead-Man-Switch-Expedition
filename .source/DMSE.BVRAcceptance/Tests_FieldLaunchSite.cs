using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
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
        private static readonly FieldInfo NextTurretAmmo = typeof(MapComponent_MissileBase)
            .GetField("nextTurretAmmoTick", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PendingTurretRefuels = typeof(MapComponent_MissileBase)
            .GetField("pendingTurretRefuels", BindingFlags.Instance | BindingFlags.NonPublic);

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
            // 8 per pawn, capped by the stock limit; leave room for meals the garrison eats meanwhile.
            t.Check(afterDrop >= before + high.crew * 2, $"a timed drop delivered survival meals ({before} → {afterDrop})");
            int nextDrop = (int)NextSupplyDrop.GetValue(comp) - Find.TickManager.TicksGame;
            t.Check(nextDrop > GenDate.TicksPerDay * 2 - 1300 && nextDrop <= GenDate.TicksPerDay * 4,
                $"the next drop is scheduled 2–4 days later ({nextDrop / (float)GenDate.TicksPerDay:0.00} days)");
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
                sawJob |= map.mapPawns.SpawnedPawnsInFaction(industrial).Any(p =>
                    p.CurJobDef == DMSE_MissileJobDefOf.DMSE_LoadMissileLauncher
                    || p.CurJobDef == DMSE_MissileJobDefOf.DMSE_HaulMissileToRack);
                return launcher.StoredCount > 0;
            }, 5000);
            yield return waitHaul;
            t.Check(sawJob, "a crew member took a missile-hauling job");
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
            int nextRack = (int)NextRackSupply.GetValue(comp) - Find.TickManager.TicksGame;
            t.Check(nextRack > GenDate.TicksPerDay * 2 - 700 && nextRack <= GenDate.TicksPerDay * 4,
                $"the next resupply is 2–4 days later ({nextRack / (float)GenDate.TicksPerDay:0.00} days)");
            t.Check(storage.StoredCount == 0, "the storage is not refilled instantly");
            var waitLoad = new WaitUntil(() => storage.StoredCount > 0, 4500);
            yield return waitLoad;
            t.Check(!waitLoad.TimedOut, $"the crew stows the dropped missiles ({storage.RackThing.def.defName}: {storage.StoredCount}/{storage.MaxStored})");
        }
    

        private static readonly FieldInfo BaseSiteDestroyed = typeof(MapComponent_MissileBase)
            .GetField("baseSiteDestroyed", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ReentryBlockedUntil = typeof(WorldObjectComp_MissileBase)
            .GetField("reentryBlockedUntil", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NextWorldFire = typeof(WorldObjectComp_MissileBase)
            .GetField("nextFireTick", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Generates a low-difficulty field site, drops a colonist at its map edge and kills the garrison; the site map is then
        /// left to vanilla removal (no observation timer), so the colonist alone keeps it. Waits until the site has seen its objectives.
        /// </summary>
        private static IEnumerable<Wait> StormSite(TestContext t, Faction industrial, System.Action<Map, Pawn> done)
        {
            Layout l = GenerateAt(t, industrial, 0.3f);
            Map map = l.map;
            Pawn colonist = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
            t.Require(CellFinder.TryFindRandomEdgeCellWith(c => c.Standable(map) && !c.Fogged(map), map, 0f, out IntVec3 edge), "a standable map-edge cell");
            GenSpawn.Spawn(colonist, edge, map);
            foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(industrial).ToList()) { p.Kill(null); }
            GameComponent_MissileEngage.Comp.times.Remove(map);

            // The objective check only counts the site as destroyed after it has seen a radar or launcher once.
            yield return new WaitTicks(1100);
            done(map, colonist);
        }

        /// <summary>
        /// Player report 2026-11-09: garrison dead and radars gone, but the world map offered no "reform caravan". Vanilla hides
        /// it while <see cref="GenHostility.AnyHostileActiveThreatToPlayer"/> finds a threat (any powered hostile turret counts).
        /// </summary>
        private static void CheckCanReform(TestContext t, Site site, Map map)
        {
            FormCaravanComp form = site.GetComponent<FormCaravanComp>();
            t.Require(form != null, "the site has FormCaravanComp");
            List<string> threats = map.attackTargetsCache.TargetsHostileToFaction(Faction.OfPlayer)
                .Where(a => GenHostility.IsActiveThreatTo(a, Faction.OfPlayer, true, !site.CanReformFoggedEnemies))
                .Select(a => $"{a.Thing.def.defName}({a.Thing.GetType().Name})").ToList();
            t.Check(threats.Count == 0, $"no active threats remain ({string.Join(", ", threats)})");
            t.Check(form.CanReformNow() && form.GetGizmos().Any(g => g is Command_Action c && !c.Disabled && c.icon == FormCaravanComp.FormCaravanCommand),
                "the world map offers an enabled reform-caravan command");
        }

        private static Faction Industrial(TestContext t)
        {
            Faction industrial = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel == TechLevel.Industrial && f.def.humanlikeFaction);
            t.Require(industrial != null, "an industrial humanlike faction exists");
            return industrial;
        }

        /// <summary>
        /// Every fire-control radar destroyed (launchers and search radars left standing) with a colonist on the site map: the site is destroyed, but its map
        /// stays until the colonist leaves (before, the site was destroyed at once and vanilla's map close took the colonist
        /// as kidnapped). After the colonist leaves by caravan, vanilla removes the map and the site.
        /// </summary>
        public static IEnumerable<Wait> SiteKeptWhileColonistsOnMap(TestContext t)
        {
            Faction industrial = Industrial(t);
            Map map = null;
            Pawn colonist = null;
            foreach (Wait w in StormSite(t, industrial, (m, c) => { map = m; colonist = c; })) { yield return w; }
            Site site = (Site)map.Parent;
            MapComponent_MissileBase comp = map.GetComponent<MapComponent_MissileBase>();
            t.Require(comp != null && BaseSiteDestroyed != null, "MapComponent_MissileBase with baseSiteDestroyed");

            List<Building> launchers = comp.RemainingLaunchers.ToList();
            List<Building> radars = comp.RemainingRadars.ToList();
            t.Require(launchers.Count > 0 && radars.Count > 0, $"the site has world launchers ({launchers.Count}) and fire-control radars ({radars.Count})");
            t.Check(radars.All(b => b.TryGetComp<CompFireControlRadar>() != null), "only fire-control radars are objectives");
            List<Building> searchRadars = map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == industrial && !(b is Building_Turret) && b.TryGetComp<CompSearchRadar>() != null && b.TryGetComp<CompFireControlRadar>() == null).ToList();
            // Only the fire-control radars go: launchers and search radars are left standing.
            foreach (Building b in radars) { b.Destroy(DestroyMode.Vanish); }

            var waitDestroyed = new WaitUntil(() => (bool)BaseSiteDestroyed.GetValue(comp), 1100);
            yield return waitDestroyed;
            t.Check(!waitDestroyed.TimedOut, "with every fire-control radar gone, the site counts as destroyed");
            t.Check(launchers.All(b => b.Spawned) && searchRadars.All(b => b.Spawned),
                $"its launchers ({launchers.Count}) and search radars ({searchRadars.Count}) did not have to be destroyed");
            int launchesBefore = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_IncomingMissile>().Count(w => w.Faction == industrial);
            yield return new WaitTicks(1000);
            t.Check(Find.WorldObjects.AllWorldObjects.OfType<WorldObject_IncomingMissile>().Count(w => w.Faction == industrial) <= launchesBefore,
                "a destroyed site with launchers standing fires no more");
            yield return new WaitTicks(300);
            t.Check(!site.Destroyed && site.HasMap && site.Map == map, "the site and its map stay while the colonist is there");
            t.Check(colonist.Spawned && colonist.Map == map && !colonist.Dead, "the colonist is still on the site map");
            t.Check(!industrial.kidnapped.KidnappedPawnsListForReading.Contains(colonist), "the colonist was not taken as kidnapped");
            CheckCanReform(t, site, map);

            Caravan caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(new List<Pawn> { colonist }, Faction.OfPlayer, map.Tile, map.Tile, PlanetTile.Invalid);
            Track(caravan);
            var waitRemoved = new WaitUntil(() => site.Destroyed, 1200);
            yield return waitRemoved;
            t.Check(!waitRemoved.TimedOut && map.Disposed, "after the colonist leaves, vanilla removes the map together with the site");
            t.Check(caravan != null && caravan.ContainsPawn(colonist), "the colonist is in the caravan");
        }

        /// <summary>
        /// Fire-control radars are the only objective: with every launcher gone but a fire-control radar standing the site is not destroyed, and a retreat
        /// keeps the site (vanilla would delete it) and blocks caravans and transport pods for reentryBlockedDays. No world salvo
        /// fires straight after the retreat, and once the block runs out the site can be visited again.
        /// </summary>
        public static IEnumerable<Wait> RetreatWithRadarsBlocksReentry(TestContext t)
        {
            Faction industrial = Industrial(t);
            Map map = null;
            Pawn colonist = null;
            foreach (Wait w in StormSite(t, industrial, (m, c) => { map = m; colonist = c; })) { yield return w; }
            Site site = (Site)map.Parent;
            MapComponent_MissileBase comp = map.GetComponent<MapComponent_MissileBase>();
            WorldObjectComp_MissileBase woc = site.GetComponent<WorldObjectComp_MissileBase>();
            t.Require(comp != null && woc != null && ReentryBlockedUntil != null && NextWorldFire != null, "missile base comps and their timers");
            t.Check(comp.PlayerVisited, "the ground visit is recorded");

            List<Building> launchers = comp.RemainingLaunchers.ToList();
            List<Building> radars = comp.RemainingRadars.ToList();
            t.Require(launchers.Count > 0 && radars.Count > 0, $"the site has world launchers ({launchers.Count}) and radars ({radars.Count})");
            t.Note($"radars: {string.Join(", ", radars.Select(b => b.def.defName))}");
            t.Check(!radars.Any(b => b is Building_Turret), "air-defence turrets with a built-in radar are not radar objectives");

            Alert_MissileBaseObjectives alert = new Alert_MissileBaseObjectives();
            Current.Game.CurrentMap = map;
            AlertReport report = alert.GetReport();
            t.Check(report.active && report.AllCulprits.Count() == radars.Count && report.AllCulprits.All(c => c.Thing is Building cb && radars.Contains(cb)),
                $"the objectives alert lists only the fire-control radars ({alert.GetLabel()})");

            foreach (Building b in launchers) { b.Destroy(DestroyMode.Vanish); }
            yield return new WaitTicks(1100);
            t.Check(!comp.BaseSiteDestroyed && !site.Destroyed, "with every launcher gone but fire-control radars standing, the site is not destroyed");
            report = alert.GetReport();
            t.Check(report.active && report.AllCulprits.Count() == radars.Count, $"the alert still lists the fire-control radars ({alert.GetLabel()})");
            Current.Game.CurrentMap = Home;

            Caravan caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(new List<Pawn> { colonist }, Faction.OfPlayer, map.Tile, map.Tile, PlanetTile.Invalid);
            Track(caravan);
            var waitGone = new WaitUntil(() => !site.HasMap, 1200);
            yield return waitGone;
            t.Check(!waitGone.TimedOut && map.Disposed, "after the retreat the site map is removed");
            t.Check(!site.Destroyed && site.Spawned, "the site stays on the world map");
            t.Check(woc.IsReentryBlocked, $"re-entry is blocked for {woc.ReentryBlockedFor.ToStringTicksToPeriod()}");
            int now = Find.TickManager.TicksGame;
            t.Check((int)NextWorldFire.GetValue(woc) > now + GenDate.TicksPerDay, "no world salvo is due right after the retreat");

            FloatMenuAcceptanceReport byCaravan = CaravanArrivalAction_VisitSite.CanVisit(caravan, site);
            t.Check(!byCaravan.Accepted && !byCaravan.FailReason.NullOrEmpty(), $"a caravan cannot visit ({byCaravan.FailReason})");
            List<FloatMenuOption> approach = site.GetFloatMenuOptions(caravan).Where(o => o.Label.StartsWith(site.ApproachOrderString)).ToList();
            t.Check(approach.Count > 0 && approach.All(o => o.Disabled), $"the caravan's approach option is disabled ({string.Join(" | ", approach.Select(o => o.Label))})");
            FloatMenuAcceptanceReport byPods = TransportersArrivalAction_VisitSite.CanVisit(new List<IThingHolder> { caravan }, site);
            t.Check(!byPods.Accepted && !byPods.FailReason.NullOrEmpty(), "transport pods cannot visit either");

            ReentryBlockedUntil.SetValue(woc, Find.TickManager.TicksGame + 1);
            yield return new WaitTicks(2);
            t.Check(!woc.IsReentryBlocked && CaravanArrivalAction_VisitSite.CanVisit(caravan, site).Accepted, "once the block runs out, the caravan can visit again");
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

        private static Site SpawnEarlySite(Faction faction, float points)
        {
            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamed("DMSE_EarlyLaunchSite");
            SitePartDef part = DefDatabase<SitePartDef>.GetNamed("DMSE_EarlyLaunchSitePart");
            PlanetTile home = Home.Tile;
            for (int i = 0; i < Find.WorldGrid.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, home.Layer);
                float d = MissileWorldUtility.RangeDistance(home, t);
                if (d < 6f || d > 14f) { continue; }
                Tile info = Find.WorldGrid[t];
                if (!info.PrimaryBiome.canBuildBase || info.hilliness == Hilliness.Impassable) { continue; }
                if (Find.WorldObjects.AnyWorldObjectAt(t) || Find.WorldObjects.AnySettlementBaseAtOrAdjacent(t)) { continue; }
                Site site = (Site)WorldObjectMaker.MakeWorldObject(def);
                site.Tile = t;
                site.SetFaction(faction);
                site.AddPart(new SitePart(site, part, new SitePartParams { points = points, threatPoints = points }));
                Find.WorldObjects.Add(site);
                Track(site);
                return site;
            }
            return null;
        }

        /// <summary>Site strike fire skips interceptors wherever they are stored and leaves everything on a pure SAM platform alone, dual-role rounds included.</summary>
        public static IEnumerable<Wait> SiteKeepsAirDefenseRounds(TestContext t)
        {
            Faction industrial = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel == TechLevel.Industrial && f.def.humanlikeFaction);
            t.Require(industrial != null, "an industrial humanlike faction exists");

            foreach (string defName in new[] { "DMSE_InterceptorMissile", "DMSE_Missile_Rheintochter", "DMSE_Missile_Stinger", "DMSE_Missile_Sparrow" })
            {
                t.Check(!MissileBaseRackSupplyUtility.IsStrikeRound(ThingMaker.MakeThing(ThingDef.Named(defName))), $"{defName} is not a strike round");
            }
            foreach (string defName in new[] { "DMSE_Missile_LegacyLoiter", "DMSE_Missile_Maverick", "DMSE_Missile_Grison" })
            {
                t.Check(MissileBaseRackSupplyUtility.IsStrikeRound(ThingMaker.MakeThing(ThingDef.Named(defName))), $"{defName} is a strike round");
            }

            Layout layout = GenerateAt(t, industrial, 1f);
            Map map = layout.map;

            Building SpawnOnSite(string defName, int x)
            {
                ThingDef def = ThingDef.Named(defName);
                Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                thing.SetFaction(industrial);
                GenSpawn.Spawn(thing, new IntVec3(x, 0, 12), map, Rot4.North, WipeMode.Vanish);
                return (Building)thing;
            }
            Building sam = SpawnOnSite("DMSE_Building_RotaryMissileLauncher", 12);
            Building fieldAirDefense = SpawnOnSite("DMSE_Building_FieldAirDefense", 24);
            Building shared = SpawnOnSite("DMSE_Building_LightMissileLauncher", 36);
            Building depot = SpawnOnSite("DMSE_MissileRack", 48);
            // Enemy racks auto-fill on spawn, so empty them only after the test platforms are placed.
            foreach (IMissileRack existing in MissileBaseRackSupplyUtility.RacksOf(map, industrial))
            {
                existing.GetDirectlyHeldThings().ClearAndDestroyContents();
            }
            t.Check(MissileBaseRackSupplyUtility.IsAirDefenseOnly(sam) && MissileBaseRackSupplyUtility.IsAirDefenseOnly(fieldAirDefense), "SAM launcher and field air defense are air-defense-only platforms");
            t.Check(!MissileBaseRackSupplyUtility.IsAirDefenseOnly(shared) && !MissileBaseRackSupplyUtility.IsAirDefenseOnly(depot), "the shared launcher and the depot are not");

            MapComponent_MissileBase comp = map.GetComponent<MapComponent_MissileBase>();
            MethodInfo find = typeof(MapComponent_MissileBase).GetMethod("FindRackWithMissiles", BindingFlags.Instance | BindingFlags.NonPublic);
            t.Require(comp != null && find != null, "the site's rack picker is reachable");
            Thing Pick()
            {
                object[] args = { null };
                find.Invoke(comp, args);
                return (Thing)args[0];
            }
            Thing Load(Building rack, string defName)
            {
                Thing missile = ThingMaker.MakeThing(ThingDef.Named(defName));
                t.Require(MissileRackUtil.Of(rack).GetDirectlyHeldThings().TryAdd(missile), $"{defName} loaded into {rack.def.defName}");
                return missile;
            }

            Load(shared, "DMSE_Missile_Stinger");
            Load(depot, "DMSE_Missile_Sparrow");
            Load(depot, "DMSE_InterceptorMissile");
            Load(sam, "DMSE_Missile_Stinger");
            Thing pick = Pick();
            t.Check(pick == null, $"only interceptors at the site: nothing to fire (picked {pick?.def.defName ?? "nothing"})");

            Load(sam, "DMSE_Missile_Grison");
            Load(fieldAirDefense, "DMSE_Missile_Grison");
            pick = Pick();
            t.Check(pick == null, $"a dual-role round on a pure SAM platform stays there (picked {pick?.def.defName ?? "nothing"})");

            Thing dual = Load(shared, "DMSE_Missile_Grison");
            pick = Pick();
            t.Check(pick == dual, $"with no strike round left, a dual-role round on a shared launcher fires (picked {pick?.def.defName ?? "nothing"})");

            // Depot rounds never fire directly: the crew has to carry them onto a launcher first (a site once fired 16 rounds from 3 launchers this way).
            Load(depot, "DMSE_Missile_Maverick");
            pick = Pick();
            t.Check(pick == dual, $"a strike round in the depot is not fired straight from storage (picked {pick?.def.defName ?? "nothing"})");

            Thing maverick = Load(shared, "DMSE_Missile_Maverick");
            pick = Pick();
            t.Check(pick == maverick, $"a strike round on the launcher is picked before its dual-role round and past the interceptor beside it (picked {pick?.def.defName ?? "nothing"})");

            // A launcher that just fired waits out its own cooldown, even with rounds left on the rail.
            CompMissileLauncher_WorldTargeting pad = shared.TryGetComp<CompMissileLauncher_WorldTargeting>();
            t.Require(pad != null, "the shared launcher has a world launch comp");
            int now = Find.TickManager.TicksGame;
            pad.Notify_LaunchedExternally(now);
            int cooldown = pad.CooldownTicksRemaining(now);
            t.Check(cooldown > 0 && cooldown == pad.CooldownTicksAfterLaunch, $"site fire starts the launcher's own cooldown ({cooldown} of {pad.CooldownTicksAfterLaunch} ticks)");
            pick = Pick();
            t.Check(pick == null, $"a launcher on cooldown is skipped although it still holds rounds (picked {pick?.def.defName ?? "nothing"})");
            yield return new WaitTicks(cooldown + 1);
            pick = Pick();
            t.Check(pick == maverick, $"after the cooldown the launcher fires again (picked {pick?.def.defName ?? "nothing"})");

            // The player must not be able to fire, re-store or re-fit an enemy launcher; claiming it gives the controls back.
            string launchLabel = "ScorerLaunch".Translate();
            bool HasLaunch() => shared.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == launchLabel);
            bool HasCopyPaste() => shared.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == "CommandCopyZoneSettingsLabel".Translate() || c.defaultLabel == "CommandPasteZoneSettingsLabel".Translate());
            bool AssemblyTabVisible()
            {
                ITab_MissileAssembly tab = (ITab_MissileAssembly)shared.GetInspectTabs().FirstOrDefault(x => x is ITab_MissileAssembly);
                if (tab == null) return false;
                Find.Selector.ClearSelection();
                Find.Selector.Select(shared, playSound: false, forceDesignatorDeselect: false);
                bool visible = tab.IsVisible;
                Find.Selector.ClearSelection();
                return visible;
            }
            t.Check(!HasLaunch(), "an enemy launcher shows no launch button");
            t.Check(!HasCopyPaste(), "an enemy launcher shows no storage copy/paste buttons");
            t.Check(!AssemblyTabVisible(), "an enemy launcher hides the missile assembly tab");
            shared.SetFaction(Faction.OfPlayer);
            t.Check(HasLaunch(), "once claimed, the launcher shows its launch button");
            t.Check(HasCopyPaste(), "once claimed, the storage copy/paste buttons are back");
            t.Check(AssemblyTabVisible(), "once claimed, the missile assembly tab is back");
        }

        /// <summary>
        /// A launch still waiting in the launch queue when its launcher is destroyed must not lift off from the empty cell:
        /// its world track is destroyed (the site's fire channel frees itself), and the round that already left keeps flying.
        /// </summary>
        public static IEnumerable<Wait> QueuedLaunchDiesWithLauncher(TestContext t)
        {
            Faction industrial = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel == TechLevel.Industrial && f.def.humanlikeFaction);
            t.Require(industrial != null, "an industrial humanlike faction exists");
            Layout layout = GenerateAt(t, industrial, 1f);
            Map map = layout.map;

            ThingDef def = ThingDef.Named("DMSE_Building_LightMissileLauncher");
            Building launcher = (Building)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            launcher.SetFaction(industrial);
            GenSpawn.Spawn(launcher, new IntVec3(20, 0, 20), map, Rot4.North, WipeMode.Vanish);
            CompMissileLauncher_WorldTargeting pad = launcher.TryGetComp<CompMissileLauncher_WorldTargeting>();
            MapComponent_MissileLaunchQueue queue = MapComponent_MissileLaunchQueue.For(map);
            t.Require(pad != null && queue != null, "the launcher has a world launch comp and the map a launch queue");

            PlanetTile target = Home.Tile;
            WorldObject MakeTrack()
            {
                WorldObject wo = WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("DMSE_WorldObject_IncomingMissile"));
                wo.Tile = map.Tile;
                wo.SetFaction(industrial);
                return wo;
            }
            WorldObject first = MakeTrack();
            WorldObject queued = MakeTrack();
            int firstTick = pad.LaunchDeparture(first, pad.LaunchDefFor(null), target);
            int queuedTick = pad.LaunchDeparture(queued, pad.LaunchDefFor(null), target);
            t.Require(queuedTick > Find.TickManager.TicksGame && queue.PendingDepartures >= 1,
                $"the second round waits in the launch queue (spawns at {queuedTick}, first at {firstTick}, now {Find.TickManager.TicksGame})");

            launcher.Destroy(DestroyMode.KillFinalize);
            yield return new WaitTicks(MapComponent_MissileLaunchQueue.SpacingTicks * 3);

            t.Check(queued.Destroyed, "the queued round's world track was destroyed with its launcher");
            t.Check(!Find.WorldObjects.Contains(queued), "the queued round never entered the world");
            t.Check(!map.listerThings.AllThings.OfType<Projectile_MissileLaunch>().Any(p => p.worldObject == queued),
                "no departure lifted off from the destroyed launcher's cell");
            t.Check(queue.PendingDepartures == 0, $"nothing is left in the launch queue ({queue.PendingDepartures})");
            t.Check(!first.Destroyed, "the round that had already left is unaffected");

            // Do not leave a live track aimed at the colony for later tests.
            foreach (Projectile_MissileLaunch p in map.listerThings.AllThings.OfType<Projectile_MissileLaunch>().Where(p => p.worldObject == first).ToList())
                p.Destroy();
            if (!first.Destroyed) first.Destroy();
        }

        /// <summary>The site has to actually shoot: racks loaded with the fixed-config loiter, radar powered and manned, a missile off the map; plus a world-layer salvo of the same body.</summary>
        public static IEnumerable<Wait> EarlySiteFires(TestContext t)
        {
            // The fixture's unlimited power would hide a generator shortfall, and power is what this site depends on.
            DebugSettings.unlimitedPower = false;
            try
            {
                foreach (Wait w in EarlySiteFiresBody(t)) { yield return w; }
            }
            finally { DebugSettings.unlimitedPower = true; }
        }

        private static IEnumerable<Wait> EarlySiteFiresBody(TestContext t)
        {
            Faction industrial = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel == TechLevel.Industrial && f.def.humanlikeFaction);
            t.Require(industrial != null, "an industrial humanlike faction exists");

            // World layer first: no map yet, the salvo must use the legacy body.
            Site worldSite = SpawnEarlySite(industrial, 1000f);
            t.Require(worldSite != null, "an early site spawned");
            WorldObjectComp_MissileBase comp = worldSite.GetComponent<WorldObjectComp_MissileBase>();
            t.Check(comp.BuildFireConfig()?.body?.defName == "DMSE_MissileBody_LegacyLoiter", "the world layer fires the legacy loitering munition");
            t.Check(comp.TryFireAtPlayerColonies() == 1, "a world-layer salvo launches one missile at the colony");
            foreach (WorldObject_IncomingMissile m in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_IncomingMissile>().ToList()) { m.Destroy(); }

            Map map = MissileWorldUtility.ResolveArrivalMap(worldSite.Tile, worldSite, true);
            t.Require(map != null, "the early site map is generated");
            GameComponent_MissileEngage.Comp.times.SetOrAdd(map, Find.TickManager.TicksGame + GenDate.TicksPerDay * 2);

            List<Building> mine = map.listerBuildings.allBuildingsNonColonist.Where(b => b.Faction == industrial).ToList();
            t.Check(mine.Any(b => b.def.defName == "DMSE_AcousticEarlyWarningRadar"), "an acoustic warning post is at the site");
            List<Building> radars = mine.Where(b => b.def.defName == "DMSE_EarlyFireControlRadar").ToList();
            t.Require(radars.Count >= 1, "an early fire-control radar is at the site");
            t.Check(mine.Any(b => b.def.defName == "DMSE_Building_LoiteringLauncher"), "the guaranteed first position has a rail that needs no operator");
            t.Check(!mine.Any(b => b.def.defName == "DMSE_SearchRadar" || b.def.defName == "DMSE_FireControlRadar"), "no mid-tier radar appears");

            List<IMissileRack> racks = MissileBaseRackSupplyUtility.RacksOf(map, industrial);
            t.Check(racks.Count > 0 && racks.All(r => r.StoredCount > 0), $"every rack starts loaded ({racks.Count(r => r.StoredCount > 0)}/{racks.Count})");
            t.Check(racks.All(r => r.HeldThings.All(m => m.def.defName == "DMSE_Missile_LegacyLoiter")), "racks hold only legacy loitering munitions");
            t.Note($"crew {map.mapPawns.SpawnedPawnsInFaction(industrial).Count(p => p.RaceProps.Humanlike)}, launchers {mine.Count(b => b.TryGetComp<CompMissileLauncher_WorldTargeting>() != null)}, stations {mine.Count(b => b.TryGetComp<CompMannable>() != null)}");

            int stored = racks.Sum(r => r.StoredCount);
            var waitRadar = new WaitUntil(() => radars.Any(r => r.TryGetComp<CompFireControlRadar>().Active), 6000);
            yield return waitRadar;
            Building radar = radars[0];
            t.Check(!waitRadar.TimedOut, $"a fire-control radar comes online (manned {radar.TryGetComp<CompMannable>().MannedNow}, power {radar.TryGetComp<CompPowerTrader>().PowerOn}, net gain {radar.TryGetComp<CompPowerTrader>().PowerNet?.CurrentEnergyGainRate() * 60000f:0} W)");
            List<CompRefuelable> generators = mine.Where(b => b.TryGetComp<CompPowerPlant>() != null).Select(b => b.TryGetComp<CompRefuelable>()).ToList();
            t.Check(generators.Count >= 2 && generators.All(g => g.Fuel > 0f), $"the crew fuels the generators ({generators.Count})");
            PowerNet radarNet = radar.TryGetComp<CompPowerTrader>().PowerNet;
            foreach (Building g in mine.Where(b => b.TryGetComp<CompPowerPlant>() != null))
            {
                CompPowerPlant plant = g.TryGetComp<CompPowerPlant>();
                t.Note($"generator {g.Position}: output {plant.PowerOutput:0} W, fuel {g.TryGetComp<CompRefuelable>().Fuel:0}, net {(plant.PowerNet == null ? "none" : plant.PowerNet == radarNet ? "radar's" : "other")}, transmitter {(plant.connectParent?.parent.def.defName ?? "none")}");
            }
            t.Note($"radar net: {radarNet.powerComps.Count} comps, {radarNet.batteryComps.Count} batteries, {radarNet.transmitters.Count} transmitters, stored {radarNet.CurrentStoredEnergy():0}");
            t.Check(radar.TryGetComp<CompPowerTrader>().PowerNet.CurrentEnergyGainRate() >= 0f, "the generators cover the radar and floodlight without draining the batteries");
            generators[0].ConsumeFuel(generators[0].Fuel - 1f);
            var waitFuel = new WaitUntil(() => generators[0].Fuel >= generators[0].Props.fuelCapacity * 0.5f, 700);
            yield return waitFuel;
            t.Check(!waitFuel.TimedOut, "a nearly empty generator is topped up while the crew lives");

            var waitFire = new WaitUntil(() => racks.Sum(r => r.StoredCount) < stored, 3000);
            yield return waitFire;
            t.Check(!waitFire.TimedOut, $"the site fires a legacy loiter from the map ({stored} -> {racks.Sum(r => r.StoredCount)} stored)");

            // Empty launchers: gunners must not re-take the man job in a loop, and the crew carries munitions over from the caches.
            List<IMissileRack> launchers = racks.Where(MissileBaseRackSupplyUtility.IsLauncher).ToList();
            foreach (IMissileRack l in launchers) { l.GetDirectlyHeldThings().ClearAndDestroyContents(); }
            t.Check(racks.Any(r => !MissileBaseRackSupplyUtility.IsLauncher(r) && r.StoredCount > 0), "the caches still hold munitions");
            // Caches can also fire directly through a launcher, so only a round actually sitting in a launcher proves the carry.
            var waitReload = new WaitUntil(() => launchers.Any(l => l.StoredCount > 0), 6000);
            yield return waitReload;
            t.Check(!waitReload.TimedOut, "the crew moves a munition from a cache to an emptied launcher");
        }

        public static IEnumerable<Wait> EarlySiteIncidentGates(TestContext t)
        {
            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("DMSE_EarlyLaunchSiteDeployed");
            t.Require(def != null, "IncidentDef DMSE_EarlyLaunchSiteDeployed loaded");
            MethodInfo canFireSub = typeof(IncidentWorker).GetMethod("CanFireNowSub", BindingFlags.Instance | BindingFlags.NonPublic);
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, Home);
            t.Require(IncidentWorker_FieldLaunchSite.ActiveSiteCount() == 0, "no NPC missile site exists yet");

            Faction pirate = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.defeated && !f.Hidden && SitePartWorker_EarlyLaunchSite.IsPirate(f));
            t.Require(pirate != null, "a pirate faction exists");
            SitePartDef part = DefDatabase<SitePartDef>.GetNamed("DMSE_EarlyLaunchSitePart");
            t.Check(part.FactionCanOwn(pirate), "the early site part accepts pirates");
            Faction tribe = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && !f.Hidden && f.def.techLevel == TechLevel.Neolithic);
            if (tribe != null) { t.Check(!part.FactionCanOwn(tribe), $"the early site part rejects neolithic {tribe.Name}"); }

            ResearchProjectDef radarNetwork = DefDatabase<ResearchProjectDef>.GetNamed("DMSE_RadarNetwork");
            var progress = (Dictionary<ResearchProjectDef, float>)typeof(ResearchManager)
                .GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Find.ResearchManager);
            float saved = Find.ResearchManager.GetProgress(radarNetwork);

            // Before the research: only high points.
            progress[radarNetwork] = 0f;
            t.Require(!radarNetwork.IsFinished, "radar network un-finished for the check");
            parms.points = 1000f;
            t.Check(!(bool)canFireSub.Invoke(def.Worker, new object[] { parms }), "before radar network, 1000 points is refused");
            parms.points = 2500f;
            t.Check((bool)canFireSub.Invoke(def.Worker, new object[] { parms }), "before radar network, 2500 points can fire");
            t.Check(Mathf.Approximately(def.Worker.ChanceFactorNow(Home), 1f), "before radar network the chance is not reduced");

            // After the research: any points, pirates only, reduced chance.
            progress[radarNetwork] = radarNetwork.baseCost;
            t.Require(radarNetwork.IsFinished, "radar network finished for the check");
            EnsureNpcWorldNetwork();
            parms.points = 1000f;
            t.Check((bool)canFireSub.Invoke(def.Worker, new object[] { parms }), "after radar network, 1000 points can fire");
            t.Check(def.Worker.ChanceFactorNow(Home) < 1f, "after radar network the chance is reduced");
            List<WorldObject> before = Find.WorldObjects.AllWorldObjects.ToList();
            t.Check(def.Worker.TryExecute(parms), "the incident executes");
            Site site = Find.WorldObjects.Sites.FirstOrDefault(s => !before.Contains(s) && s.def.defName == "DMSE_EarlyLaunchSite");
            t.Require(site != null, "a DMSE_EarlyLaunchSite world object was added");
            Track(site);
            t.Check(SitePartWorker_EarlyLaunchSite.IsPirate(site.Faction), $"owned by pirates ({site.Faction?.Name}, {site.Faction?.def.defName})");
            t.Check(MissileWorldUtility.RangeDistance(Home.Tile, site.Tile) <= 15f, "within the site's strike range");
            t.Check(!(bool)canFireSub.Invoke(def.Worker, new object[] { parms }), "a second site is refused while one exists");
            if (saved < radarNetwork.baseCost) { progress[radarNetwork] = saved; }
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
        /// Cannon rounds are <see cref="CompRefuelable"/> and NPCs never refuel on their own. The garrison now gets a
        /// pod dropped beside a dry gun and hauls the rounds over itself — nothing is teleported into a gun, a gun that
        /// still has over half its magazine gets no pod at all, and without a crew the rounds just stay on the ground.
        /// </summary>
        public static IEnumerable<Wait> GarrisonKeepsTurretsStocked(TestContext t)
        {
            Faction industrial = Industrial(t);
            foreach (Site site in new[] { SpawnFieldSite(industrial, 1000f), SpawnEarlySite(industrial, 1000f) })
            {
                t.Require(site != null, "a site spawned");
                Map map = MissileWorldUtility.ResolveArrivalMap(site.Tile, site, true);
                t.Require(map != null, $"{site.def.defName}: map generated");
                KeepMapAlive(map);
                MapComponent_MissileBase comp = map.GetComponent<MapComponent_MissileBase>();
                t.Require(comp != null && NextTurretAmmo != null && PendingTurretRefuels != null,
                    "MapComponent_MissileBase with turret-ammo state");

                IntVec3 hub = map.listerBuildings.allBuildingsNonColonist.First(b => b.Faction == industrial && MapComponent_MissileBase.IsObjectiveRadar(b)).Position;
                // Layouts are random: place both guns so each is always covered.
                foreach (string def in new[] { "DMSE_Turret_LegacyFlak", "DMSE_Building_FieldAirDefense" })
                {
                    t.Require(SpawnStation(map, def, industrial, hub) != null, $"{def} placed");
                }
                CompRefuelable Ammo(Building b) => b.TryGetComp<CompRefuelable>();
                float Cap(Building b) => Ammo(b).Props.fuelCapacity;
                List<Building> guns = map.listerBuildings.allBuildingsNonColonist.Where(b => b.Faction == industrial && Ammo(b) != null
                    && (b is Building_Turret || b.TryGetComp<CompTerminalCIWS>() != null)).ToList();
                t.Require(guns.Count > 0, $"{site.def.defName}: the site has armed turrets");
                int Steel() => map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(x => x.stackCount);
                int RefuelJobs() => map.mapPawns.SpawnedPawnsInFaction(industrial)
                    .Count(p => p.CurJobDef == JobDefOf.Refuel || p.CurJobDef == JobDefOf.RefuelAtomic);
                string Rounds() => string.Join(", ", guns.Select(g => $"{g.def.defName} {Ammo(g).Fuel:0}/{Cap(g):0}"));

                t.Note($"{site.def.defName}: {string.Join(", ", guns.GroupBy(g => g.def.defName).Select(g => $"{g.Key}×{g.Count()}"))}");
                t.Note($"rounds at generation: {Rounds()}");

                // ── The first check only schedules; nothing is teleported into a gun on the site's own clock ──
                // Normalise the rig first: the two guns spawned above arrive full, generated ones sit at initialFuelPercent,
                // and half a magazine is exactly the threshold the site ignores.
                foreach (Building g in guns.Where(g => !g.Destroyed)) { SetRounds(Ammo(g), Cap(g) * 0.5f); }
                yield return new WaitTicks(700);
                List<Building> toppedUp = guns.Where(g => !g.Destroyed && Ammo(g).Fuel > Cap(g) * 0.5f + 1f).ToList();
                t.Check(toppedUp.Count == 0, $"{site.def.defName}: guns are not refilled on the tick{(toppedUp.Count > 0 ? ": " + string.Join(", ", toppedUp.Select(g => $"{g.def.defName} {Ammo(g).Fuel:0}/{Cap(g):0}")) : "")}");

                // ── Over half a magazine: no pod is worth dropping ──
                foreach (Building g in guns.Where(g => !g.Destroyed)) { SetRounds(Ammo(g), Cap(g) * 0.6f); }
                int steelBefore = Steel();
                ForceTurretAmmoCheck(comp);
                yield return new WaitTicks(700);
                t.Check(guns.Where(g => !g.Destroyed).All(g => Ammo(g).Fuel <= Cap(g) * 0.6f + 1f),
                    $"{site.def.defName}: a gun with over half its rounds is not topped up ({Rounds()})");
                t.Check(Steel() == steelBefore, $"{site.def.defName}: no ammo pod for a gun that still has rounds ({steelBefore} → {Steel()})");

                // ── Dry guns: a pod lands nearby, then the crew carries the rounds over ──
                foreach (Building g in guns.Where(g => !g.Destroyed)) { SetRounds(Ammo(g), 0f); }
                steelBefore = Steel();
                ForceTurretAmmoCheck(comp);
                var waitDrop = new WaitUntil(() => Steel() > steelBefore, 1500);
                yield return waitDrop;
                int dropped = Steel() - steelBefore;
                t.Check(dropped > 0, $"{site.def.defName}: an ammo pod delivers rounds to the dry guns (+{dropped} steel)");
                t.Check(guns.Where(g => !g.Destroyed).All(g => !Ammo(g).HasFuel),
                    $"{site.def.defName}: the rounds are not teleported into the guns ({Rounds()})");

                bool sawRefuel = false;
                var waitHaul = new WaitUntil(() =>
                {
                    sawRefuel |= RefuelJobs() > 0;
                    return guns.Any(g => !g.Destroyed && Ammo(g).IsFull);
                }, 3500);
                yield return waitHaul;
                t.Check(sawRefuel, $"{site.def.defName}: a crew member took the refuel job for the dropped rounds");
                t.Check(!waitHaul.TimedOut, $"{site.def.defName}: the crew hauls the rounds into a gun ({Rounds()})");
                t.Check(Steel() < steelBefore + dropped, $"{site.def.defName}: the hauled rounds leave the ground ({steelBefore + dropped} → {Steel()})");
                t.Screenshot("turret_ammo_haul");

                // ── No crew: the rounds rot where they landed ──
                ((List<PendingTurretRefuel>)PendingTurretRefuels.GetValue(comp))?.Clear();
                foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(industrial).ToList()) { p.Destroy(DestroyMode.Vanish); }
                foreach (Building g in guns.Where(g => !g.Destroyed)) { SetRounds(Ammo(g), 0f); }
                steelBefore = Steel();
                ForceTurretAmmoCheck(comp);
                yield return new WaitTicks(1500);
                t.Check(Steel() == steelBefore, $"{site.def.defName}: no pod is dropped without a crew ({steelBefore} → {Steel()})");
                t.Check(guns.Where(g => !g.Destroyed).All(g => !Ammo(g).HasFuel), $"{site.def.defName}: without a crew nobody restocks them");
            }
        }

        /// <summary>
        /// Sets a refuelable's rounds to an exact amount. <see cref="CompRefuelable.Refuel(float)"/> takes fuel <i>items</i>
        /// and multiplies them by <see cref="CompProperties_Refuelable.FuelMultiplierCurrentDifficulty"/>, so divide first.
        /// </summary>
        private static void SetRounds(CompRefuelable comp, float amount)
        {
            if (comp.Fuel > amount) { comp.ConsumeFuel(comp.Fuel - amount); }
            else { comp.Refuel((amount - comp.Fuel) / Mathf.Max(0.001f, comp.Props.FuelMultiplierCurrentDifficulty)); }
        }

        /// <summary>Makes the turret-ammo schedule come due on the site's next 500-tick pass.</summary>
        private static void ForceTurretAmmoCheck(MapComponent_MissileBase comp)
            => NextTurretAmmo?.SetValue(comp, Find.TickManager.TicksGame + 1);

        /// <summary>
        /// NPC gunners man every kind of mannable station (missile launcher, radar, gun turret), leave the post
        /// to eat once hungry, and go back afterwards.
        /// </summary>
        /// <summary>The war room is a low-priority post: idle garrison sits there and the site's own radar gets the bonus.</summary>
        public static IEnumerable<Wait> GarrisonMansWarRoom(TestContext t)
        {
            Faction industrial = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f =>
                !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel == TechLevel.Industrial && f.def.humanlikeFaction);
            t.Require(industrial != null, "an industrial humanlike faction exists");
            Layout site = GenerateAt(t, industrial, 1f);
            Map map = site.map;
            List<CompWarRoomConsole> consoles = map.listerThings.ThingsOfDef(ThingDef.Named("DMSE_WarRoomConsole"))
                .Select(x => x.TryGetComp<CompWarRoomConsole>()).ToList();
            t.Require(consoles.Count == 3, $"the war room structure brought three consoles ({consoles.Count})");
            t.Check(consoles.All(c => c.parent.Faction == industrial), "the consoles belong to the site's faction");
            t.Check(!MapComponent_EWControl.For(map).RoeActive, "NPC consoles do not switch on player rules of engagement");
            yield return new WaitUntil(() => consoles.All(c => c.PowerOK), 600);
            t.Check(consoles.All(c => c.PowerOK), "the field power unit runs the consoles");

            var waitMan = new WaitUntil(() => consoles.Any(c => c.MannedNow), 5000);
            yield return waitMan;
            t.Check(!waitMan.TimedOut, "idle garrison mans a console");
            CompWarRoomConsole manned = consoles.FirstOrDefault(c => c.MannedNow);
            t.Require(manned != null, "a console is manned");
            Pawn op = manned.Operator;
            t.Note($"operator {op?.LabelShort} ({op?.GetLord()?.LordJob?.GetType().Name}), manned {consoles.Count(c => c.MannedNow)}/3");
            t.Check(op?.CurJobDef == DMSE_EWJobDefOf.DMSE_OperateWarRoom, "the operator runs the war room job");

            ThingWithComps radar = map.listerThings.ThingsOfDef(ThingDef.Named("DMSE_SearchRadar")).FirstOrDefault() as ThingWithComps;
            t.Require(radar != null, "the site's search radar exists");
            int total = MapComponent_EWControl.For(map).OperatorLevelTotal(SkillDefOf.Intellectual, industrial);
            t.Check(Mathf.Approximately(EWControlUtility.RadarRangeFactor(radar), EWControlUtility.IntellectualRangeFactor(total)),
                $"the site's radar gets the garrison's bonus (total {total}, factor {EWControlUtility.RadarRangeFactor(radar):0.00})");

            // Stations still come first: every gunner-class post that was free is not displaced by consoles.
            List<Thing> stations = map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == industrial && b.TryGetComp<CompMannable>() != null && b.TryGetComp<CompWarRoomConsole>() == null).Cast<Thing>().ToList();
            List<Pawn> stationCrew = map.mapPawns.SpawnedPawnsInFaction(industrial).Where(p => p.GetLord()?.LordJob is LordJob_MissileStationCrew).ToList();
            yield return new WaitTicks(1200);
            int mannedStations = stations.Count(s => s.TryGetComp<CompMannable>().MannedNow);
            t.Note($"{stationCrew.Count} gunners, {mannedStations}/{stations.Count} stations manned, {consoles.Count(c => c.MannedNow)}/3 consoles manned");
            t.Check(stationCrew.Count(p => p.CurJobDef == DMSE_EWJobDefOf.DMSE_OperateWarRoom) == 0 || mannedStations == stations.Count,
                "gunners only sit at a console when every other station is taken");

            // A hostile in sight pulls the operator off: the job expires and the defend node takes over.
            op = consoles.FirstOrDefault(c => c.MannedNow)?.Operator;
            if (op != null)
            {
                // Inside the room: walls block line of sight, so a hostile outside is not seen until the lord goes on the assault.
                // The picked faction may be neutral; goodwill drives the relation, SetRelationDirect refuses here.
                int oldGoodwill = industrial.PlayerGoodwill;
                Override(() => industrial.TryAffectGoodwillWith(Faction.OfPlayer, -100 - oldGoodwill, false, false),
                    () => industrial.TryAffectGoodwillWith(Faction.OfPlayer, oldGoodwill - industrial.PlayerGoodwill, false, false));
                t.Check(industrial.HostileTo(Faction.OfPlayer), "the site's faction is hostile for this step");
                Pawn intruder = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                IntVec3 beside = GenAdjFast.AdjacentCells8Way(op.Position).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null);
                t.Require(beside.IsValid && beside != default, "a free cell beside the operator");
                GenSpawn.Spawn(intruder, beside, map);
                Pawn watched = op;
                var waitLeave = new WaitUntil(() => watched.Dead || watched.CurJobDef != DMSE_EWJobDefOf.DMSE_OperateWarRoom, 900);
                yield return waitLeave;
                t.Check(!waitLeave.TimedOut, $"the operator leaves the console when a hostile shows up (job {watched.CurJobDef?.defName})");
                if (intruder.Spawned) { intruder.Destroy(DestroyMode.Vanish); }
            }
        }

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
