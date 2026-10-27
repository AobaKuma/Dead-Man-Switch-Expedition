using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 一種發射方式的特效參數（docs/導彈發射特效_重製計畫.md §5）。由發射 Projectile 的
    /// <see cref="CompProperties_EffecterLaunch.fxProfile"/> 指定；沒有指定時走舊的尾焰／煙（相容）。
    /// 長度單位：格；時間單位：tick。顏色：煙與地面雲的深淺主要放在貼圖裡，tint 是在貼圖上再乘；日夜與燈光由 <see cref="FxLighting"/> 另外乘上。
    /// </summary>
    public class LaunchFxProfileDef : Def
    {
        // ---- 尾焰（§4.1） ----
        /// <summary>白熱核心長度（推力 1 時）；外焰 = 1.8 倍。</summary>
        public float coreLength = 2.5f;
        /// <summary>外焰寬度；核心 = 0.35 倍、光暈直徑 = 2.5 倍。</summary>
        public float plumeWidth = 0.9f;
        public Color coreColor = new Color(1f, 0.97f, 0.85f, 1f);
        public Color flameColor = new Color(1f, 0.62f, 0.22f, 1f);

        // ---- 推力時間軸（§4.1） ----
        /// <summary>點火後推力從 0 升到 1 的 tick 數。</summary>
        public int rampTicks = 4;
        /// <summary>助推器燃燒 tick（0 = 無助推器）；燒完後 10 tick 內降到 <see cref="sustainThrust"/>，並觸發分離。</summary>
        public int boosterTicks;
        public float sustainThrust = 1f;
        /// <summary>可選：推力對發射進度（EffectEvaluate）的倍率。</summary>
        public SimpleCurve thrustCurve;

        // ---- 煙柱（§4.2–4.3） ----
        /// <summary>尾焰把煙往後推的速度（格/tick）。</summary>
        public float exhaustSpeed = 0.35f;
        /// <summary>煙團初始大小（推力 1 時）。</summary>
        public float smokeSize = 1.4f;
        public float smokeGrowth = 2.5f;
        /// <summary>煙的深淺：0 = 淺灰、1 = 深灰（淺灰貼圖乘上灰色 tint，<see cref="LaunchFxEmitter.ShadeTint"/>）。</summary>
        public float smokeShade = 0.5f;
        /// <summary>單顆煙團的顏色與透明度：煙柱沿線約 4 顆前後重疊，透明度給 0.3–0.4。</summary>
        public Color smokeColor = new Color(1f, 1f, 1f, 0.4f);
        /// <summary>剛出噴口被尾焰照亮的顏色。</summary>
        public Color smokeHotColor = new Color(1f, 0.82f, 0.6f, 0.55f);
        /// <summary>煙團壽命（中密度；低 0.5 倍、高 1.5 倍）：8 秒（使用者核准）。</summary>
        public int trailLifeTicks = 480;
        public float buoyancy = 0.003f;
        /// <summary>沿線段發射的間距 = spacingFactor × 煙團大小（0.25：前後重疊 75%；0.35 時實機截圖仍顯得一顆顆）。</summary>
        public float spacingFactor = 0.25f;
        /// <summary>煙團繼承彈體速度的比例。</summary>
        public float inherit = 0.1f;

        // ---- 一次性爆發（§4.4） ----
        /// <summary>熱射點火的地面雲顆數（中密度）。</summary>
        public int groundCloudCount = 14;
        public float groundCloudSpeed = 0.35f;
        public FloatRange groundCloudSize = new FloatRange(2.5f, 4.5f);
        public Color groundCloudColor = new Color(1f, 0.95f, 0.85f, 0.5f);
        public int groundCloudLifeTicks = 480;
        /// <summary>點火閃光大小。</summary>
        public float ignitionFlash = 5f;
        /// <summary>冷射彈射時發射點的燃氣顆數（熱射不用）。</summary>
        public int ejectionPuffs = 8;
        /// <summary>冷射在空中點火時噴口周圍的煙環顆數。</summary>
        public int airIgnitionRing = 8;
        /// <summary>助推器燒完時掉落的殘骸片數（用攔截特效的主殘骸）。</summary>
        public int boosterDebris;

        // ---- 照明（§4.5） ----
        /// <summary>點火後發射點的地面光暈直徑（熱射；冷射 × 0.4）。</summary>
        public float groundGlow = 10f;
        public int groundGlowTicks = 90;
    }
}
