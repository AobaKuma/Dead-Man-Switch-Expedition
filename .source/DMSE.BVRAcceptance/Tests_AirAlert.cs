using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// NPC air-defense alert stages (docs/NPC防空警戒五階段_執行計畫.md): stage policy on real world tracks,
    /// pressure from events and its regional spread, blockade notice and activation, and route restrictions.
    /// </summary>
    public static class Tests_AirAlert
    {
        private static WorldComponent_FactionAirDefenseAlert Alert => WorldComponent_FactionAirDefenseAlert.Current;

        private static PlanetTile TileNear(PlanetTile center, float min, float max)
        {
            int count = Find.WorldGrid.TilesCount;
            for (int i = 0; i < count; i++)
            {
                PlanetTile t = new PlanetTile(i, center.Layer);
                float d = MissileWorldUtility.RangeDistance(center, t);
                if (d < min || d > max || Find.WorldObjects.AnyWorldObjectAt(t)) { continue; }
                Tile info = Find.WorldGrid[t];
                if (!info.PrimaryBiome.canBuildBase || info.hilliness == Hilliness.Impassable) { continue; }
                return t;
            }
            return PlanetTile.Invalid;
        }

        /// <summary>A1: stage 3 lets a missile aimed at an empty tile pass (but records intel); stage 4 engages it.</summary>
        public static IEnumerable<Wait> PassiveThenActive(TestContext t)
        {
            Site site = SpawnMissileBase(8f, 14f);
            t.Require(site != null, "an NPC missile base spawned 8–14 tiles away");
            yield return new WaitTicks(20);
            AirDefenseSiteAlert state = Alert?.StateFor(site);
            t.Require(state != null, "the base has an alert state");
            t.Check(state.stage == (int)AirDefenseStage.Passive, $"a missile base starts at stage 3 (got {state.stage})");
            t.Check(site.GetInspectString().ToLowerInvariant().Contains(WorldComponent_FactionAirDefenseAlert.StageLabel(3).ToLowerInvariant()), "inspect string shows the alert stage");

            PlanetTile empty = TileNear(site.Tile, 2f, 4f);
            t.Require(empty.Valid, "an empty tile next to the base exists");
            ScorerProjectile_WorldObject wo = LaunchPlayerMissile(empty, null, CruiseConfig("DMSE_Guidance_Inertial"));
            var observed = new WaitUntil(() => wo.Destroyed || wo.State.ObservationOf(Enemy) != null, 3000);
            yield return observed;
            t.Require(!wo.Destroyed && wo.State.ObservationOf(Enemy) != null, "the base faction records its own intel on the track");
            yield return new WaitTicks(300);
            t.Check(wo.State.interceptor == null && wo.State.lockUntil < 0, "stage 3 does not engage a missile aimed at an empty tile");
            t.Check(state.local >= 3.5f && state.local <= 12.1f, $"first transit adds local pressure ({state.local:0.##})");
            t.Check(state.stage == (int)AirDefenseStage.Passive, "a transit alone does not raise the stage");

            Alert.DevSetStage(site, (int)AirDefenseStage.Active);
            var engaged = new WaitUntil(() => wo.Destroyed || wo.State.interceptor != null, 3000);
            yield return engaged;
            t.Check(!wo.Destroyed && wo.State.interceptor != null && wo.State.interceptor.Faction == Enemy,
                "stage 4 engages the same transit missile");
            if (!wo.Destroyed && wo.State.interceptor != null) { wo.State.interceptor.hitChance = 0f; }
            yield return new WaitUntil(() => wo.Destroyed, 4000);
        }

        /// <summary>A2: repeated hits raise pressure, announce and then enforce a blockade; neighbors get regional pressure only.</summary>
        public static IEnumerable<Wait> PressureNoticeAndBlockade(TestContext t)
        {
            Site site = SpawnMissileBase(8f, 14f);
            t.Require(site != null, "an NPC missile base spawned");
            PlanetTile near = TileNear(site.Tile, 3f, 6f);
            t.Require(near.Valid, "a tile near the base exists for a second settlement");
            var neighbor = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            neighbor.Tile = near;
            neighbor.SetFaction(Enemy);
            Find.WorldObjects.Add(neighbor);
            Track(neighbor);
            yield return new WaitTicks(20);

            AirDefenseSiteAlert state = Alert.StateFor(site);
            AirDefenseSiteAlert other = Alert.StateFor(neighbor);
            t.Check(other.stage == (int)AirDefenseStage.EarlyWarning, $"a plain settlement with radar starts at stage 2 (got {other.stage})");
            int letters = Find.LetterStack.LettersListForReading.Count;

            Alert.DevInject(site, "hit");
            t.Check(state.local >= 24.9f && state.waves.Count == 1, $"a hit adds 25 local pressure and one attack wave ({state.local:0.#})");
            t.Check(state.stage >= (int)AirDefenseStage.Passive, "a hit raises the base to at least stage 3");
            Alert.DevInject(site, "hit");
            Alert.DevInject(site, "hit");
            t.Check(state.local >= 99f, $"repeat waves add the repeat bonus ({state.local:0.#})");
            t.Check(state.BlockadePending && state.stage == (int)AirDefenseStage.Active, "T >= 80 with several waves announces a blockade at stage 4");
            t.Check(Find.LetterStack.LettersListForReading.Count > letters, "the blockade notice reaches the player");
            t.Check(AirspaceRestrictionUtility.CheckPoint(Player, site.Tile).Restricted == false, "no restriction during the notice period");
            t.Check(other.local < 0.01f && other.regional > 30f && other.stage >= (int)AirDefenseStage.Passive,
                $"the neighbor only gets regional pressure (L {other.local:0.#}, R {other.regional:0.#}, stage {other.stage})");
            t.Check(!other.BlockadePending, "regional pressure alone never orders a blockade");

            int due = state.blockadeAt;
            yield return new WaitUntil(() => state.BlockadeActive, due - Find.TickManager.TicksGame + 600);
            t.Require(state.BlockadeActive, "the blockade takes effect after the notice period");
            BlockadeZone zone = AirspaceRestrictionUtility.ActiveZones.FirstOrDefault(z => z.site == site);
            t.Require(zone != null, "an active blockade zone exists");
            t.Check(zone.radius > 0f && zone.radius <= 8.01f, $"zone radius is capped by the configured intercept radius ({zone.radius:0.#})");
            t.Check(AirspaceRestrictionUtility.CheckPoint(Player, site.Tile).Restricted, "takeoff/landing at the base is restricted for the player");
            t.Check(!AirspaceRestrictionUtility.CheckPoint(Enemy, site.Tile).Restricted, "the blockading faction itself is not restricted");
            t.Check(AirspaceRestrictionUtility.CheckRoute(Player, Home.Tile, site.Tile).Restricted, "a route into the zone is restricted");
            PlanetTile far = TileNear(site.Tile, zone.radius + 4f, zone.radius + 8f);
            if (far.Valid)
            {
                bool crosses = AirspaceRestrictionUtility.SegmentDistanceTiles(zone.center,
                    MissileWorldUtility.TileCenter(Home.Tile), MissileWorldUtility.TileCenter(far)) <= zone.radius;
                t.Check(AirspaceRestrictionUtility.CheckRoute(Player, Home.Tile, far).Restricted == crosses,
                    $"a route outside the zone is judged by segment intersection (crosses={crosses})");
            }
            t.Check(site.GetInspectString().ToLowerInvariant().Contains(WorldComponent_FactionAirDefenseAlert.StageLabel(5).ToLowerInvariant()), "inspect string shows the blockade stage");

            site.GetComponent<WorldObjectComp_AirDefense>().DevDrain();
            AirspaceRestrictionUtility.Invalidate();
            t.Check(!AirspaceRestrictionUtility.ActiveZones.Any(z => z.site == site), "no interceptors left: the blockade cannot be enforced");
            t.Check(state.stage == (int)AirDefenseStage.Blockade, "the blockade order stays in place while it cannot be enforced");
            string inspect = site.GetInspectString();
            t.Note("inspect: " + inspect.Replace("\n", " | "));
            t.Check(inspect.ToLowerInvariant().Contains("DMSE.AirAlert.Unavailable.NoAmmo".Translate().ToString().ToLowerInvariant()), "inspect string says why it is unavailable");
        }
    }
}
