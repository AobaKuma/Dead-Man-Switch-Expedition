using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 在施工端結構樁旁架設或拆除鋼樑。每 tick 工作量與經驗比照原版 JobDriver_ConstructFinishFrame
    /// （ConstructionSpeed × 1.7、Construction 技能經驗）；施工單本身存在樁上，讀檔後工作可延續。
    /// </summary>
    public class JobDriver_BuildPileBeam : JobDriver
    {
        private const TargetIndex PileInd = TargetIndex.A;

        /// <summary>與原版相同：一段時間後結束 job，讓小人重新評估工作。</summary>
        private const int JobEndInterval = 5000;

        private Thing Pile => job.GetTarget(PileInd).Thing;

        private CompStructuralPile Comp => Pile?.TryGetComp<CompStructuralPile>();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(PileInd), job, 1, -1, null, errorOnFailed);
        }

        public override string GetReport()
        {
            PileBeamOrder order = Comp?.CurrentOrder;
            if (order != null && order.dismantle)
            {
                return "DMSE.Pile.Report.Dismantle".Translate(Pile.LabelShort);
            }
            return base.GetReport();
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(PileInd);
            this.FailOn(() => Comp == null || !Comp.CurrentOrderReady);

            yield return Toils_Goto.GotoThing(PileInd, PathEndMode.Touch);

            Toil work = ToilMaker.MakeToil("BuildPileBeam");
            work.initAction = delegate
            {
                GenClamor.DoClamor(work.actor, 15f, ClamorDefOf.Construction);
            };
            work.tickIntervalAction = delegate (int delta)
            {
                CompStructuralPile comp = Comp;
                PileBeamOrder order = comp?.CurrentOrder;
                if (order == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                Pawn actor = work.actor;
                actor.skills?.Learn(SkillDefOf.Construction, 0.25f * delta);
                actor.rotationTracker.FaceTarget(Pile);
                order.workDone += actor.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f * delta;
                if (order.workDone >= comp.WorkOf(order))
                {
                    comp.CompleteOrder(order, actor);
                    ReadyForNextToil();
                }
            };
            work.WithProgressBar(PileInd, delegate
            {
                CompStructuralPile comp = Comp;
                PileBeamOrder order = comp?.CurrentOrder;
                return order == null ? 0f : order.workDone / comp.WorkOf(order);
            });
            work.WithEffect(EffecterDefOf.ConstructMetal, PileInd);
            work.defaultCompleteMode = ToilCompleteMode.Delay;
            work.defaultDuration = JobEndInterval;
            work.activeSkill = () => SkillDefOf.Construction;
            work.handlingFacing = true;
            yield return work;
        }
    }
}
