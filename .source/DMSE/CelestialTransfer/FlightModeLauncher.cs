using System;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 負責從控制台發起各種飛行模式（標準/Transfer/Impact）。
    /// </summary>
    public static class FlightModeLauncher
    {
        public static bool CanUse(CompPilotConsole console, bool launching)
        {
            string error;
            return !FlightUtility.GetFailReason(console, out error);
        }

        public static void ChooseAndStart(CompPilotConsole console, Action<CompPilotConsole> onStandardChosen)
        {
            Find.WindowStack.Add(
                new Dialog_SelectFlightMode(
                    mode =>
                    {
                        if (mode == FlightMode.Standard)
                        {
                            onStandardChosen?.Invoke(console);
                            return;
                        }

                        StartTransferOrImpact(console, mode);
                    },
                    console));
        }

        public static void StartTransferOrImpact(CompPilotConsole comp, FlightMode mode)
        {
            // 共用前置檢查
            if (!FlightUtility.ValidateTransferPreconditions(comp))
            {
                return;
            }

            bool isImpact = mode == FlightMode.Impact;

            CameraJumper.TryJump(CameraJumper.GetWorldTarget(comp.parent), CameraJumper.MovementMode.Pan);
            Find.WorldSelector.ClearSelection();

            PlanetTile curTile = comp.parent.Map.Tile;
            PlanetLayer curLayer = curTile.Layer;
            float totalFuel = comp.engine.TotalFuel;
            float fuelUseageFactor = comp.engine.FuelUseageFactor;

            float radius = GravshipUtility.MaxDistForFuel(
                totalFuel,
                curLayer, curLayer,
                FlightUtility.FuelConsumePerTile,
                fuelUseageFactor);

            Find.TilePicker.StartTargeting_NewTemp(
                // 目標判定
                t =>
                {
                    return FlightUtility.ValidateDestinationTile(t, curTile, radius, mode);
                },
                // 決定後行為
                t =>
                {
                    SettlementProximityGoodwillUtility.CheckConfirmSettle(
                        t,
                        () => DoLaunch(comp, t, curTile, isImpact, mode),
                        () => StartTransferOrImpact(comp, mode),
                        comp.engine);
                },
                null,
                // 半徑顯示
                () =>
                {
                    GenDraw.DrawWorldRadiusRing(curTile, (int)radius, CompPilotConsole.GetThrusterRadiusMat(curTile));
                },
                true,
                () => CameraJumper.TryJump(comp.parent.Position, comp.parent.Map),
                null,
                false, true, true, true);
        }

        private static void DoLaunch(
            CompPilotConsole comp,
            PlanetTile target,
            PlanetTile curTile,
            bool isImpact,
            FlightMode mode)
        {
            Find.World.renderer.wantedMode = WorldRenderMode.None;
            FlightUtility.ConsumeFuel(comp.engine, target);
            SoundDefOf.Gravship_Launch.PlayOneShotOnCamera(null);

            if (isImpact)
            {
                var wo = (WorldObject_ImpactGravship)WorldObjectMaker.MakeWorldObject(DMSE_DefOf.DMSE_ImpactGravship);
                wo.Tile = curTile;
                Find.World.worldObjects.Add(wo);
                InitShipMapComponent(comp, wo);
            }
            else
            {
                var wo = (WorldObject_Transfer)WorldObjectMaker.MakeWorldObject(DMSE_DefOf.DMSE_TransferGravShip);
                wo.Tile = curTile;
                Find.World.worldObjects.Add(wo);
                InitShipMapComponent(comp, wo);
            }
        }

        private static void InitShipMapComponent(CompPilotConsole comp, ITravelingShip wo)
        {
            MapComponent_Ship mc = comp.parent.Map.GetComponent<MapComponent_Ship>();
            mc.status = OrbitalTransferState.Working;
            var core = comp.engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading.Find(thing =>
            {
                var fac = thing.TryGetComp<CompGravshipFacility>();
                return fac != null && fac.Props.componentTypeDef == DMSE_DefOf.DMSE_FusionCore;
            });
            var facilities = comp.engine.GetComp<CompAffectedByFacilities>()
                .LinkedFacilitiesListForReading
                .FindAll(thing =>
                {
                    var fac = thing.TryGetComp<CompGravshipFacility>();
                    return fac != null && (fac.Props.componentTypeDef == DMSE_DefOf.DMSE_TransferThruster);
                });

            mc.StartWarmUp(core,facilities, wo);
        }
    }
}