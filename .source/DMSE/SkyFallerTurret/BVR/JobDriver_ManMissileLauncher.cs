using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 操作旋轉導彈發射架（<see cref="Building_MissileRack"/> + <see cref="CompMissileLauncher_Interceptor"/> +
    /// <see cref="CompMannable"/>）的專用工作。
    ///
    /// 仿照 vanilla <see cref="JobDriver_ManTurret"/> 的「操作中自動處理裝填」設計，但改用本 Mod
    /// 的儲存架容器搬運機制（與 <see cref="WorkGiver_LoadMissileLauncher"/> /
    /// <see cref="JobDriver_LoadMissileLauncher"/> 相同的取出／裝填方式），而非 vanilla 只認得的
    /// 「地面散落彈殼」：
    ///   開始操作前先確認發射平台是否還有空彈槽；若有且鄰近儲存架能找到相容彈藥，
    ///   則自行前往取彈、搬回裝填，直到彈藥槽裝滿（支援多彈頭槽位，逐發循環取彈）才開始操作；
    ///   操作期間若彈藥被打光，同樣會自動中斷操作、重新取彈。
    ///
    /// 找不到任何可用來源時：若彈艙內仍有至少一發，就先以現有存量繼續操作；
    /// 完全沒有彈藥時才放棄本次操作指令（需玩家日後重新下令），行為對齊 vanilla 缺燃料／缺彈時的處理。
    /// </summary>
    public class JobDriver_ManMissileLauncher : JobDriver
    {
        private const TargetIndex LauncherInd = TargetIndex.A;
        private const TargetIndex SourceRackInd = TargetIndex.B;

        private Building_MissileRack Launcher => job.GetTarget(LauncherInd).Thing as Building_MissileRack;
        private Building_MissileRack SourceRack => job.GetTarget(SourceRackInd).Thing as Building_MissileRack;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(LauncherInd), job, 1, -1, null, errorOnFailed);

        /// <summary>發射平台是否仍有空彈槽，值得再跑一趟裝填。非發射平台一律 false。</summary>
        private static bool NeedsReload(Thing t)
        {
            if (!(t is Building_MissileRack rack)) { return false; }
            if (rack.TryGetComp<CompMissileLauncher_Interceptor>() == null) { return false; }
            return !rack.Full;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(LauncherInd);

            Toil gotoLauncherToOperate = Toils_Goto.GotoThing(LauncherInd, PathEndMode.InteractionCell);

            // ── 檢查是否需要裝填：需要則尋找來源儲存架並開始搬運，否則直接前往操作位置。 ──
            Toil checkReload = ToilMaker.MakeToil("DMSE_CheckMissileReload");
            checkReload.initAction = delegate
            {
                Building_MissileRack launcher = Launcher;
                if (launcher == null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                    return;
                }

                if (!NeedsReload(launcher))
                {
                    JumpToToil(gotoLauncherToOperate);
                    return;
                }

                Building_MissileRack source = WorkGiver_LoadMissileLauncher.FindSourceRack(pawn, launcher);
                if (source == null)
                {
                    if (launcher.StoredCount > 0)
                    {
                        // 附近暫時找不到來源，但彈艙仍有存量：先以現有彈藥繼續操作。
                        JumpToToil(gotoLauncherToOperate);
                        return;
                    }
                    if (pawn.Faction == Faction.OfPlayer)
                    {
                        Messages.Message(
                            "DMSE.Missile.NoRackAvailable".Translate().CapitalizeFirst(),
                            launcher,
                            MessageTypeDefOf.NegativeEvent);
                    }
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                    return;
                }

                job.SetTarget(SourceRackInd, source);
            };
            yield return checkReload;

            yield return Toils_Reserve.Reserve(SourceRackInd, 1, -1);

            // ── 前往儲存架 ──
            yield return Toils_Goto.GotoThing(SourceRackInd, PathEndMode.Touch)
                .FailOnDespawnedNullOrForbidden(SourceRackInd);

            // ── 等候取出（進度條顯示於儲存架上） ──
            yield return Toils_General.Wait(30).WithProgressBarToilDelay(SourceRackInd);

            // ── 從儲存架容器取出一枚相容彈藥，持於手上 ──
            Toil extractToil = ToilMaker.MakeToil("DMSE_ExtractMissileForManning");
            extractToil.initAction = delegate
            {
                Building_MissileRack rack = SourceRack;
                Building_MissileRack launcher = Launcher;
                if (rack == null || launcher == null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true);
                    return;
                }

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
                    // 期間被其他人取走：回頭重新判斷（可能已不需要，或需另尋來源）。
                    JumpToToil(checkReload);
                    return;
                }

                // 直接容器對容器搬運（rack 內部容器 → pawn 的 carryTracker 容器），不經過地圖。
                // 舊寫法先 TryDrop(ThingPlaceMode.Near) 落地再撿起：若發射架/儲存架週遭沒有空格
                // （例如緊貼牆面、其他建築的密集部署），TryDrop 會失敗、dropped 為 null，
                // 導致裝填工作在讀條取出後被直接中斷。改用 TryTransferToContainer 完全不依賴
                // 地圖上是否有空位，取出必定成功（只要 toTake 確實在 rack 容器內）。
                int transferred = rack.GetDirectlyHeldThings().TryTransferToContainer(
                    toTake, pawn.carryTracker.innerContainer, toTake.stackCount, out Thing carried);
                if (transferred <= 0 || carried == null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable, true);
                    return;
                }
                carried.def.soundPickup.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            };
            extractToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return extractToil;

            // ── 前往發射平台 ──
            yield return Toils_Goto.GotoThing(LauncherInd, PathEndMode.Touch)
                .FailOnDespawnedNullOrForbidden(LauncherInd);

            // ── 裝填；仍有空槽則回頭再取一發（多彈頭槽位），否則前往操作位置。 ──
            Toil depositToil = ToilMaker.MakeToil("DMSE_DepositMissileForManning");
            depositToil.initAction = delegate
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                Building_MissileRack launcher = Launcher;
                if (carried != null && launcher != null)
                {
                    if (!launcher.GetDirectlyHeldThings().TryAdd(carried))
                    {
                        pawn.carryTracker.TryDropCarriedThing(launcher.InteractionCell, ThingPlaceMode.Near, out _);
                    }
                }

                if (launcher != null && NeedsReload(launcher))
                {
                    JumpToToil(checkReload);
                }
                else
                {
                    JumpToToil(gotoLauncherToOperate);
                }
            };
            depositToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return depositToil;

            // ── 前往操作位置 ──
            yield return gotoLauncherToOperate;

            // ── 操作中：每 tick 檢查是否又需要裝填（彈藥被打光時自動中斷、重新取彈）。 ──
            Toil manToil = ToilMaker.MakeToil("DMSE_ManMissileLauncher");
            manToil.tickAction = delegate
            {
                Pawn actor = manToil.actor;
                Building building = actor.CurJob.targetA.Thing as Building;
                if (building == null)
                {
                    // 理論上會被 job 級的 FailOnDespawnedNullOrForbidden 提前攔截；
                    // 保守起見仍在此收尾，避免萬一發生時卡在無事可做的空轉狀態。
                    actor.jobs.EndCurrentJob(JobCondition.Incompletable);
                    return;
                }

                if (NeedsReload(building))
                {
                    JumpToToil(checkReload);
                    return;
                }

                building.GetComp<CompMannable>()?.ManForATick(actor);
                actor.rotationTracker.FaceCell(building.Position);
            };
            manToil.handlingFacing = true;
            manToil.defaultCompleteMode = ToilCompleteMode.Never;
            manToil.FailOnCannotTouch(LauncherInd, PathEndMode.InteractionCell);
            yield return manToil;
        }
    }
}
