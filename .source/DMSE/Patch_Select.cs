using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Tilemaps;
using Verse;
using Verse.Sound;
using static Unity.Burst.Intrinsics.X86.Avx;
using static UnityEngine.Networking.UnityWebRequest;

namespace DMSE
{
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination))]
    public class Patch_Select
    {
        [HarmonyPrefix]
        public static bool prefix(CompPilotConsole __instance)
        {
            int count = __instance.engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading.FindAll(a => a.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0 && comp0.parent.def == PRDefOf.DMSE_NuclearThruster).Count;
            if (count < 2)
            {
                return true;
            }
            if (!Run)
            {
                Find.WindowStack.Add(new Dialog_MessageBox("SelectAction".Translate(), "正常飞行", () =>
                {
                    Run = true;
                    __instance.StartChoosingDestination();
                }, "转移飞行", () => Start(__instance)));
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
            if (comp.engine == null || comp.parent == null ||
                !comp.parent.Spawned || comp.parent.Map == null)
            {
                Log.Error("无法发射");
                return;
            }

            if (!comp.parent.Map.Parent.Tile.LayerDef.isSpace)
            {
                Log.Error("不在太空中");
                return;
            }
            int count = comp.engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading
                .FindAll(a =>
            a.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0
            && comp0.parent.def == PRDefOf.DMSE_NuclearThruster).Count;
            if (count < 2)
            {
                Log.Error("建筑不够");
                return;
            }
            //这里Defname需要改
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(comp.parent), CameraJumper.MovementMode.Pan);
            Find.WorldSelector.ClearSelection();
            PlanetTile curTile = comp.parent.Map.Tile;
            PlanetLayer curLayer = comp.parent.Map.Tile.Layer;
            PlanetTile cachedClosestLayerTile = PlanetTile.Invalid;
            float radius = count * GravshipUtility.MaxDistForFuel(comp.engine.TotalFuel,
                curLayer, PlanetLayer.Selected, 10f, comp.engine.FuelUseageFactor) / 4f;
            Find.TilePicker.StartTargeting(t =>
            {
                if (!GravshipUtility.TryGetPathFuelCost(curTile, t,
                    out float cost, out int distance, 10f, comp.engine.FuelUseageFactor) && !DebugSettings.ignoreGravshipRange)
                {
                    Messages.Message("CannotLaunchDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                if (!Find.World.worldObjects.ObjectsAt(t).EnumerableNullOrEmpty())
                {
                    Messages.Message("CannotLaunchDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                if (t.Layer != curTile.Layer)
                {
                    Messages.Message("CannotLaunchDestination".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                if (distance > radius && !DebugSettings.ignoreGravshipRange)
                {
                    Messages.Message("TransportPodDestinationBeyondMaximumRange".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                return true;
            }
            , (t) =>
            {
                SettlementProximityGoodwillUtility.CheckConfirmSettle(t, delegate
                {
                    Find.World.renderer.wantedMode = WorldRenderMode.None;
                    comp.engine.ConsumeFuel(t);
                    SoundDefOf.Gravship_Launch.PlayOneShotOnCamera(null);
                    WorldObject_Transfer wo = (WorldObject_Transfer)WorldObjectMaker.
                    MakeWorldObject(PRDefOf.Ship);
                    wo.Tile = curTile;
                    wo.start = curTile;
                    wo.end = t;
                    wo.worldObjec = comp.parent.Map.Parent;
                    Find.World.worldObjects.Add(wo);
                    MapComponent_Ship mc = comp.parent.Map.GetComponent<MapComponent_Ship>();
                    mc.status = OrbitalTransferState.Working;//到時候要先改成WarmUp
                    mc.Init(comp.engine.GetComp<CompAffectedByFacilities>()
                        .LinkedFacilitiesListForReading.FindAll(
                        thing => thing.TryGetComp<CompGravshipFacility>() is CompGravshipFacility comp0
            && comp0.Props.componentTypeDef == PRDefOf.Thruster), wo);
                }, () => Start(comp), comp.engine);
            }
            , null, delegate
            {
                GenDraw.DrawWorldRadiusRing(curTile, (int)radius, CompPilotConsole.GetThrusterRadiusMat(curTile));
            }, true, () => CameraJumper.TryJump(comp.parent.Position, comp.parent.Map), null, false, true, true, true, null);
        }

        public static bool Run = false;
    }
}