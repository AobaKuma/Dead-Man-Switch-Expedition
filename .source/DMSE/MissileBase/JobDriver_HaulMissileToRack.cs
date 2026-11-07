using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 把地上（空投落地）的導彈搬進導彈平台，由守軍自己動手。<br/>
    /// TargetA = 目標平台（<see cref="IMissileRack"/>：彈藥庫、發射架或 comp 型平台）；
    /// TargetB = 地上的導彈。<br/>
    /// <br/>
    /// 與 <see cref="JobDriver_LoadMissileLauncher"/> 的分工：那份工作搬的是「彈架容器內」的 unspawned 導彈；
    /// 這份工作的來源是地圖上的實體導彈——補給艙開艙後導彈就躺在地上，守軍必須走過去撿、再搬到平台上，
    /// 玩家隨時可以搶走或炸掉。搬運途中導彈一直在 pawn 手上（<see cref="Pawn_CarryTracker"/>），不會瞬間移動。
    /// </summary>
    public class JobDriver_HaulMissileToRack : JobDriver
    {
        private const TargetIndex RackInd = TargetIndex.A;
        private const TargetIndex MissileInd = TargetIndex.B;

        private IMissileRack Rack => MissileRackUtil.Of(job.GetTarget(RackInd).Thing);

        private Thing Missile => job.GetTarget(MissileInd).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.GetTarget(RackInd), job, 1, -1, null, errorOnFailed)) { return false; }
            if (!pawn.Reserve(job.GetTarget(MissileInd), job, 1, -1, null, errorOnFailed)) { return false; }
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(RackInd);
            this.FailOnDestroyedOrNull(MissileInd);
            // 平台在途中被別人裝滿／儲存設定改了：不再搬過去（手上還沒東西）。
            AddFailCondition(() =>
            {
                IMissileRack rack = Rack;
                Thing missile = Missile;
                return rack == null || missile == null || rack.Full || !rack.Accepts(missile);
            });

            // ── 步驟 1：走到導彈旁撿起來 ──
            yield return Toils_Goto.GotoThing(MissileInd, PathEndMode.ClosestTouch)
                .FailOnDestroyedNullOrForbidden(MissileInd);
            yield return Toils_Haul.StartCarryThing(MissileInd, putRemainderInQueue: false,
                subtractNumTakenFromJobCount: false, failIfStackCountLessThanJobCount: false);

            // ── 步驟 2：搬著走到平台 ──
            yield return Toils_Goto.GotoThing(RackInd, PathEndMode.Touch)
                .FailOnDespawnedNullOrForbidden(RackInd);

            // ── 步驟 3：在平台旁裝填（讀條，與殖民者的裝填工作一致） ──
            yield return Toils_General.Wait(30, RackInd)
                .WithProgressBarToilDelay(RackInd)
                .FailOnCannotTouch(RackInd, PathEndMode.Touch);

            // ── 步驟 4：導彈進平台容器 ──
            Toil deposit = ToilMaker.MakeToil("DepositMissileInRack");
            deposit.initAction = delegate
            {
                Pawn actor = deposit.actor;
                Thing carried = actor.carryTracker.CarriedThing;
                IMissileRack rack = Rack;
                if (carried == null) { return; }

                // 走到時平台已被別人裝滿（或改成不收這種彈）：導彈放回地上，不讓它消失。
                if (rack == null || rack.Full || !rack.Accepts(carried))
                {
                    actor.carryTracker.TryDropCarriedThing(actor.Position, ThingPlaceMode.Near, out _);
                    return;
                }

                int space = MissileRackUtil.LoadCount(carried, rack);
                int added = actor.carryTracker.innerContainer.TryTransferToContainer(
                    carried, rack.GetDirectlyHeldThings(), space);
                if (added <= 0)
                    actor.carryTracker.TryDropCarriedThing(rack.RackThing.InteractionCell, ThingPlaceMode.Near, out _);
            };
            deposit.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return deposit;
        }
    }
}
