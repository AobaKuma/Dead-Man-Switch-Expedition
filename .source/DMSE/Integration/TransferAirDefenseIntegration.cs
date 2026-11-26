using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移船與防空封鎖之間的銜接。兩邊的核心互不引用：轉移船只提供 <see cref="TransferReadiness.ExtraChecks"/>，
    /// 封鎖攔截只提供 <see cref="IBlockadeTargetHandler"/>；只有這個類別同時認識兩邊。
    /// 搬進 FFF 後，這裡要在轉移船與封鎖空域兩項功能都啟用時才註冊（docs/轉移船與BVR移植FFF_可行性評估.md §2.1、§6.4）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TransferAirDefenseIntegration
    {
        private static readonly TransferShipBlockadeTarget ShipTarget = new TransferShipBlockadeTarget();

        static TransferAirDefenseIntegration()
        {
            TransferReadiness.ExtraChecks -= AirspaceCheck;
            TransferReadiness.ExtraChecks += AirspaceCheck;
            BlockadeHitUtility.RegisterTarget(ShipTarget);
        }

        // 防空封鎖（主動攔截）：航線穿越封鎖圈只警告，飛行中會被接戰。
        private static void AirspaceCheck(CompTransferConsole console, TransferPlan plan, bool includeWarnings, List<TransferIssue> issues)
        {
            string airspace = AirspaceProblem(plan);
            if (airspace != null)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, airspace));
            }
        }

        /// <summary>
        /// 計畫航線是否穿越防空封鎖圈（會被主動攔截）：轉移檢查太空層航段；撞擊另檢查地表撞擊點。穿越時回傳說明。
        /// </summary>
        public static string AirspaceProblem(TransferPlan plan)
        {
            if (plan == null || !plan.origin.Valid) { return null; }
            AirspaceRestriction restriction = AirspaceRestrictionUtility.CheckRoute(Faction.OfPlayer, plan.origin, plan.FlightTarget);
            if (!restriction.Restricted && plan.isImpact)
            {
                restriction = AirspaceRestrictionUtility.CheckPoint(Faction.OfPlayer, plan.destination);
            }
            return restriction.Restricted ? restriction.Reason : null;
        }

        /// <summary>飛行中的轉移艦會被封鎖攔截；命中時船體地圖上幾處建築爆炸。</summary>
        private sealed class TransferShipBlockadeTarget : IBlockadeTargetHandler
        {
            public bool InFlight(WorldObject target) => target is WorldObject_TransferShip ship && ship.InFlight;

            public bool TryApplyHit(WorldObject target, Faction by)
            {
                if (!(target is WorldObject_TransferShip ship)) { return false; }
                if (!ship.HasMap) { return true; }
                Map map = ship.Map;
                List<Building> buildings = map.listerBuildings.allBuildingsColonist.Where(b => !b.Destroyed).ToList();
                int hits = 0;
                foreach (Building building in buildings.InRandomOrder().Take(3))
                {
                    GenExplosion.DoExplosion(building.Position, map, 2.9f, DamageDefOf.Bomb, null, damAmount: 35);
                    hits++;
                }
                Find.LetterStack.ReceiveLetter("DMSE.Blockade.TransferHitLabel".Translate(),
                    "DMSE.Blockade.TransferHitText".Translate(ship.LabelCap, by.NameColored, hits), LetterDefOf.NegativeEvent, ship);
                return true;
            }
        }
    }
}
