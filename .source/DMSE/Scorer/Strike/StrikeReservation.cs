using Verse;

namespace DMSE
{
    /// <summary>
    /// 打擊保留的查詢入口（docs/BVR_波次打擊規劃.md §8-4）。彈藥 = Rail 容器中的導彈 Thing，
    /// 或彈道井／Scorer 平台本身。保留中的彈藥不會被單發 Gizmo、SAM 或裝配工作取用。
    /// 容器中的導彈是 unspawned，以 <see cref="Thing.MapHeld"/> 找到所在地圖。
    /// </summary>
    public static class StrikeReservation
    {
        private static Map cachedMap;
        private static MapComponent_StrikePlanner cachedPlanner;

        /// <summary>
        /// 防空每 tick 對每座發射器查一次（找可用攔截彈時要略過保留彈），
        /// Map.GetComponent 是線性搜尋，所以記住上一張地圖的結果。
        /// </summary>
        public static MapComponent_StrikePlanner PlannerFor(Thing thing)
        {
            Map map = thing?.MapHeld;
            if (map == null) { return null; }
            if (cachedMap != map || cachedPlanner == null || cachedPlanner.map != map)
            {
                cachedPlanner = map.GetComponent<MapComponent_StrikePlanner>();
                cachedMap = map;
            }
            return cachedPlanner;
        }

        public static bool IsReserved(Thing round)
        {
            return PlannerFor(round)?.IsReserved(round) ?? false;
        }

        /// <summary>保留擁有者；未保留時回傳 -1。</summary>
        public static int OwnerOf(Thing round)
        {
            return PlannerFor(round)?.OwnerOf(round) ?? -1;
        }
    }
}
