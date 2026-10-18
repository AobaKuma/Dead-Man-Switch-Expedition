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
        public static MapComponent_StrikePlanner PlannerFor(Thing thing)
        {
            return thing?.MapHeld?.GetComponent<MapComponent_StrikePlanner>();
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
