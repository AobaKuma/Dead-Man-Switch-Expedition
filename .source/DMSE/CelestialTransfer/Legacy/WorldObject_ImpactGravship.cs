using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 舊版撞擊飛行的世界物件，只供舊存檔遷移：讀檔後第一個 tick 直接撞擊（設計稿 §10-2）。
    /// 新的撞擊飛行由 <see cref="WorldObject_TransferShip"/> 的 isImpact 分支處理。
    /// </summary>
    public class WorldObject_ImpactGravship : TravelingObject
    {
        public override void Arrive()
        {
            MapParent parent = worldObject as MapParent;
            int engines = parent != null && parent.HasMap ? TransferImpactUtility.CountImpactEngines(parent.Map.listerBuildings.allBuildingsColonist) : 0;
            string shipName = worldObject?.LabelCap ?? Label;
            TransferImpactUtility.Resolve(shipName, engines, _end, worldObject);
            if (!Destroyed)
            {
                Destroy();
            }
        }
    }
}
