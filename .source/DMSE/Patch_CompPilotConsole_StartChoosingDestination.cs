using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using Verse;
using Verse.Sound;

namespace DMSE
{
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class Patch_CompPilotConsole_StartChoosingDestination
    {
        [ThreadStatic]
        private static bool Run = false;

        [HarmonyPrefix]
        public static bool Prefix(CompPilotConsole __instance, bool launching)
        {
            if (!launching) return true;
            if (__instance == null || __instance.engine == null) return true;

            if (!TransferFlightUtility.AnyOfRelatedFacilities(__instance.engine))
            {
                return true;
            }

            if (!Run)
            {
                Find.WindowStack.Add(new Dialog_SelectFlightMode(mode =>
                {
                    if (mode == FlightMode.Standard)
                    {
                        Run = true;
                        __instance.StartChoosingDestination();
                    }
                    else if (mode == FlightMode.Transfer)
                    {
                        Start(__instance);
                    }
                }, __instance));
            }
            bool result = Run;
            if (Run)
            {
                Run = false;
            }
            return result;
        }

        public static void Start(CompPilotConsole comp)
        {
            if (!TransferFlightUtility.ValidateTransferPreconditions(comp))
            {
                return;
            }

            int count = TransferFlightUtility.GetTransferThrusterCount(comp.engine);

            CameraJumper.TryJump(CameraJumper.GetWorldTarget(comp.parent), CameraJumper.MovementMode.Pan);
            Find.WorldSelector.ClearSelection();
            PlanetTile curTile = comp.parent.Map.Tile;
            PlanetLayer curLayer = comp.parent.Map.Tile.Layer;
            float totalFuel = comp.engine.TotalFuel;
            float fuelUseageFactor = comp.engine.FuelUseageFactor;
            float radius = GravshipUtility.MaxDistForFuel(totalFuel,
                curLayer, curLayer, TransferFlightUtility.FuelConsumePerTile,
                fuelUseageFactor);

            Find.TilePicker.StartTargeting_NewTemp(t =>
            {
                return TransferFlightUtility.ValidateDestinationTile(t, curTile, radius);
            }
            , (t) =>
            {
                SettlementProximityGoodwillUtility.CheckConfirmSettle(t, delegate
                {
                    Find.World.renderer.wantedMode = WorldRenderMode.None;
                    TransferFlightUtility.ConsumeFuel(comp.engine, t);
                    SoundDefOf.Gravship_Launch.PlayOneShotOnCamera(null);
                    WorldObject_Transfer wo = (WorldObject_Transfer)WorldObjectMaker.
                        MakeWorldObject(DMSE_DefOf.Ship);
                    wo.Tile = curTile;
                    wo.start = curTile;
                    wo.end = t;
                    wo.worldObject = comp.parent.Map.Parent;
                    Find.World.worldObjects.Add(wo);
                    MapComponent_Ship mc = comp.parent.Map.GetComponent<MapComponent_Ship>();
                    mc.status = OrbitalTransferState.Working;
                    mc.Init(comp.engine.GetComp<CompAffectedByFacilities>()
                        .LinkedFacilitiesListForReading.FindAll(
                            thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0
                                && comp0.Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster), wo);
                }, () => Start(comp), comp.engine);
            }
            , null
            , delegate
            {
                GenDraw.DrawWorldRadiusRing(curTile, (int)radius, CompPilotConsole.GetThrusterRadiusMat(curTile));
            }, true, () => CameraJumper.TryJump(comp.parent.Position, comp.parent.Map), null, false, true, true, true);
        }
    }
}