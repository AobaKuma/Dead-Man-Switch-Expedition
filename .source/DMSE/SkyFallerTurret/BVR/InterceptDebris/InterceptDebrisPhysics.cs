using Verse;

namespace DMSE
{
    /// <summary>
    /// 攔截殘骸的物理參數（docs/攔截殘骸特效_實作與技術美術計畫.md §4、§6）。
    /// 之後由 InterceptDebrisProfileDef 持有並在 XML 調參；未指定時用 <see cref="Default"/>。
    /// 單位：格、tick。高度 h 為假想高度，畫成往畫面上方（+z）的偏移。
    /// </summary>
    public class InterceptDebrisPhysics
    {
        /// <summary>保留來襲物前進速度的比例（殘骸「從來襲方向掉落」的來源）。</summary>
        public FloatRange inherit = new FloatRange(0.45f, 0.85f);
        /// <summary>繼承速度的上限（格/tick），避免極快目標的殘骸飛出整張地圖。</summary>
        public float maxInheritSpeed = 1.5f;
        /// <summary>爆散速度（格/tick，乘上質量^-0.35）。</summary>
        public float burstSpeed = 0.18f;
        /// <summary>爆散方向朝上的機率。</summary>
        public float upBias = 0.6f;
        /// <summary>
        /// 來襲物俯衝分量（相對前進速度）。P4 實機：0.3 時末端殘骸 25–35 tick 就落地，比 §11「1–2 秒」快，P6 調為 0.1。
        /// </summary>
        public float descentRatio = 0.1f;
        /// <summary>重力（格/tick²）。P6 由 0.010 調為 0.006：末端 10–18 格高約 60–90 tick 落地。</summary>
        public float gravity = 0.006f;
        /// <summary>每 tick 速度衰減（乘上質量^-0.5；輕碎片阻力大、落點近）。</summary>
        public float dragBase = 0.02f;
        /// <summary>旋轉速度（度/tick，乘上質量^-0.5）。</summary>
        public float spinBase = 12f;
        public float windFactor = 0.002f;
        /// <summary>高度畫成畫面偏移的倍率。</summary>
        public float heightScale = 1f;

        // ---- 攔截點 ----
        /// <summary>末端攔截高度：剛進入末端窗口（approach = 0）時。</summary>
        public float terminalHeightFar = 18f;
        /// <summary>末端攔截高度：即將落地（approach = 1）時。</summary>
        public float terminalHeightNear = 10f;
        /// <summary>末端攔截點沿航線的隨機抖動（格）。</summary>
        public float terminalJitter = 1f;
        public FloatRange midcourseHeight = new FloatRange(30f, 40f);
        /// <summary>中過程爆心「畫面位置」在入場邊緣內側的距離（格）：雲團一部分越過邊緣被裁掉，看起來像在邊緣外被擊落。</summary>
        public FloatRange midcourseInset = new FloatRange(4f, 8f);
        /// <summary>
        /// 爆心畫面位置至少離地圖外框幾格（在圖內）。原版 MapEdgeClipDrawer 用 MetaOverlay shader 在地圖外畫黑色遮罩，
        /// 它的 render queue 蓋過所有 mote，不論 altitude（2026-10-26 實機截圖：圖外的爆心完全看不見）。超過時把地面點往回推，高度不變。
        /// </summary>
        public float drawEdgeInset = 4f;

        // ---- 遠距末端攔截（虛擬爆心） ----
        /// <summary>
        /// 迫近進度低於此值的末端攔截視為「遠距」：爆心放在入場邊緣外的虛擬點（CIWS 剛開火時目標還在圖外），
        /// 不畫爆炸，殘骸飛進畫面時淡入。原本這些爆心都被夾在入場邊緣上。
        /// </summary>
        public float terminalRemoteApproach = 0.2f;
        /// <summary>遠距攔截點的地面點在入場邊緣外的距離（格，approach = 0 時；越接近 <see cref="terminalRemoteApproach"/> 越靠邊緣）。</summary>
        public FloatRange terminalRemoteDistance = new FloatRange(6f, 18f);
        /// <summary>外推距離上限 = 代表性主殘骸的前進距離 × 此比例：慢速目標的殘骸飛不遠，點太遠會整片看不到。</summary>
        public float remoteReach = 0.6f;
        /// <summary>遠距攔截點垂直航線的隨機偏移（格），對應 CIWS 的橫向掃射。</summary>
        public float remoteSpread = 6f;
        /// <summary>
        /// 遠距殘骸的淡入帶：畫面位置離外框 <see cref="drawEdgeInset"/> 格內完全透明，再往內這麼多格才完全不透明。
        /// </summary>
        public float remoteFadeCells = 6f;
        /// <summary>遠距殘骸至少要淡入到這個不透明度才生成（生成前先模擬；看不到的不生成）。</summary>
        public float remoteMinFade = 0.5f;

        // ---- 生命週期 ----
        /// <summary>在圖外離邊緣超過此距離仍在飛的殘骸提前移除。</summary>
        public float offMapCull = 40f;
        public int maxLifeTicks = 600;

        public static readonly InterceptDebrisPhysics Default = new InterceptDebrisPhysics();
    }
}
