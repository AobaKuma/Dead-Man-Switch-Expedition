using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE.BVRAcceptance
{
    /// <summary>Shared setup, spawning and cleanup for acceptance tests.</summary>
    public static class BVRFixture
    {
        public const string TestSaveName = "DMSE_BVR_Acceptance";

        // ---- defs ----
        public static ThingDef PhaseArray => ThingDef.Named("DMSE_PhaseArrayRadar");
        public static ThingDef AcousticRadar => ThingDef.Named("DMSE_AcousticEarlyWarningRadar");
        public static ThingDef EarlyFireControl => ThingDef.Named("DMSE_EarlyFireControlRadar");
        public static ThingDef LightLauncher => ThingDef.Named("DMSE_Building_LightMissileLauncher");
        public static ThingDef WingBack => ThingDef.Named("DMSE_Turret_Vulcan_WingBack");
        public static ThingDef LightMissileItem => ThingDef.Named("DMSE_Missile_Grison");
        public static ThingDef InterceptorSkyfaller => ThingDef.Named("DMSE_SkyFaller_Interceptor");

        /// <summary>
        /// The interceptor in flight at <paramref name="targetId"/> (-1 = visual-only world launch), old skyfaller or
        /// the interceptor body's own launch projectile (docs/Missile_Skyfaller轉Projectile_計畫.md).
        /// </summary>
        public static IInterceptorShot InterceptorShotAt(int targetId)
            => Home.listerThings.AllThings.OfType<IInterceptorShot>().FirstOrDefault(p => p.TargetId == targetId);

        /// <summary>Strike missiles still climbing out of the home map (old ScorerProjectile or a body launch projectile).</summary>
        public static int DepartingMissiles
            => Home.listerThings.AllThings.Count(x => x is ScorerProjectile || x is Projectile_MissileLaunch { worldObject: not null });
        public static ThingDef CruiseIncoming => ThingDef.Named("DMSE_Incoming_CruiseMissile");
        public static MissileBodyDef CruiseBody => DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Cruise");
        public static MissileBodyDef BallisticBody => DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Ballistic");
        public static MissilePartDef Part(string defName) => DefDatabase<MissilePartDef>.GetNamed(defName);

        // ---- world ----
        public static Map Home => Find.Maps.FirstOrDefault(m => m.IsPlayerHome) ?? Find.CurrentMap;
        public static MapComponent_BVRCombat HomeBVR => Home.GetComponent<MapComponent_BVRCombat>();
        public static Faction Player => Faction.OfPlayer;
        public static Faction Enemy { get; private set; }

        /// <summary>Area cleared, paved and wired for test buildings (south-west quarter of the map).</summary>
        public static CellRect Pad { get; private set; }

        /// <summary>Far corner where test missiles are steered (laser mark) so impacts do not hit the colony or the pad.</summary>
        public static IntVec3 SafeImpactCell { get; private set; }

        private static readonly List<Thing> spawned = new List<Thing>();
        private static readonly List<WorldObject> worldObjects = new List<WorldObject>();
        private static readonly List<Action> restoreActions = new List<Action>();

        // ==================================================================
        //  One-time game preparation
        // ==================================================================

        public static void PrepareGameOnce()
        {
            // No random raids, quests or weather events while testing.
            Find.Storyteller.storytellerComps.Clear();
            DebugSettings.unlimitedPower = true;

            Enemy = Find.FactionManager.RandomEnemyFaction(allowHidden: false, allowDefeated: false,
                allowNonHumanlike: false, minTechLevel: TechLevel.Industrial)
                ?? Find.FactionManager.RandomEnemyFaction(false, false, false, TechLevel.Undefined);
            AcceptanceDriver.Log($"Enemy faction: {Enemy?.Name ?? "NONE"}");

            Map map = Home;
            int size = 40;
            Pad = new CellRect(20, 20, size, size).ClipInsideMap(map);
            foreach (IntVec3 c in Pad.ExpandedBy(2).ClipInsideMap(map))
            {
                ClearCell(map, c);
            }
            foreach (IntVec3 c in Pad)
            {
                Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
                conduit.SetFaction(Player);
                GenSpawn.Spawn(conduit, c, map, WipeMode.Vanish);
            }
            SafeImpactCell = new IntVec3(map.Size.x - 15, 0, map.Size.z - 15);
            ClearCell(map, SafeImpactCell);
            AcceptanceDriver.Log($"Test pad {Pad} on map size {map.Size}; safe impact cell {SafeImpactCell}.");
        }

        public static void OnGameReloaded()
        {
            spawned.Clear();
            worldObjects.Clear();
            restoreActions.Clear();
            Find.Storyteller.storytellerComps.Clear();
            DebugSettings.unlimitedPower = true;
            Enemy = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.loadID == enemyLoadId) ?? Enemy;
        }

        private static int enemyLoadId = -1;
        public static void RememberEnemyForReload() { enemyLoadId = Enemy?.loadID ?? -1; }

        private static void ClearCell(Map map, IntVec3 c)
        {
            if (!c.InBounds(map)) { return; }
            foreach (Thing t in map.thingGrid.ThingsListAt(c).ToList())
            {
                if (t is Pawn) { continue; }
                if (!t.Destroyed) { t.Destroy(DestroyMode.Vanish); }
            }
            map.roofGrid.SetRoof(c, null);
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
            map.fogGrid.Unfog(c);
            map.snowGrid.SetDepth(c, 0f);
        }

        // ==================================================================
        //  Spawning
        // ==================================================================

        /// <summary>Spawn a player (or given faction) building on the pad at a slot offset from the pad origin.</summary>
        public static Thing SpawnBuilding(ThingDef def, int dx, int dz, Faction faction = null)
        {
            Map map = Home;
            IntVec3 cell = new IntVec3(Pad.minX + dx, 0, Pad.minZ + dz);
            ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            Thing thing = ThingMaker.MakeThing(def, stuff);
            thing.SetFaction(faction ?? Player);
            GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
            spawned.Add(thing);
            CompRefuelable fuel = thing.TryGetComp<CompRefuelable>();
            if (fuel != null) { fuel.Refuel(fuel.Props.fuelCapacity); }
            return thing;
        }

        public static Building_MissileRack SpawnLoadedLauncher(int dx, int dz, int missiles)
        {
            Building_MissileRack rack = (Building_MissileRack)SpawnBuilding(LightLauncher, dx, dz);
            for (int i = 0; i < missiles; i++)
            {
                Thing missile = ThingMaker.MakeThing(LightMissileItem);
                if (!rack.GetDirectlyHeldThings().TryAdd(missile)) { missile.Destroy(); }
            }
            return rack;
        }

        /// <summary>
        /// Locks need interceptor ammo (an Active SAM). For tests that want locks to stay pending, fire one
        /// interceptor with a very long reload: the SAM stays armed but never becomes ready. Call once powered.
        /// </summary>
        public static bool HoldInReload(Building_MissileRack rack)
        {
            CompMissileLauncher_Interceptor sam = rack.TryGetComp<CompMissileLauncher_Interceptor>();
            int old = sam.Props.reloadCooldownTicks;
            Override(() => sam.Props.reloadCooldownTicks = 1000000, () => sam.Props.reloadCooldownTicks = old);
            return sam.FireWorldInterceptor(Find.TickManager.TicksGame);
        }

        public static Pawn MakeEnemyPawn()
        {
            PawnKindDef kind = Enemy.def.basicMemberKind ?? Enemy.RandomPawnKind();
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Enemy));
            return pawn;
        }

        /// <summary>Drop an enemy pod (pawns + optional item) aimed at a cell west of the pad. Returns the pawns.</summary>
        public static List<Pawn> DropEnemyPod(int pawnCount, bool withItem = false, IntVec3? at = null)
        {
            Map map = Home;
            ActiveTransporterInfo info = new ActiveTransporterInfo();
            List<Pawn> pawns = new List<Pawn>();
            for (int i = 0; i < pawnCount; i++)
            {
                Pawn p = MakeEnemyPawn();
                pawns.Add(p);
                info.innerContainer.TryAdd(p);
            }
            if (withItem)
            {
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = 25;
                info.innerContainer.TryAdd(steel);
            }
            IntVec3 cell = at ?? new IntVec3(Pad.maxX + 8, 0, Pad.CenterCell.z);
            ClearCell(map, cell);
            DropPodUtility.MakeDropPodAt(cell, map, info);
            return pawns;
        }

        /// <summary>
        /// Vanilla PowerNet switches newly connected consumers on gradually (a few per 30–200 ticks),
        /// so wait until every spawned consumer that wants power actually has it.
        /// </summary>
        public static WaitUntil WaitPowered(int timeoutTicks = 1200)
        {
            return new WaitUntil(() => spawned.All(t => t.Destroyed
                || !(t.TryGetComp<CompPowerTrader>() is CompPowerTrader p)
                || p.PowerOn
                || !FlickUtility.WantsToBeOn(t)), timeoutTicks);
        }

        public static void Track(Thing t) { if (t != null) spawned.Add(t); }
        public static void Track(WorldObject wo) { if (wo != null) worldObjects.Add(wo); }

        /// <summary>Temporarily change a def value; reverted after the test.</summary>
        public static void Override(Action apply, Action restore)
        {
            apply();
            restoreActions.Add(restore);
        }

        /// <summary>
        /// The mechanics suite (M/S/W/D/K/G/L) was written against the pre-generation values: a 2-channel phased array,
        /// a 2-channel early fire-control radar and a 2-channel NPC missile base. Pin them for those tests so their numeric
        /// assertions keep testing the ledger mechanics; the T tests (Tests_Generations) check the shipped values
        /// (docs/三代裝備體系_實作計畫.md §7, §14-2).
        /// </summary>
        public static void PinLegacyRadarValues()
        {
            CaptureShippedRadarValues();
            Override(() => SetRadarValues(2, 2, 2), UseShippedRadarValues);
        }

        /// <summary>
        /// Put the shipped radar values back. Restore actions are dropped when a test reloads the game, so T tests call
        /// this themselves instead of trusting the previous test's cleanup.
        /// </summary>
        public static void UseShippedRadarValues()
        {
            CaptureShippedRadarValues();
            SetRadarValues(shippedPhaseArray, shippedEarlyFc, shippedNpcBase);
        }

        private static int shippedPhaseArray = -1, shippedEarlyFc = -1, shippedNpcBase = -1;

        private static CompProperties_FireControlRadar PhaseArrayFc => PhaseArray.GetCompProperties<CompProperties_FireControlRadar>();
        private static CompProperties_FireControlRadar EarlyFc => EarlyFireControl.GetCompProperties<CompProperties_FireControlRadar>();
        private static CompProperties_WorldAirDefense NpcBaseDefense => DefDatabase<WorldObjectDef>.GetNamed("DMSE_MissileBase").comps
            .OfType<CompProperties_WorldAirDefense>().First();

        private static void CaptureShippedRadarValues()
        {
            if (shippedPhaseArray >= 0) { return; }
            shippedPhaseArray = PhaseArrayFc.maxTargets;
            shippedEarlyFc = EarlyFc.maxTargets;
            shippedNpcBase = NpcBaseDefense.channels;
        }

        private static void SetRadarValues(int phaseArray, int earlyFc, int npcBase)
        {
            PhaseArrayFc.maxTargets = phaseArray;
            EarlyFc.maxTargets = earlyFc;
            NpcBaseDefense.channels = npcBase;
        }

        public static void SetFlick(Thing t, bool on)
        {
            CompFlickable f = t.TryGetComp<CompFlickable>();
            if (f != null) { f.SwitchIsOn = on; }
        }

        // ==================================================================
        //  World helpers
        // ==================================================================

        /// <summary>A surface tile whose surface-equivalent distance from the home tile is within [min, max].</summary>
        /// <summary>
        /// A free tile on <paramref name="center"/>'s layer within [min, max] tiles of it; with <paramref name="closestTo"/>
        /// valid, the one nearest to that tile, otherwise the nearest to <paramref name="center"/>.
        /// </summary>
        public static PlanetTile TileNear(PlanetTile center, float min, float max, PlanetTile closestTo, PlanetTile? exclude = null)
        {
            PlanetTile best = PlanetTile.Invalid;
            float bestScore = float.MaxValue;
            int count = Find.WorldGrid.TilesCount;
            for (int i = 0; i < count; i++)
            {
                PlanetTile t = new PlanetTile(i, center.Layer);
                if (t == center || (exclude.HasValue && t == exclude.Value)) { continue; }
                float d = MissileWorldUtility.RangeDistance(center, t);
                if (d < min || d > max || Find.WorldObjects.AnyWorldObjectAt(t)) { continue; }
                float score = closestTo.Valid ? MissileWorldUtility.RangeDistance(closestTo, t) : d;
                if (score < bestScore) { bestScore = score; best = t; }
            }
            return best;
        }

        public static PlanetTile TileAtDistance(float min, float max)
        {
            PlanetTile home = Home.Tile;
            int count = Find.WorldGrid.TilesCount;
            PlanetTile best = PlanetTile.Invalid;
            for (int i = 0; i < count; i++)
            {
                PlanetTile t = new PlanetTile(i, home.Layer);
                if (t == home) { continue; }
                float d = MissileWorldUtility.RangeDistance(home, t);
                if (d >= min && d <= max && !Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    best = t;
                    break;
                }
            }
            return best;
        }

        public static MissileConfig CruiseConfig(string guidance = "DMSE_Guidance_Laser")
        {
            MissileConfig cfg = new MissileConfig(CruiseBody);
            if (guidance != null) { cfg.SetPart(MissilePartCategory.Guidance, Part(guidance)); }
            return cfg;
        }

        /// <summary>Enemy cruise missile toward the home colony, steered by a laser mark to the safe corner.</summary>
        public static WorldObject_IncomingMissile LaunchEnemyMissile(PlanetTile source)
        {
            EnsureEnemySafeMark();
            WorldObject_IncomingMissile wo = IncomingMissileUtility.Launch(source, Home.Tile, CruiseConfig(), Enemy,
                CruiseIncoming, null, Home.Parent);
            Track(wo);
            return wo;
        }

        public static ScorerProjectile_WorldObject LaunchPlayerMissile(PlanetTile destination, MapParent target, MissileConfig config)
        {
            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamed("DMSE_WorldObject_CruiseMissile");
            ScorerProjectile_WorldObject wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(def);
            wo.skyfallerIncoming = CruiseIncoming;
            wo.SetFaction(Player);
            wo.Tile = Home.Tile;
            wo.destinationTile = destination;
            wo.targetParent = target;
            wo.config = config;
            Find.WorldObjects.Add(wo);
            Track(wo);
            return wo;
        }

        private static LaserDesignatorMark enemySafeMark;
        public static void EnsureEnemySafeMark()
        {
            if (enemySafeMark != null && enemySafeMark.Spawned && !enemySafeMark.Destroyed) { return; }
            enemySafeMark = LaserDesignatorUtility.PlaceMark(SafeImpactCell, Home, Enemy, null, -1);
        }

        public static Site SpawnMissileBase(float minDist, float maxDist)
        {
            WorldObjectDef baseDef = DefDatabase<WorldObjectDef>.GetNamed("DMSE_MissileBase");
            PlanetTile tile = PlanetTile.Invalid;
            PlanetTile home = Home.Tile;
            int count = Find.WorldGrid.TilesCount;
            for (int i = 0; i < count && !tile.Valid; i++)
            {
                PlanetTile t = new PlanetTile(i, home.Layer);
                float d = MissileWorldUtility.RangeDistance(home, t);
                if (d < minDist || d > maxDist) { continue; }
                Tile info = Find.WorldGrid[t];
                if (!info.PrimaryBiome.canBuildBase || info.hilliness == Hilliness.Impassable) { continue; }
                if (Find.WorldObjects.AnyWorldObjectAt(t) || Find.WorldObjects.AnySettlementBaseAtOrAdjacent(t)) { continue; }
                tile = t;
            }
            if (!tile.Valid) { return null; }
            Site site = (Site)WorldObjectMaker.MakeWorldObject(baseDef);
            site.Tile = tile;
            site.SetFaction(Enemy);
            SitePartDef partDef = DefDatabase<SitePartDef>.GetNamedSilentFail("DMSE_MissileBaseSitePart");
            if (partDef != null) { site.AddPart(new SitePart(site, partDef, new SitePartParams { points = 2000f })); }
            Find.WorldObjects.Add(site);
            Track(site);
            return site;
        }

        // ==================================================================
        //  Reflection helpers for private state
        // ==================================================================

        /// <summary>
        /// The mod setting "always show BVR coverage", set in memory only (the public setter also writes the settings file).
        /// </summary>
        public static bool ShowAllCoverage
        {
            get => WorldComponent_BVRNetwork.Current.ShowAllCoverage;
            set
            {
                object settings = typeof(DMSEMod).GetField("Settings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
                settings?.GetType().GetField("showBVRWorldCoverage")?.SetValue(settings, value);
            }
        }

        /// <summary>Private state of the world BVR drawer (ring cache and pending flashes).</summary>
        public static System.Collections.IDictionary CoverageRings
            => Field<System.Collections.IDictionary>(Field<object>(WorldComponent_BVRNetwork.Current, "drawer"), "rings");
        public static System.Collections.ICollection WorldFlashes
            => Field<System.Collections.ICollection>(Field<object>(WorldComponent_BVRNetwork.Current, "drawer"), "flashes");

        public static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null ? (T)f.GetValue(obj) : default;
        }

        public static int LetterCount(string labelKey)
        {
            string label = labelKey.Translate().ToString();
            return Find.LetterStack.LettersListForReading.Count(l => l.Label.ToString() == label);
        }

        // ==================================================================
        //  Cleanup
        // ==================================================================

        public static void CleanupAfterTest()
        {
            for (int i = restoreActions.Count - 1; i >= 0; i--) { restoreActions[i](); }
            restoreActions.Clear();

            Map map = Home;
            MapComponent_BVRCombat bvr = HomeBVR;
            foreach (BVRWave w in bvr.Waves) { foreach (BVRTarget t in w.targets) { t.Discard(); } }
            bvr.Waves.Clear();

            foreach (Thing t in spawned)
            {
                if (t.Destroyed) { continue; }
                // A destroyed rack drops its missiles; colonists would haul them into the next test's launcher.
                if (t is Building_MissileRack rack) { rack.GetDirectlyHeldThings().ClearAndDestroyContents(); }
                t.Destroy(DestroyMode.Vanish);
            }
            spawned.Clear();
            foreach (Thing loose in map.listerThings.ThingsOfDef(LightMissileItem).ToList())
            {
                if (!loose.Destroyed) { loose.Destroy(DestroyMode.Vanish); }
            }

            // Enemy pawns, corpses and loose skyfallers left by the test.
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (t.Destroyed) { continue; }
                if (t is Pawn p && p.Faction == Enemy) { p.Destroy(DestroyMode.Vanish); continue; }
                if (t is Corpse c && c.InnerPawn?.Faction == Enemy) { c.Destroy(DestroyMode.Vanish); continue; }
                if (t is Skyfaller || t is Projectile_MissileBase || t is LaserDesignatorMark && t != enemySafeMark) { t.Destroy(DestroyMode.Vanish); continue; }
                if (t is Projectile) { t.Destroy(DestroyMode.Vanish); }
            }

            foreach (WorldObject wo in worldObjects)
            {
                if (wo.Destroyed) { continue; }
                if (wo is MapParent mp && mp.HasMap) { Current.Game.DeinitAndRemoveMap(mp.Map, false); }
                if (!wo.Destroyed) { wo.Destroy(); }
            }
            worldObjects.Clear();
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>().ToList()) { wo.Destroy(); }
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects.OfType<MissileWorldObject>().ToList()) { wo.Destroy(); }

            if (Find.CurrentMap != map) { Current.Game.CurrentMap = map; }
            CameraJumper.TryHideWorld();
            Find.Targeter.StopTargeting();
        }

        public static void DeleteTestSave()
        {
            try
            {
                string path = GenFilePaths.FilePathForSavedGame(TestSaveName);
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch (Exception) { }
        }
    }
}
