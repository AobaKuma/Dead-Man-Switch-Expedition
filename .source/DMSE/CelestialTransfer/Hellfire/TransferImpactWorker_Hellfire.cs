using RimWorld;
using RimWorld.Planet;
using Verse;
using Fortified;

namespace DMSE
{
    /// <summary>
    /// DMSE 的撞擊內容（Hellfire）：撞擊坑、移除 DMS 軍隊據點、全派系敵對，
    /// 以及 DMS 殖民艦隊還在時的「艦隊失敗」結局。留在 DMSE，不隨轉移船框架搬進 FFF。
    /// </summary>
    public class TransferImpactWorker_Hellfire : TransferImpactWorker
    {
        public override string MakeCredits(string time, bool planetkiller, PlanetTile target)
        {
            if (!planetkiller && Find.FactionManager.FirstFactionOfDef(DMSE_DefOf.DMS_Army) != null)
            {
                return GameVictoryUtility.MakeEndCredits(
                    time + "\n" + "DMSE.Impact.Ending.Intro".Translate(),
                    "DMSE.Impact.Ending.FleetFailed.Outro".Translate(),
                    string.Empty,
                    "DMSE.Impact.Ending.Aboard".Translate(),
                    new System.Collections.Generic.List<Pawn>());
            }
            return base.MakeCredits(time, planetkiller, target);
        }

        public override void ApplyWorldEffects(string shipName, int engineCount, PlanetTile target, bool planetkiller)
        {
            ImpactCraterUtility.ApplyImpactCraterAtTile(shipName, engineCount, target);
            ImpactCraterUtility.RemoveAllDmsArmySettlements();
            ImpactCraterUtility.MakeAllFactionsHostileToPlayerFromImpact();
        }
    }
}
