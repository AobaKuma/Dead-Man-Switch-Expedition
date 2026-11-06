using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Empero long-range gun (CompArtilleryStrike): world targeting rules, the departure → world track → map
    /// entry pipeline, and radar guidance through a fire-control radar's artillery channels.
    /// </summary>
    public static class Tests_Artillery
    {
        public static ThingDef Empero => ThingDef.Named("DMSE_Turret_Empero");
        public static ThingDef FireControlRadar => ThingDef.Named("DMSE_FireControlRadar");
        private static ThingDef Departure => ThingDef.Named("DMSE_ArtilleryDeparture_Empero");
        private static ThingDef Shell => ThingDef.Named("DMSE_Bullet_Shell_Empero");

        private static IEnumerable<WorldObject_ArtilleryStrike> Tracks
            => Find.WorldObjects.AllWorldObjects.OfType<WorldObject_ArtilleryStrike>();

        private static IntVec3 Center(Map map) => new IntVec3(map.Size.x / 2, 0, map.Size.z / 2);

        /// <summary>Defs, head rendering setup, targeting rules and the inspect line without a radar.</summary>
        public static IEnumerable<Wait> DefsAndTargeting(TestContext t)
        {
            CompProperties_ArtilleryStrike props = Empero.GetCompProperties<CompProperties_ArtilleryStrike>();
            t.Require(props != null, "Empero has CompArtilleryStrike");
            t.Check(props.departureProjectileDef == Departure && props.projectileDef == Shell
                && props.worldObjectDef?.worldObjectClass == typeof(WorldObject_ArtilleryStrike), "departure, shell and world track defs are wired");
            t.Check(props.SupportsRadarGuidance && props.guidedScatterRadius < props.scatterRadius, "radar guidance tightens scatter");
            t.Check(Departure.thingClass == typeof(Projectile_ArtilleryDeparture) && Shell.projectile.flyOverhead, "projectile classes");
            CompProperties_TurretFacingRenderer head = Empero.GetCompProperties<CompProperties_TurretFacingRenderer>();
            t.Check(head != null && head.texPath == "Things/Turrets/DMSE_Weapon_Empero" && head.drawSize == new Vector2(10f, 4f),
                "gun head is DMSE_Weapon_Empero drawn at 10x4");
            t.Check(Empero.graphicData.texPath == "Things/Building/FacilityBase", "base is FacilityBase");
            t.Check(FireControlRadar.GetCompProperties<CompProperties_FireControlRadar>().supportsArtilleryGuidance, "fire-control radar supports artillery guidance");

            Thing gun = SpawnBuilding(Empero, 2, 2);
            yield return WaitPowered();
            yield return new WaitTicks(2);
            CompArtilleryStrike art = gun.TryGetComp<CompArtilleryStrike>();
            t.Require(art.Active, "gun is Active once powered");
            t.Check(gun.TryGetComp<CompRefuelable>().Fuel == 12f, "SpawnBuilding loads 12 shells");
            t.Check(!art.RadarGuidanceAvailable, "no fire-control radar: no guidance");
            t.Check(art.CompInspectStringExtra() == "DMSE.Artillery.GuidanceOffline".Translate("14").ToString(), $"inspect line says unguided ({art.CompInspectStringExtra()})");

            PlanetTile home = Home.Tile;
            t.Check(!art.CanTargetTile(home, out string r1) && r1 == "DMSE.Artillery.TargetIsHome".Translate().ToString(), "own tile rejected");
            PlanetTile near = TileAtDistance(6f, 12f);
            PlanetTile far = TileAtDistance(25f, 40f);
            t.Require(near.Valid && far.Valid, "test tiles exist");
            t.Check(!art.CanTargetTile(near, out string rMap)
                && rMap == "DMSE.Artillery.MapNotSpawned".Translate().ToString(),
                $"tile without a spawned map rejected ({rMap})");
            t.Check(!art.CanTargetTile(far, out string r2) && r2.Contains("20"),
                $"tile beyond 20 rejected before the map check ({r2})");
            PlanetLayer orbit = Find.WorldGrid.Orbit;
            if (orbit != null)
            {
                t.Check(!art.CanTargetTile(new PlanetTile(0, orbit), out string r3) && r3 == "DMSE.Artillery.CrossLayer".Translate().ToString(), "other layer rejected");
            }
            else { t.Note("no second planet layer in this world: cross-layer check skipped"); }

            // Out of shells: sustained fire stops at the ammo check.
            gun.TryGetComp<CompRefuelable>().ConsumeFuel(12f);
            art.StartSustainedFire(near);
            yield return new WaitTicks(3);
            t.Check(!art.IsSustainedFiring, "empty gun stops sustained fire");
            t.Check(!Home.listerThings.ThingsOfDef(Departure).Any(), "nothing was fired");
            string fireLabel = "DMSE.Artillery.Fire".Translate();
            Command fire = gun.GetGizmos().OfType<Command>().FirstOrDefault(c => c.defaultLabel == fireLabel);
            string noShells = gun.TryGetComp<CompRefuelable>().Props.outOfFuelMessage;
            t.Check(fire != null && fire.Disabled && fire.disabledReason == noShells,
                $"empty gun: Fire button disabled with the no-shells reason ({fire?.disabledReason})");
        }

        /// <summary>
        /// Local reduced-charge fire: the gun turns its head onto a nearby raider, fires a plunging shell from the shared
        /// shell stock, and holds fire while world sustained fire owns the barrel.
        /// </summary>
        public static IEnumerable<Wait> LocalReducedChargeFire(TestContext t)
        {
            ThingDef reduced = ThingDef.Named("DMSE_Bullet_Shell_EmperoReduced");
            t.Check(reduced.projectile.flyOverhead, "reduced-charge shell flies overhead");
            Thing gun = SpawnBuilding(Empero, 2, 2);
            yield return WaitPowered();
            CompBuildingTurretGun local = gun.TryGetComp<CompBuildingTurretGun>();
            CompTurretFacingRenderer head = gun.TryGetComp<CompTurretFacingRenderer>();
            CompRefuelable shells = gun.TryGetComp<CompRefuelable>();
            CompArtilleryStrike art = gun.TryGetComp<CompArtilleryStrike>();
            t.Require(local != null && local.AttackVerb.verbProps.defaultProjectile == reduced, "gun has the reduced-charge local weapon");

            // A raider 30 cells east of the gun, outside min range 12.9, on open ground.
            IntVec3 cell = CellFinder.StandableCellNear(gun.Position + new IntVec3(30, 0, 6), Home, 6f);
            t.Require(cell.IsValid, "a standable cell for the raider");
            Pawn raider = MakeEnemyPawn();
            GenSpawn.Spawn(raider, cell, Home);
            Track(raider);

            Projectile shell = null;
            TickWatch fired = AcceptanceDriver.Watch(() =>
            {
                shell = shell ?? Home.listerThings.ThingsOfDef(reduced).OfType<Projectile>().FirstOrDefault();
                return shell != null;
            });
            var waitFire = new WaitUntil(() => fired.Seen, 900);
            yield return waitFire;
            t.Require(!waitFire.TimedOut, "the gun fires a reduced-charge shell at the raider");
            t.Check(shells.Fuel == 11f, $"one shell taken from the shared stock ({shells.Fuel})");
            t.Check(local.CurrentTarget.Thing == raider || local.CurrentTarget.IsValid, "it was engaging a target");
            t.Check(head.IsFacing(raider.DrawPos, 10f), $"head faces the raider ({head.CurRotation:0})");
            IntVec3 aim = Field<LocalTargetInfo>(shell, "intendedTarget").Cell;
            t.Check(aim.DistanceTo(raider.Position) <= 4f, $"aim point near the raider ({aim.DistanceTo(raider.Position):0.0} cells)");
            t.Screenshot("local_fire");

            // World sustained fire owns the barrel: local fire stops.
            PlanetTile near = TileAtDistance(6f, 12f);
            t.Require(near.Valid, "a world target tile");
            int oldWarmup = art.Props.warmupTicks;
            Override(() => art.Props.warmupTicks = 100000, () => art.Props.warmupTicks = oldWarmup);
            art.StartSustainedFire(near);
            float fuel = shells.Fuel;
            yield return new WaitTicks(2);
            t.Check(!local.CurrentTarget.IsValid, "local target cleared during world sustained fire");
            yield return new WaitTicks(900);
            t.Check(shells.Fuel == fuel && !Home.listerThings.ThingsOfDef(reduced).Any(p => p != shell), "no local shot while world fire is aiming");
            art.StopSustainedFire();

            // Out of shells: no target is taken up, not even a forced one (vanilla Verb would skip the shot silently
            // while the head kept tracking the enemy); one shell back and it fires at the forced cell.
            IntVec3 ground = gun.Position + new IntVec3(-2, 0, 45);
            if (!ground.InBounds(Home)) { ground = gun.Position + new IntVec3(45, 0, -2); }
            shells.ConsumeFuel(shells.Fuel);
            t.Require(local.TrySetForcedTarget(ground, out string why), $"forced ground target accepted ({why})");
            yield return new WaitTicks(600);
            t.Check(!local.CurrentTarget.IsValid, $"empty gun takes up no target ({local.CurrentTarget})");
            Projectile reloadedShot = null;
            TickWatch refired = AcceptanceDriver.Watch(() =>
            {
                reloadedShot = reloadedShot ?? Home.listerThings.ThingsOfDef(reduced).OfType<Projectile>().FirstOrDefault(p => p != shell);
                return reloadedShot != null;
            });
            shells.Refuel(1f);
            var waitRefire = new WaitUntil(() => refired.Seen, 1500);
            yield return waitRefire;
            t.Check(!waitRefire.TimedOut, "one shell loaded: the gun fires at the forced cell");
            t.Check(shells.Fuel == 0f, $"the loaded shell was used ({shells.Fuel})");
            local.ResetForcedTarget();
            if (!raider.Destroyed) { raider.Destroy(DestroyMode.Vanish); }
        }

        /// <summary>
        /// Local fire controls: buttons, minimum-range limit (auto and forced), hold fire, and a forced ground target
        /// that wins over an enemy in range.
        /// </summary>
        public static IEnumerable<Wait> LocalFireControls(TestContext t)
        {
            ThingDef reduced = ThingDef.Named("DMSE_Bullet_Shell_EmperoReduced");
            Thing gun = SpawnBuilding(Empero, 2, 2);
            yield return WaitPowered();
            CompBuildingTurretGun local = gun.TryGetComp<CompBuildingTurretGun>();
            CompRefuelable shells = gun.TryGetComp<CompRefuelable>();
            float minRange = local.AttackVerb.verbProps.minRange;

            List<Gizmo> gizmos = gun.GetGizmos().ToList();
            string forceLabel = "CommandSetForceAttackTarget".Translate(), holdLabel = "CommandHoldFire".Translate(), stopLabel = "CommandStopForceAttack".Translate();
            t.Check(gizmos.OfType<Command>().Any(c => c.defaultLabel == forceLabel), "force-target button");
            t.Check(gizmos.OfType<Command_Toggle>().Any(c => c.defaultLabel == holdLabel), "hold-fire toggle");
            t.Check(!gizmos.OfType<Command>().Any(c => c.defaultLabel == stopLabel), "no stop button without a forced target");

            // Minimum range: a forced target inside it is refused with the reason.
            IntVec3 tooClose = gun.Position + new IntVec3(0, 0, 6);
            t.Check(!local.TrySetForcedTarget(tooClose, out string why) && why == "DMSE.TurretGun.TooClose".Translate(minRange.ToString("0.#")).ToString(),
                $"forced target inside min range {minRange} refused ({why})");

            // A raider inside the minimum range is never engaged.
            IntVec3 nearCell = CellFinder.StandableCellNear(gun.Position + new IntVec3(7, 0, 0), Home, 2f);
            Pawn close = MakeEnemyPawn();
            GenSpawn.Spawn(close, nearCell, Home);
            Track(close);
            close.stances?.stunner?.StunFor(2000, null, addBattleLog: false);
            t.Require(close.Position.DistanceTo(gun.Position) < minRange, $"raider inside min range ({close.Position.DistanceTo(gun.Position):0.0})");
            yield return new WaitTicks(600);
            t.Check(shells.Fuel == 12f && !Home.listerThings.ThingsOfDef(reduced).Any(), "raider inside min range is not shelled");
            if (!close.Destroyed) { close.Destroy(DestroyMode.Vanish); }

            // Hold fire: a raider in range is ignored.
            IntVec3 farCell = CellFinder.StandableCellNear(gun.Position + new IntVec3(30, 0, 6), Home, 6f);
            Pawn raider = MakeEnemyPawn();
            GenSpawn.Spawn(raider, farCell, Home);
            Track(raider);
            raider.stances?.stunner?.StunFor(3000, null, addBattleLog: false);
            local.SetHoldFire(true);
            yield return new WaitTicks(600);
            t.Check(shells.Fuel == 12f && !local.CurrentTarget.IsValid, "hold fire: no shot at a raider in range");

            // Forced ground target beats the raider; hold fire is lifted by forcing.
            IntVec3 ground = gun.Position + new IntVec3(-2, 0, 45);
            if (!ground.InBounds(Home)) { ground = gun.Position + new IntVec3(45, 0, -2); }
            t.Require(local.TrySetForcedTarget(ground, out string why2), $"forced ground target accepted ({why2})");
            t.Check(!local.HoldFire, "forcing a target lifts hold fire");
            t.Check(gun.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == stopLabel), "stop button shown while forcing");
            Projectile shell = null;
            TickWatch fired = AcceptanceDriver.Watch(() =>
            {
                shell = shell ?? Home.listerThings.ThingsOfDef(reduced).OfType<Projectile>().FirstOrDefault();
                return shell != null;
            });
            var waitFire = new WaitUntil(() => fired.Seen, 900);
            yield return waitFire;
            t.Require(!waitFire.TimedOut, "the gun fires at the forced target");
            IntVec3 aim = Field<LocalTargetInfo>(shell, "intendedTarget").Cell;
            t.Check(aim.DistanceTo(ground) <= 3f && aim.DistanceTo(raider.Position) > 10f,
                $"shell aimed at the forced cell, not the raider ({aim.DistanceTo(ground):0.0} from it)");

            local.ResetForcedTarget();
            t.Check(!local.ForcedTarget.IsValid && !gun.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == stopLabel), "stop forced attack clears it");
            local.SetHoldFire(true);
            if (!raider.Destroyed) { raider.Destroy(DestroyMode.Vanish); }
        }

        /// <summary>
        /// Unguided shot at an NPC base: an unspawned base map is refused, the spawned one is aimed at (warmup turns the
        /// head), one shell is used, the departure flies off the map edge, the world track arrives, reuses that same map
        /// and keeps it, and the shell lands near its centre.
        /// </summary>
        public static IEnumerable<Wait> UnguidedStrike(TestContext t)
        {
            Site site = SpawnMissileBase(6f, 14f);
            t.Require(site != null, "an NPC missile base 6-14 tiles away");
            t.Require(!site.HasMap, "the base map does not exist yet");
            Thing gun = SpawnBuilding(Empero, 2, 2);
            yield return WaitPowered();
            CompArtilleryStrike art = gun.TryGetComp<CompArtilleryStrike>();
            CompTurretFacingRenderer head = gun.TryGetComp<CompTurretFacingRenderer>();
            CompRefuelable shells = gun.TryGetComp<CompRefuelable>();

            // Artillery does not generate maps: a tile whose map is not spawned cannot be targeted.
            t.Check(!art.CanTargetTile(site.Tile, out string notSpawned)
                && notSpawned == "DMSE.Artillery.MapNotSpawned".Translate().ToString(),
                $"base without a spawned map is refused ({notSpawned})");
            t.Check(!site.HasMap, "targeting did not generate the base map");
            Map map = SpawnSiteMap(site);
            t.Require(map != null && site.HasMap, "the base map is spawned up front");
            t.Require(art.CanTargetTile(site.Tile, out string why), $"base tile is a valid target once its map is spawned ({why})");

            // The world bearing must be the same one the world map shows (vanilla PlanetLayer heading), or the turret
            // and the shell leave/enter on the mirrored side of the map.
            PlanetLayer layer = Home.Tile.Layer;
            float bearing = ArtilleryStrikeUtility.WorldDirectionAngle(Home.Tile, site.Tile);
            t.Check(Mathf.Abs(Mathf.DeltaAngle(bearing, layer.GetHeadingFromTo(Home.Tile, site.Tile))) < 0.5f,
                $"the artillery bearing is the world-map heading ({bearing:0.0} vs {layer.GetHeadingFromTo(Home.Tile, site.Tile):0.0})");
            float backBearing = ArtilleryStrikeUtility.WorldDirectionAngle(site.Tile, Home.Tile);
            t.Check(Mathf.Abs(Mathf.Abs(Mathf.DeltaAngle(bearing, backBearing)) - 180f) < 0.5f,
                $"the shell enters the target map from the side facing home ({backBearing:0.0} vs {bearing:0.0})");

            Projectile departure = null;
            TickWatch departed = AcceptanceDriver.Watch(() =>
            {
                departure = departure ?? Home.listerThings.ThingsOfDef(Departure).OfType<Projectile>().FirstOrDefault();
                return departure != null;
            });
            art.StartSustainedFire(site.Tile);
            yield return new WaitTicks(150);
            t.Check(art.IsSustainedFiring && !departed.Seen, "still aiming 150 ticks in (warmup 300)");
            t.Check(Mathf.Abs(Mathf.DeltaAngle(art.AimAngle, bearing)) < 0.5f, $"aim angle is the world bearing ({art.AimAngle:0.0} vs {bearing:0.0})");

            var waitFire = new WaitUntil(() => departed.Seen, 600);
            yield return waitFire;
            t.Require(!waitFire.TimedOut, "the gun fires after the warmup (and once the barrel is on the bearing)");
            IntVec3 exit = Field<LocalTargetInfo>(departure, "intendedTarget").Cell;
            float exitBearing = (exit.ToVector3Shifted() - gun.TrueCenter()).AngleFlat();
            t.Check(Mathf.Abs(Mathf.DeltaAngle(exitBearing, bearing)) < 2f,
                $"the departure flies along the barrel bearing to the edge ({exit}, {exitBearing:0.0} vs {bearing:0.0})");
            t.Check(shells.Fuel == 11f, $"one shell used ({shells.Fuel})");
            t.Check(!art.LastShotGuided, "no radar: unguided");
            t.Check(Mathf.Abs(Mathf.DeltaAngle(head.CurRotation, bearing)) < 2.5f, $"head faces the bearing ({head.CurRotation:0.0} vs {bearing:0.0})");
            t.Screenshot("fired");

            var waitTrack = new WaitUntil(() => Tracks.Any(), 1200);
            yield return waitTrack;
            t.Require(!waitTrack.TimedOut, "the departure reaches the map edge and becomes a world track");
            WorldObject_ArtilleryStrike track = Tracks.First();
            Track(track);
            t.Check(!track.guided && Mathf.Approximately(track.scatterRadius, 14f), "track carries unguided scatter 14");
            t.Check(track.destinationTile == site.Tile && track.InitialTile == Home.Tile, "track runs home → base");
            art.StopSustainedFire();

            int launched = Find.TickManager.TicksGame;
            var waitArrive = new WaitUntil(() => track.Destroyed, 3000);
            yield return waitArrive;
            t.Require(!waitArrive.TimedOut, "the track arrives");
            float tiles = MissileWorldUtility.RangeDistance(Home.Tile, site.Tile);
            int flight = Find.TickManager.TicksGame - launched;
            t.Note($"world flight took {flight} ticks for {tiles:0.0} tiles");
            float expectedFlight = tiles * art.Props.worldTicksPerTile;
            t.Check(Mathf.Abs(flight - expectedFlight) <= expectedFlight * 0.25f + 10f,
                $"world flight follows worldTicksPerTile ({flight} vs ~{expectedFlight:0})");
            t.Check(Find.CurrentMap == Home && !WorldRendererUtility.WorldSelected, "arrival does not move the camera");
            t.Require(site.HasMap, "the base map still exists");
            t.Check(site.Map == map, "the salvo lands on the existing map (no map is generated for the shot)");
            t.Check(GameComponent_MissileEngage.Comp.times.ContainsKey(map), "the map is kept until the shells land");

            Projectile shell = null;
            IntVec3 entry = IntVec3.Invalid;
            TickWatch spawnedShell = AcceptanceDriver.Watch(() =>
            {
                if (shell == null)
                {
                    shell = map.listerThings.ThingsOfDef(Shell).OfType<Projectile>().FirstOrDefault();
                    // The first observed Position can already be a few cells inside the map (the shell moves every
                    // tick); Projectile.origin is the exact cell SpawnShell launched it from.
                    entry = shell != null ? Field<Vector3>(shell, "origin").ToIntVec3() : IntVec3.Invalid;
                }
                return shell != null;
            });
            var waitShell = new WaitUntil(() => spawnedShell.Seen, 120);
            yield return waitShell;
            t.Require(!waitShell.TimedOut, "the shell enters the base map");
            IntVec3 aim = Field<LocalTargetInfo>(shell, "intendedTarget").Cell;
            float off = aim.DistanceTo(Center(map));
            t.Check(off <= 14f + 1.5f, $"unguided aim point within scatter of the map centre ({off:0.0} cells)");
            int edgeDist = Mathf.Min(Mathf.Min(entry.x, entry.z), Mathf.Min(map.Size.x - 1 - entry.x, map.Size.z - 1 - entry.z));
            float expectedBearing = ArtilleryStrikeUtility.WorldDirectionAngle(site.Tile, Home.Tile);
            float entryBearing = (entry.ToVector3Shifted() - Center(map).ToVector3Shifted()).AngleFlat();
            t.Check(edgeDist <= 2, $"the shell enters at the map edge ({entry})");
            t.Check(Mathf.Abs(Mathf.DeltaAngle(entryBearing, expectedBearing)) <= 3f,
                $"it comes in on the true bearing from the gun (entry bearing {entryBearing:0.0}, home bearing {expectedBearing:0.0})");
            var waitImpact = new WaitUntil(() => shell.Destroyed, 900);
            yield return waitImpact;
            t.Check(!waitImpact.TimedOut, "the shell lands");
        }

        /// <summary>
        /// Radar-guided shots: a free fire-control channel is held per shot, scatter shrinks to 3, the salvo aims at an
        /// enemy turret or powered building; a full channel pool leaves the next shot unguided.
        /// </summary>
        public static IEnumerable<Wait> RadarGuidedStrike(TestContext t)
        {
            Site site = SpawnMissileBase(6f, 14f);
            t.Require(site != null, "an NPC missile base 6-14 tiles away");
            Map map = SpawnSiteMap(site);
            t.Require(map != null && site.HasMap, "the base map is spawned up front (artillery only strikes existing maps)");
            Thing gun = SpawnBuilding(Empero, 2, 2);
            Thing radar = SpawnBuilding(FireControlRadar, 10, 2);
            yield return WaitPowered();
            yield return new WaitTicks(2);
            CompArtilleryStrike art = gun.TryGetComp<CompArtilleryStrike>();
            CompFireControlRadar fc = radar.TryGetComp<CompFireControlRadar>();
            CompProperties_ArtilleryStrike props = art.Props;
            t.Require(fc.Active && fc.FreeArtilleryChannels == 4, $"radar online with 4 free artillery channels ({fc.FreeArtilleryChannels})");
            t.Check(art.RadarGuidanceAvailable, "guidance available");
            t.Check(art.CompInspectStringExtra() == "DMSE.Artillery.GuidanceOnline".Translate("3").ToString(), $"inspect line says guided ({art.CompInspectStringExtra()})");

            int oldCooldown = props.cooldownTicks, oldWarmup = props.warmupTicks;
            Override(() => { props.cooldownTicks = 600; props.warmupTicks = 60; },
                     () => { props.cooldownTicks = oldCooldown; props.warmupTicks = oldWarmup; });

            int freeBefore = HomeBVR.Channels.Free(Player);
            TickWatch departed = AcceptanceDriver.Watch(() => Home.listerThings.ThingsOfDef(Departure).Any());
            art.StartSustainedFire(site.Tile);
            var waitFire = new WaitUntil(() => departed.Seen, 600);
            yield return waitFire;
            t.Require(!waitFire.TimedOut, "the gun fires");
            t.Check(art.LastShotGuided, "shot is radar-guided");
            t.Check(art.ReservedRadar == fc && fc.OccupiedArtilleryChannels == 1, "one artillery channel held on the radar");
            t.Check(HomeBVR.Channels.Free(Player) == freeBefore - 1, $"shared ledger lost one channel ({HomeBVR.Channels.Free(Player)} of {freeBefore})");

            // Second shot while the first channel is still held (cooldown 600 < hold 900): the gun re-books, never holds two.
            CompRefuelable ammo = gun.TryGetComp<CompRefuelable>();
            var waitSecond = new WaitUntil(() => ammo.Fuel <= 10f, 900);
            yield return waitSecond;
            t.Check(!waitSecond.TimedOut, "a second shot follows the cooldown");
            t.Check(fc.OccupiedArtilleryChannels == 1, $"still one channel for one gun ({fc.OccupiedArtilleryChannels})");
            art.StopSustainedFire();

            var waitTrack = new WaitUntil(() => Tracks.Any(), 1200);
            yield return waitTrack;
            t.Require(!waitTrack.TimedOut, "a world track exists");
            foreach (var tr in Tracks.ToList()) { Track(tr); }
            WorldObject_ArtilleryStrike track = Tracks.First();
            t.Check(track.guided && Mathf.Approximately(track.scatterRadius, props.guidedScatterRadius), "track carries guidance and scatter 3");

            var waitArrive = new WaitUntil(() => track.Destroyed && site.HasMap, 3000);
            yield return waitArrive;
            t.Require(!waitArrive.TimedOut, "the track arrives on the base map");
            t.Check(site.Map == map, "the salvo reuses the existing map (no map is generated for the shot)");
            IntVec3 expected = ArtilleryStrikeUtility.FindGuidedTarget(map, Player, Center(map));
            Thing victim = expected.GetThingList(map).FirstOrDefault(x => x.Faction != null && x.HostileTo(Player));
            t.Check(victim is Building, $"guided target is a hostile building ({victim?.def.defName ?? "none"} at {expected})");
            t.Check(victim is Building_Turret || victim?.TryGetComp<CompPowerTrader>() != null, "guided target is a turret or powered building");

            Projectile shell = null;
            TickWatch spawnedShell = AcceptanceDriver.Watch(() =>
            {
                shell = shell ?? map.listerThings.ThingsOfDef(Shell).OfType<Projectile>().FirstOrDefault();
                return shell != null;
            });
            var waitShell = new WaitUntil(() => spawnedShell.Seen, 120);
            yield return waitShell;
            t.Require(!waitShell.TimedOut, "the guided shell enters the base map");
            IntVec3 aim = Field<LocalTargetInfo>(shell, "intendedTarget").Cell;
            t.Check(aim.DistanceTo(expected) <= props.guidedScatterRadius + 1.5f, $"guided aim within 3 cells of the target ({aim.DistanceTo(expected):0.0})");
            t.Screenshot("guided_shell");

            // Channel released after the hold time.
            var waitRelease = new WaitUntil(() => fc.OccupiedArtilleryChannels == 0, 1200);
            yield return waitRelease;
            t.Check(!waitRelease.TimedOut, "channel released after the hold time");
            t.Check(art.ReservedRadar == null, "gun no longer references the radar");

            // Pool exhausted by other users: next shot is unguided.
            for (int i = 0; i < 4; i++) { fc.OccupyArtilleryChannel(); }
            Override(() => { }, () => { for (int i = 0; i < 4; i++) { fc.ReleaseArtilleryChannel(); } });
            t.Check(!art.RadarGuidanceAvailable, "no free channel: guidance unavailable");
            CompRefuelable shells = gun.TryGetComp<CompRefuelable>();
            float before = shells.Fuel;
            TickWatch third = AcceptanceDriver.Watch(() => shells.Fuel < before);
            art.StartSustainedFire(site.Tile);
            var waitThird = new WaitUntil(() => third.Seen, 900);
            yield return waitThird;
            t.Check(!waitThird.TimedOut, "the gun still fires without a channel");
            t.Check(!art.LastShotGuided && art.ReservedRadar == null, "that shot is unguided and books nothing");
            art.StopSustainedFire();
        }

        /// <summary>
        /// Fire modes: a ranging shot logs its impact for the faction, fire for effect from another gun lands on that
        /// shared point, each fire-for-effect shot tightens its scatter by a quarter of the base scatter, stopping the
        /// mission resets it, and a map without a logged impact refuses fire for effect.
        /// </summary>
        public static IEnumerable<Wait> FireModes(TestContext t)
        {
            Site site = SpawnMissileBase(6f, 14f);
            t.Require(site != null, "an NPC missile base 6-14 tiles away");
            Map map = SpawnSiteMap(site);
            t.Require(map != null && site.HasMap, "the base map is spawned up front");
            Thing gunA = SpawnBuilding(Empero, 2, 2);
            Thing gunB = SpawnBuilding(Empero, 10, 2);
            Thing radar = SpawnBuilding(FireControlRadar, 18, 2);
            yield return WaitPowered();
            yield return new WaitTicks(2);

            CompArtilleryStrike artA = gunA.TryGetComp<CompArtilleryStrike>();
            CompArtilleryStrike artB = gunB.TryGetComp<CompArtilleryStrike>();
            CompFireControlRadar fc = radar.TryGetComp<CompFireControlRadar>();
            CompProperties_ArtilleryStrike props = artA.Props;
            MapComponent_ArtilleryStrikes comp = map.GetComponent<MapComponent_ArtilleryStrikes>();
            t.Require(comp != null, "the base map has the artillery scheduler");
            t.Check(fc != null && fc.Active && fc.FreeArtilleryChannels > 0, "the fire-control radar is online");
            int oldCooldown = props.cooldownTicks, oldWarmup = props.warmupTicks;
            Override(() => { props.cooldownTicks = 600; props.warmupTicks = 60; },
                     () => { props.cooldownTicks = oldCooldown; props.warmupTicks = oldWarmup; });

            string rangingLabel = "DMSE.Artillery.ModeRanging".Translate().ToString();
            string effectLabel = "DMSE.Artillery.ModeEffect".Translate().ToString();

            // 1) No ranging impact on that map yet: fire for effect is refused, and it does not use guidance.
            Command_Toggle mode = gunA.GetGizmos().OfType<Command_Toggle>().FirstOrDefault(c => c.defaultLabel == rangingLabel);
            t.Require(mode != null, "the mode toggle is offered while ranging");
            t.Check(mode.isActive != null && !mode.isActive(), "the toggle starts inactive (ranging)");
            mode.toggleAction();
            t.Check(artA.FireForEffect, "toggling switches the gun to fire for effect");
            t.Check(gunA.GetGizmos().OfType<Command_Toggle>().Any(c => c.defaultLabel == effectLabel),
                "the toggle shows the fire-for-effect label");
            t.Check(artA.CompInspectStringExtra() == effectLabel, $"inspect line shows the mode ({artA.CompInspectStringExtra()})");
            t.Check(!comp.TryGetRegisteredImpact(Player, out _, out _), "no impact point is logged yet");
            t.Check(!artA.CanTargetTile(site.Tile, out string noReg)
                && noReg == "DMSE.Artillery.NoRegistration".Translate().ToString(),
                $"a map without a ranging impact refuses fire for effect ({noReg})");
            t.Check(!artA.RadarGuidanceAvailable, "fire for effect does not use radar guidance");
            artA.SetFireForEffect(false);
            t.Check(!artA.FireForEffect && artA.RadarGuidanceAvailable, "back to ranging: guidance is available again");

            // 2) A ranging shot logs its actual impact for the faction.
            Projectile_ArtilleryDeparture departureA = null;
            TickWatch firedA = AcceptanceDriver.Watch(() =>
            {
                departureA = departureA ?? Home.listerThings.ThingsOfDef(Departure)
                    .OfType<Projectile_ArtilleryDeparture>().FirstOrDefault();
                return departureA != null;
            });
            artA.StartSustainedFire(site.Tile);
            var waitA = new WaitUntil(() => firedA.Seen, 600);
            yield return waitA;
            t.Require(!waitA.TimedOut, "the ranging shot leaves the barrel");
            t.Check(departureA.artilleryTargetCell == Center(map),
                $"ranging aims at the map centre ({departureA.artilleryTargetCell})");
            t.Check(artA.LastShotGuided, "a ranging shot still uses radar guidance");
            artA.StopSustainedFire();

            var waitTrackA = new WaitUntil(
                () => Tracks.Any(tr => tr.destinationTile == site.Tile && !tr.fireForEffect), 1200);
            yield return waitTrackA;
            t.Require(!waitTrackA.TimedOut, "the ranging shot becomes a world track");
            WorldObject_ArtilleryStrike trackA = Tracks.First(tr => tr.destinationTile == site.Tile && !tr.fireForEffect);
            Track(trackA);
            t.Check(trackA.targetCell == Center(map), "the ranging track aims at the map centre");

            int shellWaitStart = Find.TickManager.TicksGame;
            Projectile shellA = null;
            TickWatch shellSeen = AcceptanceDriver.Watch(() =>
            {
                shellA = shellA ?? map.listerThings.ThingsOfDef(Shell).OfType<Projectile>().FirstOrDefault();
                return shellA != null;
            });
            var waitShellA = new WaitUntil(() => shellSeen.Seen, 2400);
            yield return waitShellA;
            t.Require(!waitShellA.TimedOut, "the ranging shell reaches the base map");
            IntVec3 aim = Field<LocalTargetInfo>(shellA, "intendedTarget").Cell;
            t.Check(comp.TryGetRegisteredImpact(Player, out IntVec3 logged, out int loggedTick) && logged == aim,
                $"the ranging impact is logged ({logged} vs shell aim {aim})");
            t.Check(loggedTick >= shellWaitStart && loggedTick <= Find.TickManager.TicksGame,
                $"the impact was logged when the shell spawned ({loggedTick}, waited from {shellWaitStart}, now {Find.TickManager.TicksGame})");
            t.Check(!comp.TryGetRegisteredImpact(Enemy, out _, out _), "another faction does not share the point");

            // 3) Move the logged impact, then fire for effect from the second gun: it lands on that shared point.
            IntVec3 far = new IntVec3(
                Mathf.Clamp(Center(map).x + 60, 1, map.Size.x - 2), 0,
                Mathf.Clamp(Center(map).z - 40, 1, map.Size.z - 2));
            comp.RegisterImpact(Player, far);
            t.Check(comp.TryGetRegisteredImpact(Player, out IntVec3 moved, out _) && moved == far,
                $"a newer ranging impact replaces the old one ({moved})");

            var waitRelease = new WaitUntil(() => fc.OccupiedArtilleryChannels == 0, 1200);
            yield return waitRelease;
            t.Check(!waitRelease.TimedOut, "the ranging gun's radar channel is released");

            artB.SetFireForEffect(true);
            t.Require(artB.CanTargetTile(site.Tile, out string okWhy),
                $"fire for effect can target a map with a logged impact ({okWhy})");
            Projectile_ArtilleryDeparture departB = null;
            TickWatch firedB = AcceptanceDriver.Watch(() =>
            {
                departB = departB ?? Home.listerThings.ThingsOfDef(Departure)
                    .OfType<Projectile_ArtilleryDeparture>().FirstOrDefault(d => d != departureA);
                return departB != null;
            });
            artB.StartSustainedFire(site.Tile);
            var waitB = new WaitUntil(() => firedB.Seen, 900);
            yield return waitB;
            t.Require(!waitB.TimedOut, "the second gun fires for effect");
            t.Check(departB.artilleryTargetCell == far,
                $"fire for effect aims at the shared logged point ({departB.artilleryTargetCell} vs {far})");
            t.Check(Mathf.Approximately(departB.artilleryScatterRadius, props.scatterRadius),
                $"the first fire-for-effect shot keeps the base scatter {props.scatterRadius} ({departB.artilleryScatterRadius})");
            t.Check(!departB.artilleryGuided && !artB.LastShotGuided, "fire for effect is unguided");
            t.Check(artB.ReservedRadar == null && fc.OccupiedArtilleryChannels == 0,
                $"fire for effect books no radar channel ({fc.OccupiedArtilleryChannels})");
            t.Check(!artB.RadarGuidanceAvailable, "guidance reports unavailable in fire for effect");
            string inspectB = artB.CompInspectStringExtra();
            t.Check(inspectB.Contains(effectLabel) && inspectB.Contains($"({far.x}, {far.z})"),
                $"inspect line shows the impact point ({inspectB})");

            var waitTrackB = new WaitUntil(
                () => Tracks.Any(tr => tr.fireForEffect && tr.targetCell == far), 1500);
            yield return waitTrackB;
            t.Check(!waitTrackB.TimedOut, "the fire-for-effect world track carries the mode and the point");
            foreach (WorldObject_ArtilleryStrike tr in Tracks.ToList()) { Track(tr); }

            // 4) Every fire-for-effect shot tightens the scatter by a quarter of the base scatter.
            Projectile_ArtilleryDeparture departB2 = null;
            TickWatch firedB2 = AcceptanceDriver.Watch(() =>
            {
                departB2 = departB2 ?? Home.listerThings.ThingsOfDef(Departure)
                    .OfType<Projectile_ArtilleryDeparture>().FirstOrDefault(d => d != departureA && d != departB);
                return departB2 != null;
            });
            var waitB2 = new WaitUntil(() => firedB2.Seen, 1200);
            yield return waitB2;
            t.Require(!waitB2.TimedOut, "the gun keeps firing for effect");
            t.Check(Mathf.Approximately(departB2.artilleryScatterRadius, props.scatterRadius * 0.75f),
                $"scatter tightens by a quarter ({departB2.artilleryScatterRadius} vs {props.scatterRadius * 0.75f})");
            t.Check(departB2.artilleryTargetCell == far, "the logged point does not move between shots");
            t.Check(comp.TryGetRegisteredImpact(Player, out IntVec3 afterFfe, out _) && afterFfe == far,
                "fire-for-effect shots never rewrite the logged point");

            // 5) Stopping the mission resets the tightening.
            artB.StopSustainedFire();
            artB.StartSustainedFire(site.Tile);
            Projectile_ArtilleryDeparture departB3 = null;
            TickWatch firedB3 = AcceptanceDriver.Watch(() =>
            {
                departB3 = departB3 ?? Home.listerThings.ThingsOfDef(Departure)
                    .OfType<Projectile_ArtilleryDeparture>().FirstOrDefault(
                        d => d != departureA && d != departB && d != departB2);
                return departB3 != null;
            });
            var waitB3 = new WaitUntil(() => firedB3.Seen, 1200);
            yield return waitB3;
            t.Require(!waitB3.TimedOut, "the restarted mission fires");
            t.Check(Mathf.Approximately(departB3.artilleryScatterRadius, props.scatterRadius),
                $"stopping resets the scatter to {props.scatterRadius} ({departB3.artilleryScatterRadius})");
            artB.StopSustainedFire();
            t.Screenshot("fire_modes");
        }

        /// <summary>
        /// Heavy mortar: the vanilla switchable projectile (gunner-loaded mortar shell) decides the BVR warhead, the
        /// mission waits for a reload instead of cancelling, the crew requirement is reported, and the loaded shell
        /// type travels through the world track onto the target map.
        /// </summary>
        public static IEnumerable<Wait> HeavyMortar(TestContext t)
        {
            ThingDef mortarDef = ThingDef.Named("DMSE_Turret_HeavyMortar");
            ThingDef gunDef = ThingDef.Named("DMSE_Gun_HeavyMortar");
            ThingDef incendiary = ThingDef.Named("Shell_Incendiary");
            ThingDef incendiaryProj = ThingDef.Named("Bullet_Shell_Incendiary");
            ThingDef he = ThingDef.Named("Shell_HighExplosive");
            ThingDef heProj = ThingDef.Named("Bullet_Shell_HighExplosive");

            CompProperties_ArtilleryStrike props = mortarDef.GetCompProperties<CompProperties_ArtilleryStrike>();
            t.Require(props != null, "the heavy mortar has CompArtilleryStrike");
            t.Check(mortarDef.thingClass == typeof(Building_HeavyMortar), "turret class keeps the barrel on the world bearing");
            t.Check(mortarDef.building.turretGunDef == gunDef, "turret gun def is wired");
            t.Check(mortarDef.size.x == 2 && mortarDef.size.z == 2, "2x2 footprint");
            t.Check(mortarDef.building.IsMortar, "counts as a mortar (overhead fire)");
            t.Check(mortarDef.uiIconPath == "Things/Turrets/HeavyMortar_icon", "menu icon");
            t.Check(props.departureProjectileDef?.thingClass == typeof(Projectile_ArtilleryDeparture), "departure projectile class");
            t.Check(!props.SupportsRadarGuidance && Mathf.Approximately(props.scatterRadius, 9f)
                && Mathf.Approximately(props.maxRangeTiles, 24f), "BVR numbers (scatter 9, range 24, no radar guidance)");
            t.Check(gunDef.GetCompProperties<CompProperties_ChangeableProjectile>() != null,
                "the gun uses the vanilla switchable projectile comp");
            t.Check(gunDef.building.fixedStorageSettings.filter.Allows(incendiary),
                "the fixed shell filter accepts mortar shells");
            t.Check(gunDef.building.defaultStorageSettings.filter.Allows(he), "high-explosive is loaded by default");
            t.Check(!gunDef.building.defaultStorageSettings.filter.Allows(ThingDef.Named("Shell_Firefoam")),
                "firefoam is disallowed by default");

            Thing mortar = SpawnBuilding(mortarDef, 2, 2);
            Building_HeavyMortar hm = (Building_HeavyMortar)mortar;
            CompArtilleryStrike art = mortar.TryGetComp<CompArtilleryStrike>();
            CompMannable mannable = mortar.TryGetComp<CompMannable>();
            CompChangeableProjectile shells = hm.gun.TryGetComp<CompChangeableProjectile>();
            CompRefuelable barrel = mortar.TryGetComp<CompRefuelable>();
            Pawn gunner = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
            Track(gunner);
            t.Require(mannable != null && shells != null && barrel != null && gunner != null,
                "mannable comp, barrel and switchable projectile comp");

            t.Check(!art.Active, "no gunner: the mortar is inactive");
            Command fire = mortar.GetGizmos().OfType<Command>().FirstOrDefault(c => c.defaultLabel == "DMSE.Artillery.Fire".Translate());
            t.Check(fire != null && fire.Disabled && fire.disabledReason == "DMSE.Artillery.NoCrew".Translate().ToString(),
                $"Fire is disabled without a gunner ({fire?.disabledReason})");

            // A real crew: a spawned colonist takes the vanilla "man turret" job, which loads the shell.
            IntVec3 stand = CellFinder.StandableCellNear(mortar.Position + new IntVec3(-3, 0, 0), Home, 3f);
            t.Require(stand.IsValid, "a standable cell for the gunner");
            GenSpawn.Spawn(gunner, stand, Home);
            mannable.ManForATick(gunner);
            t.Check(art.Active, "manned: the mortar is active");
            fire = mortar.GetGizmos().OfType<Command>().FirstOrDefault(c => c.defaultLabel == "DMSE.Artillery.Fire".Translate());
            t.Check(fire != null && fire.Disabled && fire.disabledReason == "DMSE.Artillery.NoLoadedShell".Translate().ToString(),
                $"Fire is disabled without a loaded shell ({fire?.disabledReason})");
            t.Check(shells.allowedShellsSettings.AllowedToAccept(incendiary),
                "the shell storage filter accepts incendiary shells");
            GenSpawn.Spawn(ThingMaker.MakeThing(incendiary), mortar.Position + new IntVec3(-2, 0, 0), Home);
            gunner.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar), JobTag.Misc);
            var waitLoaded = new WaitUntil(() => shells.Loaded && mannable.MannedNow, 900);
            yield return waitLoaded;
            t.Require(!waitLoaded.TimedOut, "the gunner mans the mortar and loads the shell");
            t.Check(shells.Projectile == incendiaryProj, $"the gunner loaded the incendiary shell ({shells.LoadedShell})");
            t.Check(art.Active, "the crew is on the mortar");
            fire = mortar.GetGizmos().OfType<Command>().FirstOrDefault(c => c.defaultLabel == "DMSE.Artillery.Fire".Translate());
            t.Check(fire != null && !fire.Disabled, $"Fire is available once manned and loaded ({fire?.disabledReason})");

            Site site = SpawnMissileBase(6f, 14f);
            t.Require(site != null, "an NPC missile base 6-14 tiles away");
            Map map = SpawnSiteMap(site);
            t.Require(map != null && site.HasMap, "the target map is spawned up front");
            t.Require(art.CanTargetTile(site.Tile, out string why), $"the base tile is a valid target ({why})");

            int oldCooldown = props.cooldownTicks, oldWarmup = props.warmupTicks;
            float oldWorld = props.worldTicksPerTile;
            Override(() => { props.cooldownTicks = 120; props.warmupTicks = 30; props.worldTicksPerTile = 20f; },
                     () => { props.cooldownTicks = oldCooldown; props.warmupTicks = oldWarmup; props.worldTicksPerTile = oldWorld; });

            Projectile_ArtilleryDeparture firstShot = null;
            var waitFirst = new WaitUntil(() =>
            {
                firstShot = firstShot ?? Home.listerThings.ThingsOfDef(props.departureProjectileDef)
                    .OfType<Projectile_ArtilleryDeparture>().FirstOrDefault();
                return firstShot != null;
            }, 900);
            art.StartSustainedFire(site.Tile);
            yield return waitFirst;
            t.Require(!waitFirst.TimedOut, "the mortar fires its BVR shot");
            t.Check(firstShot.artilleryProjectileDef == incendiaryProj,
                $"the loaded shell type is the BVR projectile ({firstShot.artilleryProjectileDef})");
            t.Check(!shells.Loaded, "the shot consumed the loaded shell");
            t.Check(Mathf.Approximately(barrel.Fuel, 19f), $"one barrel shot used ({barrel.Fuel})");

            // No shell in the breech: the gunner leaves to fetch one and the mission waits.
            var waitGunnerLeft = new WaitUntil(() => !mannable.MannedNow, 600);
            yield return waitGunnerLeft;
            t.Check(!waitGunnerLeft.TimedOut, "the gunner leaves the tube to reload");
            t.Check(art.IsSustainedFiring, "no shell loaded: the mission is paused, not cancelled");

            // A different shell type in the stockpile: the next BVR round changes with it.
            GenSpawn.Spawn(ThingMaker.MakeThing(he), mortar.Position + new IntVec3(-2, 0, 0), Home);
            gunner.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar), JobTag.Misc);
            var waitReloaded = new WaitUntil(() => shells.Loaded && mannable.MannedNow, 1200);
            yield return waitReloaded;
            t.Require(!waitReloaded.TimedOut, "the gunner reloads from the new stock");
            t.Check(shells.Projectile == heProj, $"the gunner switched to the high-explosive shell ({shells.LoadedShell})");
            t.Check(art.Active, "the crew is back on the mortar");

            Projectile_ArtilleryDeparture secondShot = null;
            var waitSecond = new WaitUntil(() =>
            {
                secondShot = secondShot ?? Home.listerThings.ThingsOfDef(props.departureProjectileDef)
                    .OfType<Projectile_ArtilleryDeparture>().FirstOrDefault(d => d != firstShot);
                return secondShot != null;
            }, 900);
            yield return waitSecond;
            t.Require(!waitSecond.TimedOut, "the second shot fires after the reload");
            t.Check(secondShot.artilleryProjectileDef == heProj,
                $"switching the shell switches the BVR round ({secondShot.artilleryProjectileDef})");
            art.StopSustainedFire();

            // The loaded shell type travels through the world track and lands on the target map.
            var waitTrack = new WaitUntil(
                () => Tracks.Any(tr => tr.destinationTile == site.Tile && tr.projectileDef == heProj), 600);
            yield return waitTrack;
            t.Check(!waitTrack.TimedOut, "the world track carries the switched shell");
            var waitLanding = new WaitUntil(() => map.listerThings.ThingsOfDef(heProj).Any(), 900);
            yield return waitLanding;
            t.Check(!waitLanding.TimedOut, "the switched shell lands on the target map");
            foreach (WorldObject_ArtilleryStrike tr in Tracks.ToList()) { Track(tr); }
            t.Screenshot("heavy_mortar");
        }
    }
}
