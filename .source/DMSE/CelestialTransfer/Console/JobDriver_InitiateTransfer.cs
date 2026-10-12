using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 在控制台互動格執行點火程序（operateTicks，帶進度條）；完成後開始倒數。點火期間不需駐守。
    /// </summary>
    public class JobDriver_InitiateTransfer : JobDriver
    {
        private const TargetIndex ConsoleInd = TargetIndex.A;

        private CompTransferConsole Console => job.GetTarget(ConsoleInd).Thing?.TryGetComp<CompTransferConsole>();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(ConsoleInd), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(ConsoleInd);
            this.FailOn(() => Console == null || Console.State != TransferConsoleState.AwaitingOperator || !Console.IsAvailable);

            yield return Toils_Goto.GotoThing(ConsoleInd, PathEndMode.InteractionCell);

            int ticks = Console != null ? Console.Props.operateTicks : 1250;
            Toil operate = Toils_General.Wait(ticks, ConsoleInd);
            operate.WithProgressBarToilDelay(ConsoleInd);
            operate.tickIntervalAction = delegate (int delta)
            {
                pawn.skills?.Learn(SkillDefOf.Intellectual, 0.05f * delta);
            };
            operate.activeSkill = () => SkillDefOf.Intellectual;
            yield return operate;

            Toil finish = ToilMaker.MakeToil("FinishLaunchProcedure");
            finish.initAction = delegate
            {
                Console?.BeginCountdown();
            };
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finish;
        }
    }
}
