using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 超視距戰鬥系統所有裝置 Comp 的共用基類。
    /// 統一可用性判定（未損壞、開關開啟、有電、未被擊暈、有燃料），並提供中央管理器存取。
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
        public virtual bool Active
        {
            get
            {
                if (parent == null || !parent.Spawned || parent.Destroyed)
                {
                    return false;
                }

                CompBreakdownable breakdownable = parent.GetComp<CompBreakdownable>();
                if (breakdownable != null && breakdownable.BrokenDown)
                {
                    return false;
                }

                CompFlickable flickable = parent.GetComp<CompFlickable>();
                if (flickable != null && !flickable.SwitchIsOn)
                {
                    return false;
                }

                CompPowerTrader power = parent.GetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn)
                {
                    return false;
                }

                CompStunnable stunnable = parent.GetComp<CompStunnable>();
                if (stunnable != null && stunnable.StunHandler != null && stunnable.StunHandler.Stunned)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
