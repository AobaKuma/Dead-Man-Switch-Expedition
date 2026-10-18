using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class Alert_WorldIncoming : Alert
    {
        private int jumpIndex = -1;
        private static List<MissileWorldObject> Targets => WorldComponent_BVRNetwork.Current?.PlayerIncoming.ToList()
            ?? new List<MissileWorldObject>();
        public override AlertReport GetReport() => Targets.Count > 0 ? AlertReport.Active : AlertReport.Inactive;
        public override string GetLabel()
        {
            var targets = Targets;
            return targets.Count == 0 ? "DMSE.BVR.World.IncomingAlert".Translate()
                : "DMSE.BVR.World.IncomingLabel".Translate(targets.Count, targets[0].TicksToArrival.ToStringTicksToPeriod());
        }
        public override TaggedString GetExplanation() => string.Join("\n\n", Targets.Select(t => t.Label + "\n" + t.GetInspectString()))
            + "\n\n" + "DMSE.BVR.World.ClickHint".Translate();
        protected override void OnClick()
        {
            var targets = Targets;
            if (targets.Count == 0) return;
            jumpIndex = GenMath.PositiveMod(jumpIndex + (Event.current.button == 1 ? -1 : 1), targets.Count);
            CameraJumper.TryJump(new GlobalTargetInfo(targets[jumpIndex]));
        }
    }

    /// <summary>Available on all map parents, including existing saves; no core XML patch required.</summary>
    [HarmonyLib.HarmonyPatch(typeof(MapParent), nameof(MapParent.GetGizmos))]
    public static class Patch_MapParent_BVRCoverage
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, MapParent __instance)
        {
            foreach (var gizmo in __result) yield return gizmo;
            if (__instance.Faction != Faction.OfPlayer || WorldComponent_BVRNetwork.Current == null) yield break;
            var network = WorldComponent_BVRNetwork.Current;
            yield return new Command_Toggle
            {
                defaultLabel = "DMSE.BVR.World.ShowCoverage".Translate(),
                defaultDesc = "DMSE.BVR.World.ShowCoverageDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/DMSE/BVR/Coverage"),
                isActive = () => network.ShowAllCoverage,
                toggleAction = () => network.ShowAllCoverage = !network.ShowAllCoverage
            };
        }
    }
}
