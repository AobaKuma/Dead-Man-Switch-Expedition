using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 超視距戰鬥系統所有裝置 Comp 的共用基類。
    /// 統一可用性判定（未損壞、開關開啟、有電、未被擊暈、若可操作則需已有人操作、有燃料），並提供中央管理器存取。
    /// 各子類於 <see cref="ThingComp.PostSpawnSetup"/> / <see cref="ThingComp.PostDeSpawn"/> 自行向管理器註冊。
    /// 由於是 ThingComp，單一建築可同時掛多個此類 Comp（例如搜索雷達 + 火控雷達）達成複合功能。
    /// </summary>
    public abstract class CompBVRDevice : ThingComp
    {
        public MapComponent_BVRCombat Manager
        {
            get
            {
                Map map = parent != null ? parent.MapHeld : null;
                return map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            }
        }

        /// <summary>裝置目前是否可運作。</summary>
        public virtual bool Active => Operational(countPowerStarting: false);

        /// <summary>
        /// 給反輻射導引頭判斷雷達是否開機：同 <see cref="Active"/>，但電網正要開啟的裝置也算
        /// （見 <see cref="GuidanceType.PowerStarting"/>；導彈抵達時才生成的地圖上，雷達在抵達那一 tick 都還沒通電）。
        /// </summary>
        public bool ActiveForSeeker => Operational(countPowerStarting: true);

        // 可運作判定用到的 comp：建築的 comp 組成在生成後不變，第一次判定時取一次即可
        //（火控每 tick 對每座發射器判定，數十座時每次 6 個 GetComp 的線性搜尋很可觀）。
        private bool operationalCompsCached;
        private CompBreakdownable cBreakdownable;
        private CompFlickable cFlickable;
        private CompPowerThrottle cThrottle;
        private CompPowerTrader cPower;
        private CompStunnable cStunnable;
        private CompMannable cMannable;

        private bool Operational(bool countPowerStarting)
        {
            if (parent == null) { return false; }
            if (!operationalCompsCached)
            {
                cBreakdownable = parent.GetComp<CompBreakdownable>();
                cFlickable = parent.GetComp<CompFlickable>();
                cThrottle = parent.GetComp<CompPowerThrottle>();
                cPower = parent.GetComp<CompPowerTrader>();
                cStunnable = parent.GetComp<CompStunnable>();
                cMannable = parent.GetComp<CompMannable>();
                operationalCompsCached = true;
            }
            return Evaluate(parent, cBreakdownable, cFlickable, cThrottle, cPower, cStunnable, cMannable, countPowerStarting);
        }

        /// <summary>
        /// 共用的可運作判定，供無法繼承本類的 Comp（例如 <see cref="CompBuildingTurretGun"/>）沿用同一套規則。
        /// </summary>
        public static bool IsOperational(ThingWithComps parent, bool countPowerStarting = false)
        {
            if (parent == null) { return false; }
            return Evaluate(parent, parent.GetComp<CompBreakdownable>(), parent.GetComp<CompFlickable>(), parent.GetComp<CompPowerThrottle>(),
                parent.GetComp<CompPowerTrader>(), parent.GetComp<CompStunnable>(), parent.GetComp<CompMannable>(), countPowerStarting);
        }

        private static bool Evaluate(ThingWithComps parent, CompBreakdownable breakdownable, CompFlickable flickable,
            CompPowerThrottle throttle, CompPowerTrader power, CompStunnable stunnable, CompMannable mannable, bool countPowerStarting)
        {
            if (parent == null || !parent.Spawned || parent.Destroyed)
            {
                return false;
            }

            if (breakdownable != null && breakdownable.BrokenDown)
            {
                return false;
            }

            if (flickable != null && !flickable.SwitchIsOn)
            {
                return false;
            }

            // 功率段位 0 是待機：不偵測、不干擾、不佔通道、不被反輻射鎖定，但仍吃待機電。
            if (throttle != null && throttle.IsStandby)
            {
                return false;
            }

            if (power != null && !power.PowerOn && !(countPowerStarting && GuidanceType.PowerStarting(power)))
            {
                return false;
            }

            if (stunnable != null && stunnable.StunHandler != null && stunnable.StunHandler.Stunned)
            {
                return false;
            }

            // 若掛有 CompMannable（例如需操作員手動操作的旋轉發射架／早期型雷達），
            // 未有人操作時裝置視為不可運作。與 CompTurretFacingRenderer.DeviceActive 的判定保持一致，
            // 使「視覺停止轉向」與「實際停止鎖定／發射」同步發生，而非僅視覺表現。
            if (mannable != null && !mannable.MannedNow)
            {
                return false;
            }

            return true;
        }
    }
}
