using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Vacuum sealed room (workshop feedback 2026-11-02): the room map used to have no terrain, so mods reading
    /// topGrid in Map.FinalizeInit threw and every entry made another map; the filled entrance spawned ownerless.
    /// </summary>
    public static class Tests_VacuumRoom
    {
        private static ThingDef Entrance => ThingDef.Named("DMSE_VacuumRoom_Entrance");
        private static ThingDef Filled => ThingDef.Named("DMSE_VacuumRoom_Filled");

        public static IEnumerable<Wait> TerrainAndLandfill(TestContext t)
        {
            TerrainDef floor = DefDatabase<TerrainDef>.GetNamed("DMSE_VacuumRoomFloor");
            MapPortal portal = (MapPortal)SpawnBuilding(Entrance, 14, 14);
            IntVec3 pos = portal.Position;
            Map room = null;
            Thing legacy = null;
            try
            {
                yield return new WaitTicks(2);
                room = portal.GetOtherMap();
                t.Require(room != null, "the entrance generates its room map");
                t.Check(room.terrainGrid.topGrid.All(x => x == floor), "every room cell has the sealed base terrain");
                t.Check(room.terrainGrid.TerrainAt(room.Center) == floor && floor.affordances.Contains(TerrainAffordanceDefOf.Heavy),
                    "the base takes floors and heavy buildings");
                t.Check(portal.GetOtherMap() == room, "entering again reuses the same room");

                CompLandFillable comp = portal.TryGetComp<CompLandFillable>();
                Command_Action gizmo = comp.CompGetGizmosExtra().OfType<Command_Action>().First();
                t.Check(gizmo.defaultLabel == "DMSE.Landfill.Fill".Translate().ToString() && !gizmo.defaultDesc.StartsWith("DMSE."),
                    $"the landfill button is translated ({gizmo.defaultLabel})");
                comp.startLandfill = true;
                Pawn pawn = Home.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
                t.Require(pawn != null, "a colonist can fill the entrance");
                pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("DMSE_Landfill"), portal));
                var filledIn = new WaitUntil(() => portal.Destroyed, 3000);
                yield return filledIn;
                t.Require(!filledIn.TimedOut, "the colonist fills the entrance");
                Thing filled = Home.thingGrid.ThingAt(pos, Filled);
                t.Require(filled != null, "a filled entrance replaces it");
                t.Check(!Find.Maps.Contains(room), "filling removes the room map");
                t.Check(filled.Faction == Faction.OfPlayer, "the filled entrance keeps the player's faction");
                t.Check(((Building)filled).DeconstructibleBy(Faction.OfPlayer).Accepted, "the player can deconstruct it");
                filled.Destroy(DestroyMode.Vanish);

                // Saves from before the fix hold ownerless filled entrances.
                legacy = GenSpawn.Spawn(Filled, pos, Home);
                t.Check(legacy.Faction == null && ((Building)legacy).DeconstructibleBy(Faction.OfPlayer).Accepted,
                    "an ownerless filled entrance from an old save can be deconstructed too");
            }
            finally
            {
                if (room != null && Find.Maps.Contains(room)) { PocketMapUtility.DestroyPocketMap(room); }
                if (legacy != null && !legacy.Destroyed) { legacy.Destroy(DestroyMode.Vanish); }
                Thing left = Home.thingGrid.ThingAt(pos, Filled);
                if (left != null && !left.Destroyed) { left.Destroy(DestroyMode.Vanish); }
            }
        }
    }
}
