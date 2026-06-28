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

        /// <summary>工作目標：可能是地面上的導彈物品，或含有待裝配導彈的儲存架。</summary>
        private Thing Target => job.GetTarget(MissileInd).Thing;
        private CompMissileConfig Comp => Target != null ? MissileAssemblyUtility.GetAssemblyComp(Target) : null;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(MissileInd), job, 1, -1, null, errorOnFailed);

        // 舊屬性名稱保持相容（JobDriver 內部 toil 引用）
        private Thing Missile => Target;

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
