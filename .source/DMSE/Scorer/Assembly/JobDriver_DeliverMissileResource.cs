using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>把一種資源搬到導彈並存入 CompMissileConfig.delivered。</summary>
    public class JobDriver_DeliverMissileResource : JobDriver
    {
        private const TargetIndex MissileInd = TargetIndex.A;
        private const TargetIndex ResourceInd = TargetIndex.B;

        /// <summary>工作目標：地面上的導彈物品，或含有待裝配導彈的儲存架。</summary>
        private Thing Target => job.GetTarget(MissileInd).Thing;
        private CompMissileConfig Comp => Target != null ? MissileAssemblyUtility.GetAssemblyComp(Target) : null;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.GetTarget(MissileInd), job, 1, -1, null, errorOnFailed)) { return false; }
            if (!pawn.Reserve(job.GetTarget(ResourceInd), job, 1, -1, null, errorOnFailed)) { return false; }
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(MissileInd);
            AddFailCondition(() => Comp == null || !Comp.NeedsAssembly);

            Toil reserve = Toils_Reserve.Reserve(ResourceInd);
            yield return reserve;
            yield return Toils_Goto.GotoThing(ResourceInd, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(ResourceInd)
                .FailOnSomeonePhysicallyInteracting(ResourceInd);
            yield return Toils_Haul.StartCarryThing(ResourceInd, false, true)
                .FailOnDestroyedNullOrForbidden(ResourceInd);
            yield return Toils_Haul.CheckForGetOpportunityDuplicate(reserve, ResourceInd, TargetIndex.None, true);
            yield return Toils_Goto.GotoThing(MissileInd, PathEndMode.Touch);
            yield return Toils_General.Wait(60)
                .FailOnDestroyedNullOrForbidden(MissileInd)
                .WithProgressBarToilDelay(MissileInd);

            Toil deposit = ToilMaker.MakeToil("DepositMissileResource");
            deposit.initAction = delegate
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                CompMissileConfig comp = Comp;
                if (carried != null && comp != null)
                {
                    comp.Deposit(carried.def, carried.stackCount);
                    carried.Destroy();
                }
            };
            deposit.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return deposit;
        }
    }
}
