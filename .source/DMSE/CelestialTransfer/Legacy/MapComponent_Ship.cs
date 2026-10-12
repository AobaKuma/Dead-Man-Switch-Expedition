using Verse;

namespace DMSE
{
    /// <summary>
    /// 舊版駕駛台轉移飛行的地圖元件，只為了讓舊存檔載入時不出現 "Could not find class"（設計稿 §10-2）。
    /// 舊存檔中的暖機/點火狀態不再讀取：飛行中的 <see cref="TravelingObject"/> 會在讀檔後第一個 tick 直接抵達。
    /// 遷移期結束後連同 <c>WorldObject_Transfer</c>、<c>WorldObject_ImpactGravship</c> 一起刪除。
    /// </summary>
    public class MapComponent_Ship : MapComponent
    {
        public MapComponent_Ship(Map map) : base(map)
        {
        }
    }
}
