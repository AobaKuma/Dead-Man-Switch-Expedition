using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 施工端結構樁的鋼樑架設/拆除（設計稿 §3-2「施工」）。材料到齊的架設單、或任何拆除單都會派工；
    /// 施工一律在施工端的樁旁進行（鋼樑可能橫跨虛空，中段沒有可站立的格子）。
    /// </summary>
    public class WorkGiver_BuildPileBeam : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override Danger MaxPathDanger(Pawn pawn)
        {
            return Danger.Deadly;
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(pawn.Map);
            return comp != null ? comp.PilesReadyToWork() : Enumerable.Empty<Thing>();
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(pawn.Map);
            return comp == null || !comp.AnyPileReadyToWork();
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompStructuralPile pile = t.TryGetComp<CompStructuralPile>();
            if (pile == null || t.Faction != pawn.Faction || !pile.CurrentOrderReady)
            {
                return null;
            }
            if (t.IsForbidden(pawn) || t.IsBurning() || !pawn.CanReserve(t, 1, -1, null, forced))
            {
                return null;
            }
            return JobMaker.MakeJob(DMSE_DefOf.DMSE_BuildPileBeam, t);
        }
    }
}
