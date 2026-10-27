using Verse;

namespace DMSE
{
    /// <summary>
    /// 目標屬性。掛在 incoming skyfaller / 空投的 ThingDef 上，描述該目標在超視距戰鬥系統中的雷達特性。
    /// 若目標 def 沒有此擴充，則使用 <see cref="Default"/> 的預設值。
    /// </summary>
    public class BVRTargetProps : DefModExtension
    {
        /// <summary>綜合距離等級：越高代表越早被搜索雷達偵測、預警/攔截窗口越長。</summary>
        public float distanceLevel = 1f;

        /// <summary>隱身值：雷達的反隱身等級需 >= 此值才能完整偵測，否則窗口縮短、命中率下降。</summary>
        public int stealthLevel = 0;

        /// <summary>飛行速度：越快越難被攔截（降低火控命中率、延長鎖定時間）。</summary>
        public float speed = 15f;

        /// <summary>
        /// 飛行高度（公尺）。雷達視距與預警窗口依此縮放：
        /// 低空（掠海／貼地）較晚被發現、窗口短；高空（彈道、軌道）較早被發現、窗口長。
        /// 預設 1000 = 中性，既有 Def 未指定時行為不變。
        /// </summary>
        public float altitude = 1000f;

        /// <summary>被攔截後的殘骸外觀與數量；null = 依種類取預設（導彈／空投艙）。</summary>
        public InterceptDebrisProfileDef debrisProfile;

        public static readonly BVRTargetProps Default = new BVRTargetProps();
    }
}
