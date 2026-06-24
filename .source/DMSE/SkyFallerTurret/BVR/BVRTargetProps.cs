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

        /// <summary>末端速度，用於窗口與末端時間估算（對應設計圖中的 speed）。</summary>
        public float speed = 15f;

        public static readonly BVRTargetProps Default = new BVRTargetProps();
    }
}
