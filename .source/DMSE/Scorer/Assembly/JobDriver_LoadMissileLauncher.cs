using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 從導彈儲存架（TargetB）取出一枚相容導彈，搬至發射平台（TargetA）並裝填。
    ///
    /// TargetA = 目標發射平台（<see cref="Building_MissileRack"/> 含發射 comp，且未滿）。
    /// TargetB = 來源儲存架（<see cref="Building_MissileRack"/> 純儲存，有相容導彈）。
    ///
    /// 流程：
    ///   前往儲存架 → 等候取出（30 tick）→ 從容器取出導彈持於手上
    ///   → 前往發射平台 → 裝填入發射平台容器。
    /// </summary>
    public class JobDriver_LoadMissileLauncher : JobDriver
    {
        private const TargetIndex LauncherInd = TargetIndex.A;
        private const TargetIndex SourceRackInd = TargetIndex.B;

        /// <summary>
        /// 導彈已從儲存架取出的旗標。
        /// 取出後 SourceRack.StoredCount 可能降為 0，需防止「空架」檢查誤判中斷工作。
        /// </summary>
        private bool missileExtracted;

        private Building_MissileRack Launcher => job.GetTarget(LauncherInd).Thing as Building_MissileRack;
        private Building_MissileRack SourceRack => job.GetTarget(SourceRackInd).Thing as Building_MissileRack;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.GetTarget(LauncherInd), job, 1, -1, null, errorOnFailed)) { return false; }
            if (!pawn.Reserve(job.GetTarget(SourceRackInd), job, 1, -1, null, errorOnFailed)) { return false; }
            return true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref missileExtracted, "missileExtracted", false);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(LauncherInd);
            // 儲存架消失時中斷（但已拿起導彈後就無所謂了）
            this.FailOnDespawnedNullOrForbidden(SourceRackInd);
            // 若發射平台在途中已滿（另一殖民者搶先裝填），放棄
            AddFailCondition(() => Launcher != null && Launcher.Full);
            // 若儲存架途中被他人清空（且我們尚未取出），放棄
            // 注意：取出後 StoredCount 可能降為 0，故以 missileExtracted 旗標保護
            AddFailCondition(() => !missileExtracted && SourceRack != null && SourceRack.StoredCount == 0);

            // ── 步驟 1：前往儲存架 ──
            yield return Toils_Goto.GotoThing(SourceRackInd, PathEndMode.Touch)
                .FailOnDespawnedNullOrForbidden(SourceRackInd);

            // ── 步驟 2：等候取出（進度條顯示於儲存架上） ──
            yield return Toils_General.Wait(30).WithProgressBarToilDelay(SourceRackInd);

            // ── 步驟 3：從儲存架容器取出一枚相容導彈，持於手上 ──
            Toil extractToil = ToilMaker.MakeToil("ExtractMissileFromRack");
            extractToil.initAction = delegate
            {
                Building_MissileRack rack = SourceRack;
                Building_MissileRack launcher = Launcher;
                if (rack == null || launcher == null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true);
                    return;
                }

                // 找出第一枚相容的導彈
                StorageSettings launcherSettings = launcher.GetStoreSettings();
                Thing toTake = null;
                foreach (Thing m in rack.HeldThings)
                {
                    if (launcherSettings.AllowedToAccept(m))
                    {
                        toTake = m;
                        break;
                    }
                }
                if (toTake == null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true);
                    return;
                }

                // 從容器取出並生成於儲存架旁（pawn 已在此處），再讓 pawn 拿起
                if (!rack.GetDirectlyHeldThings().TryDrop(toTake, ThingPlaceMode.Near, out Thing dropped) || dropped == null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true);
                    return;
                }

                pawn.carryTracker.TryStartCarry(dropped);
                // 旗標：導彈已成功取出，後續不再檢查儲存架是否為空
                missileExtracted = true;
            };
            extractToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return extractToil;

            // ── 步驟 4：前往發射平台 ──
            yield return Toils_Goto.GotoThing(LauncherInd, PathEndMode.Touch)
                .FailOnDespawnedNullOrForbidden(LauncherInd);

            // ── 步驟 5：裝填，將手上的導彈放入發射平台容器 ──
            Toil depositToil = ToilMaker.MakeToil("DepositMissileToLauncher");
            depositToil.initAction = delegate
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                Building_MissileRack launcher = Launcher;
                if (carried == null || launcher == null) { return; }

                bool added = launcher.GetDirectlyHeldThings().TryAdd(carried);
                if (!added)
                {
                    // 若發射平台在最後一刻滿了，把導彈放在附近而非讓它消失
                    pawn.carryTracker.TryDropCarriedThing(launcher.InteractionCell, ThingPlaceMode.Near, out _);
                }
            };
            depositToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return depositToil;
        }
    }
}
