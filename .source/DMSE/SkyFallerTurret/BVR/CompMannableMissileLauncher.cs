using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// <see cref="CompMannable"/> 的專用子類：安裝於旋轉導彈發射架
    /// （<see cref="Building_MissileRack"/> + <see cref="CompMissileLauncher_Interceptor"/>）上時，
    /// 右鍵「操作」浮動選單改派 <see cref="DMSE_MissileJobDefOf.DMSE_ManMissileLauncher"/>
    /// （由 <see cref="JobDriver_ManMissileLauncher"/> 驅動），使操作的 pawn 在開始／持續操作時
    /// 自行尋找鄰近儲存架的相容彈藥並搬運裝填。
    ///
    /// vanilla 的 <see cref="CompMannable.CompFloatMenuOptions"/> 一律指派
    /// <see cref="JobDefOf.ManTurret"/>（由 <see cref="JobDriver_ManTurret"/> 驅動），該驅動只認得
    /// <see cref="Building_TurretGun"/> 的裝填／補給邏輯，對本 Mod 以容器儲存彈藥的發射平台無效
    /// （不會報錯，但也不會自動裝填）。
    ///
    /// parent 未掛 <see cref="CompMissileLauncher_Interceptor"/>（非發射平台）時，退回 vanilla 行為，
    /// 因此本 Comp 亦可安全地用於一般 mannable 建築而無副作用。
    /// </summary>
    public class CompMannableMissileLauncher : CompMannable
    {
        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn pawn)
        {
            if (parent.TryGetComp<CompMissileLauncher_Interceptor>() == null)
            {
                foreach (FloatMenuOption option in base.CompFloatMenuOptions(pawn))
                {
                    yield return option;
                }
                yield break;
            }

            if (!pawn.RaceProps.ToolUser || !pawn.CanReserveAndReach(parent, PathEndMode.InteractionCell, Danger.Deadly))
            {
                yield break;
            }

            if (Props.manWorkType != WorkTags.None && pawn.WorkTagIsDisabled(Props.manWorkType))
            {
                if (Props.manWorkType == WorkTags.Violent)
                {
                    yield return new FloatMenuOption(
                        "CannotManThing".Translate(parent.LabelShort, parent) + " (" +
                        "IsIncapableOfViolenceLower".Translate(pawn.LabelShort, pawn) + ")",
                        null);
                }
                yield break;
            }

            if (!Props.planetLayerWhitelist.NullOrEmpty() && !Props.planetLayerWhitelist.Contains(pawn.Map.Tile.LayerDef))
            {
                yield return new FloatMenuOption(
                    "CannotManThing".Translate(parent.LabelShort, parent) + " (" +
                    "CannotFunctionOnLayer".Translate(pawn.Map.Tile.LayerDef.label) + ")",
                    null);
                yield break;
            }

            yield return new FloatMenuOption("OrderManThing".Translate(parent.LabelShort, parent), delegate
            {
                Job job = JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_ManMissileLauncher, parent);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            });
        }
    }
}
