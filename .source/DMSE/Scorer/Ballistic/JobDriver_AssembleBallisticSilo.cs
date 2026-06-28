using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    [DefOf]
    public static class DMSE_BallisticJobDefOf
    {
        public static JobDef DMSE_AssembleBallisticSilo;

        static DMSE_BallisticJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DMSE_BallisticJobDefOf));
        }
    }

    /// <summary>
    /// 在彈道導彈發射架上執行裝配施工，資源全數存入後呼叫
    /// <see cref="CompMissileConfig.ApplyAssembly"/> 並將發射架標記為已裝填。
    ///
    /// 與 <see cref="JobDriver_AssembleMissile"/> 的差異：
    ///   - 目標是建築而非導彈物品，但兩者皆為 <c>Thing</c>，Toil 邏輯完全相容。
    ///   - 完成時額外呼叫 <see cref="CompBallisticLauncher.MarkLoaded"/>。
    /// </summary>
    public class JobDriver_AssembleBallisticSilo : JobDriver
    {
        private const TargetIndex SiloInd = TargetIndex.A;

        private Thing Silo => job.GetTarget(SiloInd).Thing;
        private CompMissileConfig MissileCfg => Silo != null ? Silo.TryGetComp<CompMissileConfig>() : null;
        private CompBallisticLauncher Launcher => Silo != null ? Silo.TryGetComp<CompBallisticLauncher>() : null;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(SiloInd), job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(SiloInd);
            AddFailCondition(() =>
            {
                CompBallisticLauncher launcher = Launcher;
                CompMissileConfig config = MissileCfg;
                return launcher == null || config == null
                    || launcher.IsLoaded
                    || !config.NeedsAssembly
                    || !config.ResourcesComplete;
            });

            yield return Toils_Goto.GotoThing(SiloInd, PathEndMode.Touch);

            // 裝配工作量：由彈體定義提供，預設 600 tick。
            int work = 600;
            CompMissileConfig cfg = MissileCfg;
            if (cfg?.pending?.body != null)
            {
                work = cfg.pending.body.assembleWorkAmount;
            }

            yield return Toils_General.Wait(work)
                .FailOnDespawnedNullOrForbidden(SiloInd)
                .WithProgressBarToilDelay(SiloInd);

            Toil finalize = ToilMaker.MakeToil("ApplyBallisticSiloAssembly");
            finalize.initAction = delegate
            {
                MissileCfg?.ApplyAssembly();
                Launcher?.MarkLoaded();
            };
            finalize.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finalize;
        }
    }
}
