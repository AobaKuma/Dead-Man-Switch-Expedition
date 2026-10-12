using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移計畫（設計稿 §4-1）。只存起訖點與距離；燃料、時間等數值依當下的船況即時重算，
    /// 計畫制定後船體變動（引擎損壞、燃料被搬走）會反映在 ITab 與整備檢查上。
    /// </summary>
    public class TransferPlan : IExposable
    {
        public PlanetTile origin = PlanetTile.Invalid;

        /// <summary>轉移：停泊格（與起點同一太空層）。撞擊：地表的撞擊點。</summary>
        public PlanetTile destination = PlanetTile.Invalid;

        /// <summary>起點到 <see cref="FlightTarget"/> 的格數距離（制定時計算一次；起點地圖不會移動）。</summary>
        public int distance;

        /// <summary>選取的目的地已被占用，改停泊在最近的空鄰格（設計稿 §6-5）。</summary>
        public bool parkedAtNeighbor;

        /// <summary>目的地已有地點（小行星、空間站、據點…）：船停在旁邊的空格，抵達後選位降落到該地點的地圖。</summary>
        public bool landOnSite;

        /// <summary>撞擊飛行（設計稿 §9）：飛到撞擊點上方後不減速，直接撞向地表。</summary>
        public bool isImpact;

        /// <summary>撞擊飛行時，起點太空層中最接近撞擊點的格子；船在太空層沿航線飛到這裡。</summary>
        public PlanetTile approachTile = PlanetTile.Invalid;

        /// <summary>船在起點太空層中的航線終點。</summary>
        public PlanetTile FlightTarget => isImpact ? approachTile : destination;

        public TransferPlan()
        {
        }

        public TransferPlan(PlanetTile origin, PlanetTile destination, bool parkedAtNeighbor)
        {
            this.origin = origin;
            this.destination = destination;
            this.parkedAtNeighbor = parkedAtNeighbor;
            distance = TransferFlightMath.Distance(origin, destination);
        }

        public static TransferPlan Impact(PlanetTile origin, PlanetTile target)
        {
            TransferPlan plan = new TransferPlan
            {
                origin = origin,
                destination = target,
                isImpact = true,
                approachTile = ApproachTileFor(origin, target)
            };
            plan.distance = plan.approachTile.Valid ? TransferFlightMath.Distance(origin, plan.approachTile) : int.MaxValue;
            return plan;
        }

        /// <summary>起點太空層中最接近撞擊點的格子。</summary>
        public static PlanetTile ApproachTileFor(PlanetTile origin, PlanetTile target)
        {
            if (!origin.Valid || !target.Valid)
            {
                return PlanetTile.Invalid;
            }
            return origin.Layer.GetClosestTile_NewTemp(target);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref origin, "origin");
            Scribe_Values.Look(ref destination, "destination");
            Scribe_Values.Look(ref distance, "distance");
            Scribe_Values.Look(ref parkedAtNeighbor, "parkedAtNeighbor");
            Scribe_Values.Look(ref landOnSite, "landOnSite");
            Scribe_Values.Look(ref isImpact, "isImpact");
            Scribe_Values.Look(ref approachTile, "approachTile", PlanetTile.Invalid);
        }
    }
}
