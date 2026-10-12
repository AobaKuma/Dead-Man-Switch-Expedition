using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 把鋼樑材料搬進施工端結構樁（設計稿 §3-2「送料」）。直接沿用原版 HaulToContainer：
    /// 它經由 <see cref="ThingOwnerUtility.TryGetInnerInteractableThingOwner"/> 找到 <see cref="CompStructuralPile"/> 的容器。
    /// </summary>
    public class WorkGiver_DeliverPileBeamMaterials : WorkGiver_Scanner
    {
        private static readonly Dictionary<ThingDef, int> missing = new Dictionary<ThingDef, int>();

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override Danger MaxPathDanger(Pawn pawn)
        {
            return Danger.Deadly;
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(pawn.Map);
            return comp != null ? comp.PilesNeedingMaterials() : Enumerable.Empty<Thing>();
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(pawn.Map);
            return comp == null || !comp.AnyPileNeedsMaterials();
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompStructuralPile pile = t.TryGetComp<CompStructuralPile>();
            if (pile == null || t.Faction != pawn.Faction || !pile.NeedsMaterials)
            {
                return null;
            }
            if (t.IsForbidden(pawn) || t.IsBurning() || !pawn.CanReserve(t, 1, 1, null, forced))
            {
                return null;
            }

            missing.Clear();
            List<ThingDefCountClass> cost = pile.CostOf(pile.CurrentOrder);
            for (int i = 0; i < cost.Count; i++)
            {
                ThingDefCountClass need = cost[i];
                int count = pile.MaterialMissing(need);
                if (count <= 0)
                {
                    continue;
                }
                if (!pawn.Map.itemAvailability.ThingsAvailableAnywhere(need.thingDef, count, pawn))
                {
                    missing[need.thingDef] = count;
                    continue;
                }
                Thing res = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(need.thingDef), PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn, forced ? Danger.Deadly : pawn.NormalMaxDanger()), 9999f,
                    r => !r.IsForbidden(pawn) && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, r, forced));
                if (res == null)
                {
                    missing[need.thingDef] = count;
                    continue;
                }
                Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer, res, t);
                job.count = Mathf.Min(count, pawn.carryTracker.MaxStackSpaceEver(need.thingDef));
                job.haulMode = HaulMode.ToContainer;
                return job;
            }

            if (missing.Count > 0)
            {
                JobFailReason.Is("MissingMaterials".Translate(missing.Select(kv => kv.Value + "x " + kv.Key.label).ToCommaList()));
            }
            return null;
        }
    }
}
