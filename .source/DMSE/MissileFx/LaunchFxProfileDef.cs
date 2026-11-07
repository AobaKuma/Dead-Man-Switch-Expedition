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

        // ---- 轉向：RCS 側推／反推與轉向推進器拋棄（§3.6，Oniks） ----
        /// <summary>啟用側推轉向：依實際機頭角速度在彈體前後點放 RCS 噴嘴（0 = 停用）。</summary>
        public bool turnJets;
        /// <summary>角速度（度/tick）超過這個值才噴；低於它是細微擺動，不該一直冒煙。</summary>
        public float turnJetRate = 0.6f;
        /// <summary>側推噴嘴的火焰長度（推力 1 時，格）。</summary>
        public float turnJetLength = 2.4f;
        public float turnJetWidth = 0.8f;
        public Color turnJetColor = new Color(1f, 0.93f, 0.72f, 1f);
        /// <summary>煞停（角加速度與角速度反向）時的火焰倍率：反推噴嘴比轉向噴嘴小。</summary>
        public float turnJetBrakeScale = 0.75f;
        /// <summary>轉向噴嘴每 tick 冒出的煙顆數（0 = 不冒煙）。</summary>
        public int turnJetPuffs = 2;
        /// <summary>側推「點放」：每次噴 <c>turnJetPulseTicks</c> tick、停 <c>turnJetPulseGapTicks</c> tick（0 = 連續噴）。煞停的反推不受影響，是一次短噴。</summary>
        public int turnJetPulseTicks = 3;
        public int turnJetPulseGapTicks = 8;
        /// <summary>每次點放「開頭」額外補幾顆煙（讓每一次噴出都留下一團明顯的白煙，像參考影片的六次助推）。</summary>
        public int turnJetPulsePuffs = 3;
        /// <summary>點放次數上限（6 = 參考影片的六顆小助推；煞停且已噴滿 3 下也可以提早結束）。</summary>
        public int turnJetPulses = 6;
        /// <summary>機頭噴嘴距彈體畫面中心的前方距離（格）：對齊 skyFaller 貼圖的鼻尖。</summary>
        public float turnJetNoseCells = 1.85f;
        /// <summary>機尾噴嘴距畫面中心的後方距離（格）：對齊貼圖的助推器底。</summary>
        public float turnJetTailCells = 1.81f;
        /// <summary>強制開始轉向段的 tick（自點火起；配合角速度觸發，取先到者）。Oniks 的升空交接約在 55。</summary>
        public int turnStartTick = 48;
        /// <summary>轉向段最短 tick：避免角速度一越過門檻就判定「已煞停」。</summary>
        public int turnMinTicks = 8;
        /// <summary>轉向段最長 tick：時間到就拋棄轉向推進器，不管有沒有煞停（安全上限）。</summary>
        public int turnMaxTicks = 90;
        /// <summary>開始煞停後，還要在反推噴嘴上停留幾 tick 才拋棄轉向推進器（讓「反推止轉」看得見）。</summary>
        public int turnBrakeTicks = 5;
        /// <summary>轉向推進器的貼圖（空 = 預設 TurnPod；Oniks 用自己鼻錐的切片）。</summary>
        public string turnPodTexPath;
        /// <summary>機頭前方的轉向推進器：長度、寬度（格；貼圖機頭朝北）。</summary>
        public float turnPodLength = 1.1f;
        public float turnPodWidth = 0.55f;
        /// <summary>轉向推進器掛在機頭前方多遠（自彈體中心，格）。</summary>
        public float turnPodOffset = 2.45f;
        /// <summary>拋棄時往前推的速度（格/tick，疊在彈體速度上）：推進器會先領先彈體，再被追上。</summary>
        public float turnPodEjectSpeed = 0.16f;
        /// <summary>拋棄後的自轉（度/tick）。</summary>
        public float turnPodSpin = 10f;
        /// <summary>拋棄後的存留 tick（到時淡出移除；純視覺，不存檔）。</summary>
        public int turnPodLifeTicks = 100;
        /// <summary>轉向推進器拋棄後的主推力（1 = 全開）與漸增 tick 數。</summary>
        public float mainThrust = 1f;
        public int mainThrustRampTicks = 12;
        /// <summary>主推力全開時的尾焰放大倍率（§3.6：尾焰變成大團白橘色）。</summary>
        public float mainPlumeScale = 1.3f;
        /// <summary>主推力全開時的火焰顏色（由 <see cref="flameColor"/> 漸變）。</summary>
        public Color mainFlameColor = new Color(1f, 0.82f, 0.55f, 1f);

        // ---- 轉向重新設計 R（docs/導彈轉向特效_實作規格.md §11：好讀、誇張） ----
        /// <summary>每一下點放開頭，每個噴嘴噴出的氣團顆數（0 = 沿用舊版每 tick 小煙）。</summary>
        public int turnJetBurstPuffs;
        /// <summary>氣團初速（格/tick，沿排氣方向；阻力讓它衝出去後停成一團）。</summary>
        public float turnJetBurstSpeed = 0.24f;
        public FloatRange turnJetBurstSize = new FloatRange(0.6f, 1f);
        /// <summary>煙柱大小計算用的最低推力（0 = 照實際推力）：助推器分離後的續航段煙跡才不會細成一串點。</summary>
        public float smokeThrustFloor;
        /// <summary>主推力全開後煙跡放大倍率與變白比例。</summary>
        public float mainSmokeScale = 1f;
        public float mainSmokeWhiten;
        /// <summary>主推力全開後煙團往後噴出的初速倍率（<see cref="exhaustSpeed"/> × (1 + (此值 − 1) × main)；1 = 不變）。</summary>
        public float mainExhaustScale = 1f;
        /// <summary>主推力點火瞬間的光球直徑（格，0 = 不畫）與持續 tick；另外噴出 <see cref="mainBloomPuffs"/> 顆熱煙。</summary>
        public float mainBloomSize;
        public int mainBloomTicks = 30;
        public int mainBloomPuffs;
        /// <summary>主推力全開時尾焰寬度的額外倍率（舊版固定 0.55）。</summary>
        public float mainPlumeWiden = 0.55f;
        /// <summary>主推力全開時噴口光暈的額外倍率（直徑 × (1 + 此值 × main)；0 = 舊行為）。</summary>
        public float mainHaloScale;
        /// <summary>拋棄瞬間在接縫噴出的氣環顆數（舊版固定 8 顆小煙）。</summary>
        public int jettisonRingPuffs = 8;
        /// <summary>拋棄後轉向段往側面飄離的速度（格/tick，往最後一次轉向的外側）與自己的殘焰 tick 數（期間拖小煙）。</summary>
        public float turnPodDrift;
        public int turnPodBurnTicks = 24;
    }
}
