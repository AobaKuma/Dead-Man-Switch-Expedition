using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 撞擊飛行（Hellfire，設計稿 §9）的結局處理，沿用舊 <c>WorldObject_ImpactGravship.Arrive</c> 的流程：
    /// 摧毀目標據點 → 片尾文字與世界效果（<see cref="TransferImpactWorker"/>；DMSE 是撞擊坑、移除 DMS 軍隊據點、全派系敵對）→ 銷毀船 → 片尾。
    /// 新的 <see cref="WorldObject_TransferShip"/> 與舊存檔遷移共用。
    /// </summary>
    public static class TransferImpactUtility
    {
        /// <summary>撞擊用引擎（<see cref="CompProperties_TransferEngine.impactCapable"/>）的數量，不論是否運作中。</summary>
        public static int CountImpactEngines(List<Building> buildings)
        {
            int count = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                CompTransferEngine engine = buildings[i].GetComp<CompTransferEngine>();
                if (engine != null && engine.Props.impactCapable)
                {
                    count++;
                }
            }
            return count;
        }

        public static bool IsPlanetkiller(int engineCount)
        {
            return engineCount >= TransferFlightMath.Tuning.planetkillerMinEngines;
        }

        /// <param name="shipName">撞擊坑命名用。</param>
        /// <param name="engineCount">撞擊用引擎數：決定撞擊坑規模，達 planetkillerMinEngines 為行星殺手結局。</param>
        /// <param name="ship">撞擊後銷毀的世界物件（連同其地圖）。</param>
        public static void Resolve(string shipName, int engineCount, PlanetTile target, WorldObject ship)
        {
            Settlement settlement = Find.WorldObjects.SettlementAt(target);
            if (settlement != null && settlement.Faction != Faction.OfPlayer)
            {
                DestroyedSettlement ds = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(settlement.Tile.LayerDef.DestroyedSettlementWorldObjectDef);
                ds.Tile = settlement.Tile;
                ds.SetFaction(settlement.Faction);
                Find.WorldObjects.Add(ds);
                settlement.Destroy();
            }

            bool planetkiller = IsPlanetkiller(engineCount);
            string time = GenDate.DateFullStringAt(Find.TickManager.TicksGame, Find.WorldGrid.LongLatOf(target));
            // 片尾文字先決定（看撞擊前的世界），再套用世界效果；兩者都由調校 Def 指定的 Worker 提供
            TransferImpactWorker worker = TransferFlightMath.Tuning.ImpactWorker;
            string credits = worker.MakeCredits(time, planetkiller, target);
            worker.ApplyWorldEffects(shipName, engineCount, target, planetkiller);
            if (ship != null && !ship.Destroyed)
            {
                ship.Destroy();
            }
            GameVictoryUtility.ShowCredits(credits, SongDefOf.EndCreditsSong, exitToMainMenu: planetkiller, songStartDelay: TransferFlightMath.Tuning.impactFadeSeconds);
        }
    }
}
