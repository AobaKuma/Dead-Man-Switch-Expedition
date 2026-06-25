using Verse;

namespace DMSE
{
    /// <summary>
    /// 熱發射特效：尾管立即點火，發射瞬間即全功率噴焰並產生較強的初始爆發。
    /// 用於 SAM 等軌/管式發射的飛彈（相對於巡航導彈的冷射）。
    /// </summary>
    public class CompProperties_EffecterHotLaunch : CompProperties_EffecterLaunch
    {
        /// <summary>發射初期排氣的爆發放大倍率。</summary>
        public float launchBlastScale = 1.1f;

        public CompProperties_EffecterHotLaunch()
        {
            compClass = typeof(CompEffecterHotLaunch);
            delayTicks = 0; // 熱射無彈射延遲。
        }
    }

    public class CompEffecterHotLaunch : CompEffecterLaunch
    {
        private CompProperties_EffecterHotLaunch HotProps => (CompProperties_EffecterHotLaunch)props;

        protected override int IgnitionDelayTicks => 0;

        protected override float InitialBlastScale => HotProps.launchBlastScale;
    }
}
