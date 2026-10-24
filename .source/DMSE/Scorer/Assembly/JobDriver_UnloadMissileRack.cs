using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 從飛彈平台（TargetA）取出一枚不符合儲存設定的導彈。
    ///
    /// 流程：前往平台 → 等候取出（30 tick）→ 取出放在平台旁 → 立即排入原版「搬至倉儲」工作。
    /// 找不到倉儲時導彈留在平台旁，之後由原版搬運處理。
    /// </summary>
    public class JobDriver_UnloadMissileRack : JobDriver
    {
        private const TargetIndex RackInd = TargetIndex.A;

        private IMissileRack Rack => MissileRackUtil.Of(job.GetTarget(RackInd).Thing);

        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(RackInd), job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(RackInd);
            // 途中設定改回允許、或導彈已被射出時放棄
            AddFailCondition(() => Rack == null || WorkGiver_UnloadMissileRack.FirstDisallowed(Rack) == null);

            yield return Toils_Goto.GotoThing(RackInd, PathEndMode.Touch);

            yield return Toils_General.Wait(30).WithProgressBarToilDelay(RackInd);

            Toil extractToil = ToilMaker.MakeToil("ExtractDisallowedMissile");
            extractToil.initAction = delegate
            {
                IMissileRack rack = Rack;
                Thing toTake = rack != null ? WorkGiver_UnloadMissileRack.FirstDisallowed(rack) : null;
                if (toTake == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                // 與裝填相同，容器對容器取出，不依賴平台周圍有空格
                int transferred = rack.GetDirectlyHeldThings().TryTransferToContainer(
                    toTake, pawn.carryTracker.innerContainer, toTake.stackCount, out Thing carried);
                if (transferred <= 0 || carried == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                carried.def.soundPickup.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));

                // 放下後交給原版搬運：有更好的倉儲就接著搬過去
                if (pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing dropped)
                    && dropped != null)
                {
                    Job haul = HaulAIUtility.HaulToStorageJob(pawn, dropped, false);
                    if (haul != null)
                    {
                        pawn.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
                    }
                }
            };
            extractToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return extractToil;
        }
    }
}
