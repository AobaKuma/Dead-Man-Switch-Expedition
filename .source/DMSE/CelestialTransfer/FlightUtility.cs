using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using Verse;
using Verse.Sound;

namespace DMSE
{
    public static class FlightUtility
    {
        public const float FuelConsumePerTile = 100f;

        public static bool AnyOfRelatedFacilities(Building_GravEngine engine)
        {
            return GetTransferThrusterCount(engine) > 0 || GetFusionCores(engine).Count > 0;
        }

        public static int GetTransferThrusterCount(Building_GravEngine engine)
        {
            return engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading
                .FindAll(a => a.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp
                    && comp.Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster)
                .Count;
        }

        public static List<Thing> GetFusionCores(Building_GravEngine engine)
        {
            return engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading
                .FindAll(thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp
                    && comp.Props.componentTypeDef == DMSE_DefOf.DMSE_FusionCore);
        }

        public static bool ValidateTransferPreconditions(CompPilotConsole comp)
        {
            if (GetFailReason(comp, out string reason))
            {
                ShowErrorDialog(reason);
                return false;
            }
            return true;
        }

        public static bool ValidateDestinationTile(PlanetTile targetTile, PlanetTile currentTile, float radius, FlightMode mode)
        {
            if (!GravshipUtility.TryGetPathFuelCost(currentTile, targetTile,
                out float cost, out int distance, 10f, 1f) && !DebugSettings.ignoreGravshipRange)
            {
                ShowErrorDialog("CannotLaunchDestination".Translate());
                return false;
            }

            if (mode != FlightMode.Impact && !Find.World.worldObjects.ObjectsAt(targetTile).EnumerableNullOrEmpty())
            {
                ShowErrorDialog("DMSE.Disabled.DestinationOccupied".Translate());
                return false;
            }

            if (mode != FlightMode.Impact && targetTile.Layer == Find.WorldGrid.FirstLayerOfDef(PlanetLayerDefOf.Surface))
            {
                ShowErrorDialog("DMSE.Disabled.TargetedSurface".Translate());
                return false;
            }

            if (distance > radius && !DebugSettings.ignoreGravshipRange)
            {
                ShowErrorDialog("TransportPodDestinationBeyondMaximumRange".Translate());
                return false;
            }

            return true;
        }

        public static bool CanTransfer(CompPilotConsole comp)
        {
            if (comp.engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading
                .Find(thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0
                    && comp0.Props.componentTypeDef == DMSE_DefOf.DMSE_FusionCore) == null)
                return false;

            if (comp.engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading
                .Find(thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0
                    && comp0.Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster) == null)
                return false;

            if (comp.parent.Map.Tile.Layer != PlanetLayer.Selected)
                return false;

            if (comp.engine.TotalFuel <= 0f)
                return false;

            if (!comp.parent.Map.Parent.Tile.LayerDef.isSpace)
                return false;

            return true;
        }

        private static void ShowErrorDialog(string message)
        {
            Find.WindowStack.Add(new Dialog_MessageBox(message));
        }

        public static void ConsumeFuel(Building_GravEngine engine, PlanetTile tile)
        {
            if (!GravshipUtility.TryGetPathFuelCost(engine.Map.Tile, tile, out float num, out int num2,
                FuelConsumePerTile, 1f))
            {
                Log.Error(string.Format("Failed to get the fuel cost from tile ({0}) to {1}.", engine.Map.Tile, tile));
                return;
            }
            float ratio = num / engine.TotalFuel;
            foreach (CompGravshipFacility compGravshipFacility in engine.GravshipComponents)
            {
                if (compGravshipFacility.CanBeActive && compGravshipFacility.Props.providesFuel)
                {
                    CompRefuelable comp = compGravshipFacility.parent.GetComp<CompRefuelable>();
                    if (comp != null)
                    {
                        comp.ConsumeFuel(comp.Fuel * ratio);
                    }
                }
            }
            int ticksGame = GenTicks.TicksGame;
            LaunchInfo launchInfo = engine.launchInfo;
            engine.cooldownCompleteTick = ticksGame + (int)GravshipUtility.LaunchCooldownFromQuality(
                launchInfo != null ? launchInfo.quality : 1f);
        }

        public static bool GetFailReason(CompPilotConsole comp, out string reason)
        {
            reason = null;

            if (comp.engine == null || comp.parent == null || !comp.parent.Spawned || comp.parent.Map == null)
            {
                reason = "DMSE.Cannot.Reason.Null";
                return true;
            }

            if (!comp.parent.Map.Parent.Tile.LayerDef.isSpace)
            {
                reason = "DMSE.Cannot.Reason.NotInSpace";
                return true;
            }

            int transferThrusterCount = GetTransferThrusterCount(comp.engine);
            if (transferThrusterCount < 2)
            {
                reason = "DMSE.Cannot.Reason.TransferThruster".Translate(transferThrusterCount, 2);
                return true;
            }

            List<Thing> FusionCores = GetFusionCores(comp.engine);
            if (FusionCores.Count == 0)
            {
                reason = "DMSE.Cannot.Reason.NoFusionCore".Translate();
                return true;
            }

            if (comp.engine.TotalFuel <= 0f)
            {
                reason = "DMSE.Cannot.Reason.NoFuel".Translate();
                return true;
            }

            return false;
        }
    }
}
