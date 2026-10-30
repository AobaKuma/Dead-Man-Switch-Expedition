using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 在資源齊全後於裝配目標處施工：改裝套用 pending 設定，組裝台產出一枚導彈。
    /// 進度依小人工作速度（組裝台再乘台速）累加，與原版帳單施工同算法；進度存在目標上，中斷可接續。
    /// </summary>
    public class JobDriver_AssembleMissile : JobDriver
    {
        private const TargetIndex MissileInd = TargetIndex.A;

        /// <summary>工作目標：地面上的導彈物品、含有待裝配導彈的儲存架，或有訂單的組裝台。</summary>
        private Thing Target => job.GetTarget(MissileInd).Thing;
        private IMissileAssemblyTarget Comp => Target != null ? MissileAssemblyUtility.GetAssemblyTarget(Target) : null;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(MissileInd), job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(MissileInd);
            AddFailCondition(() => Comp == null || !Comp.WantsWork || !Comp.ResourcesComplete);

            yield return Toils_Goto.GotoThing(MissileInd, PathEndMode.Touch);

            Toil work = ToilMaker.MakeToil("DoMissileAssemblyWork");
            work.defaultCompleteMode = ToilCompleteMode.Never;
            work.tickIntervalAction = delegate (int delta)
            {
                IMissileAssemblyTarget t = Comp;
                if (t == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                t.WorkDone += t.WorkSpeedFor(pawn) * delta;
                pawn.skills?.Learn(SkillDefOf.Crafting, 0.1f * delta);   // 與原版帳單施工相同的經驗率
                pawn.GainComfortFromCellIfPossible(delta, chairsOnly: true);
                if (t.WorkDone >= t.WorkAmount) { ReadyForNextToil(); }
            };
            work.WithProgressBar(MissileInd, () =>
            {
                IMissileAssemblyTarget t = Comp;
                return t == null ? 0f : Mathf.Clamp01(t.WorkDone / Mathf.Max(1f, t.WorkAmount));
            });
            work.FailOnDespawnedNullOrForbidden(MissileInd);
            work.FailOn(() => Comp == null || !Comp.CanFinishNow);
            work.activeSkill = () => SkillDefOf.Crafting;
            yield return work;

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
