using Verse;

namespace DMSE
{
    /// <summary>
    /// 鋼樑施工單（設計稿 §3-2）：掛在施工端的樁上。<see cref="dismantle"/> 為 false 時是架設單，
    /// 完工後才建立連結；為 true 時是拆除單，完工後解除連結並依比例返還材料。
    /// </summary>
    public class PileBeamOrder : IExposable
    {
        /// <summary>鋼樑另一端的結構樁。</summary>
        public Thing target;

        public bool dismantle;

        public float workDone;

        public PileBeamOrder()
        {
        }

        public PileBeamOrder(Thing target, bool dismantle)
        {
            this.target = target;
            this.dismantle = dismantle;
        }

        public CompStructuralPile TargetComp => target?.TryGetComp<CompStructuralPile>();

        public void ExposeData()
        {
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref dismantle, "dismantle");
            Scribe_Values.Look(ref workDone, "workDone");
        }
    }
}
