using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    public class WorkGiver_RemovePlatform : WorkGiver_ConstructAffectFloor
    {
        protected override DesignationDef DesDef => DMSE_DefOf.DMSE_RemovePlatform;

        public override bool HasJobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            if (!base.HasJobOnCell(pawn, c, forced))
            {
                return false;
            }
            if (!PlatformRemovalUtility.CanRemoveAt(c, pawn.Map, out _))
            {
                return false;
            }
            // 平台拆掉後是虛空：施工者必須站在相鄰的格子
            return PlatformRemovalUtility.FindStandCell(pawn, c).IsValid;
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            IntVec3 stand = PlatformRemovalUtility.FindStandCell(pawn, c);
            if (!stand.IsValid)
            {
                return null;
            }
            return JobMaker.MakeJob(DMSE_DefOf.DMSE_RemovePlatformJob, c, stand);
        }
    }
}
