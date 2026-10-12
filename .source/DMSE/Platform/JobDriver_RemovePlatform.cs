using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 拆除平台：站在相鄰格（B）施工，完成時格上若還有 pawn 就等它離開（拆掉後是虛空），
    /// 之後回收材料並把地形改回太空。流程對照原版 JobDriver_AffectFloor。
    /// </summary>
    public class JobDriver_RemovePlatform : JobDriver
    {
        private float workLeft = -1000f;

        private float totalWork = 1f;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, ReservationLayerDefOf.Floor, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => Map.designationManager.DesignationAt(TargetLocA, DMSE_DefOf.DMSE_RemovePlatform) == null);
            this.FailOn(() => !PlatformRemovalUtility.CanRemoveAt(TargetLocA, Map, out _));
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            Toil doWork = ToilMaker.MakeToil("RemovePlatform");
            doWork.initAction = delegate
            {
                totalWork = PlatformRemovalUtility.WorkAmount(Map.terrainGrid.TopTerrainAt(TargetLocA));
                workLeft = totalWork;
            };
            doWork.tickIntervalAction = delegate (int delta)
            {
                if (workLeft > 0f)
                {
                    workLeft -= doWork.actor.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f * delta;
                    doWork.actor.skills?.Learn(SkillDefOf.Construction, 0.1f * delta);
                }
                if (workLeft <= 0f && !PlatformRemovalUtility.AnyPawnAt(TargetLocA, Map))
                {
                    PlatformRemovalUtility.Remove(TargetLocA, Map, doLeavings: true);
                    Map.designationManager.DesignationAt(TargetLocA, DMSE_DefOf.DMSE_RemovePlatform)?.Delete();
                    ReadyForNextToil();
                }
            };
            doWork.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
            doWork.WithProgressBar(TargetIndex.A, () => 1f - workLeft / totalWork);
            doWork.defaultCompleteMode = ToilCompleteMode.Never;
            doWork.activeSkill = () => SkillDefOf.Construction;
            yield return doWork;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref workLeft, "workLeft", 0f);
            Scribe_Values.Look(ref totalWork, "totalWork", 1f);
        }
    }
}
