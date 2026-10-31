using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
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
            t.Check(art.CanTargetTile(near, out _), "tile within 20 accepted");
            t.Check(!art.CanTargetTile(far, out string r2) && r2.Contains("20"), $"tile beyond 20 rejected ({r2})");
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
        /// Unguided shot at an NPC base: aim warmup turns the head, one shell is used, the departure flies off the
        /// map edge, the world track arrives, generates the base map, keeps it, and the shell lands near its centre.
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
            t.Require(art.CanTargetTile(site.Tile, out string why), $"base tile is a valid target ({why})");

            float bearing = ArtilleryStrikeUtility.WorldDirectionAngle(Home.Tile, site.Tile);
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
            t.Require(site.HasMap, "arrival generated the base map");
            Map map = site.Map;
            t.Check(GameComponent_MissileEngage.Comp.times.ContainsKey(map), "the generated map is kept until the shells land");

            Projectile shell = null;
            IntVec3 entry = IntVec3.Invalid;
            TickWatch spawnedShell = AcceptanceDriver.Watch(() =>
            {
                if (shell == null)
                {
                    shell = map.listerThings.ThingsOfDef(Shell).OfType<Projectile>().FirstOrDefault();
                    entry = shell?.Position ?? IntVec3.Invalid;
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
            t.Require(!waitArrive.TimedOut, "the track arrives and the base map exists");
            Map map = site.Map;
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
    }
}
