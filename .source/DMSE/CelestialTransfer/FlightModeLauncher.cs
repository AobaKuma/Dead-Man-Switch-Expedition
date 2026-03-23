using System;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// �t�d�q����x�o�_�U�ح���Ҧ��]�з�/Transfer/Impact�^�C
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
            // �@�Ϋe�m�ˬd
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
                // �ؼЧP�w
                t =>
                {
                    return FlightUtility.ValidateDestinationTile(t, curTile, radius, mode);
                },
                // �M�w��欰
                t =>
                {
                    SettlementProximityGoodwillUtility.CheckConfirmSettle(
                        t,
                        () => DoLaunch(comp, t, curTile, mode),
                        () => StartTransferOrImpact(comp, mode),
                        comp.engine);
                },
                null,
                // �b�|���
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
            FlightMode mode)
        {
            Find.World.renderer.wantedMode = WorldRenderMode.None;
            FlightUtility.ConsumeFuel(comp.engine, target);
            SoundDefOf.Gravship_Launch.PlayOneShotOnCamera(null);

            if (mode == FlightMode.Impact)
            {
                var wo = (WorldObject_ImpactGravship)WorldObjectMaker.MakeWorldObject(DMSE_DefOf.DMSE_ImpactGravship);
                wo.Setup(curTile, target);
                wo.worldObject = comp.parent.Map.Parent;
                Find.World.worldObjects.Add(wo);
                InitShipMapComponent(comp, wo);
            }
            else
            {
                var wo = (WorldObject_Transfer)WorldObjectMaker.MakeWorldObject(DMSE_DefOf.DMSE_TransferGravShip);
                wo.Setup(curTile, target);
                wo.worldObject = comp.parent.Map.Parent;
                Find.World.worldObjects.Add(wo);
                InitShipMapComponent(comp, wo);
            }
        }

        private static void InitShipMapComponent(CompPilotConsole comp, TravelingObject wo)
        {
            MapComponent_Ship mc = comp.parent.Map.GetComponent<MapComponent_Ship>();
            mc.status = OrbitalTransferState.WarmUp;
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