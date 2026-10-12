using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 起飛程序等待操作員時，派殖民者到控制台互動格執行點火程序（設計稿 §4-2 步驟 3）。
    /// 也可以右鍵控制台強制指派（原版 WorkGiver_Scanner 自動產生「優先」選項）。
    /// </summary>
    public class WorkGiver_InitiateTransfer : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.InteractionCell;

        public override Danger MaxPathDanger(Pawn pawn)
        {
            return Danger.Deadly;
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(pawn.Map);
            if (comp == null)
            {
                return Enumerable.Empty<Thing>();
            }
            return comp.Consoles.Where(c => c.State == TransferConsoleState.AwaitingOperator).Select(c => (Thing)c.parent);
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(pawn.Map);
            return comp == null || !comp.Consoles.Any(c => c.State == TransferConsoleState.AwaitingOperator);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompTransferConsole console = t.TryGetComp<CompTransferConsole>();
            if (console == null || console.State != TransferConsoleState.AwaitingOperator || t.Faction != pawn.Faction)
            {
                return null;
            }
            if (!console.IsAvailable)
            {
                JobFailReason.Is("DMSE.Transfer.Issue.ConsoleUnavailable".Translate());
                return null;
            }
            if (t.IsForbidden(pawn) || t.IsBurning() || !pawn.CanReserve(t, 1, -1, null, forced))
            {
                return null;
            }
            return JobMaker.MakeJob(DMSE_DefOf.DMSE_InitiateTransfer, t);
        }
    }
}
