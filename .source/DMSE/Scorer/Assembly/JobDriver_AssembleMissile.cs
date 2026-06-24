using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>在資源齊全後，於導彈處施工並套用 pending 設定（ApplyAssembly）。</summary>
    public class JobDriver_AssembleMissile : JobDriver
    {
        private const TargetIndex MissileInd = TargetIndex.A;

        private Thing Missile => job.GetTarget(MissileInd).Thing;
        private CompMissileConfig Comp => Missile != null ? Missile.TryGetComp<CompMissileConfig>() : null;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(MissileInd), job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(MissileInd);
            AddFailCondition(() => Comp == null || !Comp.NeedsAssembly || !Comp.ResourcesComplete);

            yield return Toils_Goto.GotoThing(MissileInd, PathEndMode.Touch);

            int work = 600;
            CompMissileConfig comp = Comp;
            if (comp != null && comp.pending != null && comp.pending.body != null)
            {
                work = comp.pending.body.assembleWorkAmount;
            }

            yield return Toils_General.Wait(work)
                .FailOnDespawnedNullOrForbidden(MissileInd)
                .WithProgressBarToilDelay(MissileInd);

            Toil finalize = ToilMaker.MakeToil("ApplyMissileAssembly");
            finalize.initAction = delegate
            {
                Comp?.ApplyAssembly();
            };
            finalize.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finalize;
        }
    }
}
