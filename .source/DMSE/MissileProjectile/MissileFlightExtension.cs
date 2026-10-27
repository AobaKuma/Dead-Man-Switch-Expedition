using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈 Projectile 的飛行動畫參數（docs/Missile_Skyfaller轉Projectile_計畫.md §3.3）。
    /// 欄位名稱、語意與預設值皆與原版 <see cref="SkyfallerProperties"/> 相同，
    /// 原 def 的 <c>&lt;skyfaller&gt;</c> 節點可整段搬進本擴充而不改內容。
    /// 只收錄導彈用得到的欄位（不含爆炸、碎片、生成物——導彈的爆炸由 <see cref="MissileConfig"/> 決定）。
    /// </summary>
    public class MissileFlightExtension : DefModExtension
    {
        // ---- 時間軸 ----
        /// <summary>true = 離場（ticksToImpact 遞增至 ticksToDiscardInReverse 後離開地圖）；false = 落地（遞減至 0 後命中）。</summary>
        public bool reversed;
        public IntRange ticksToImpactRange = new IntRange(120, 200);
        public IntRange ticksToDiscardInReverse = IntRange.Zero;

        // ---- 運動 ----
        public SkyfallerMovementType movementType;
        public float speed = 1f;
        public SimpleCurve speedCurve;
        public SimpleCurve angleCurve;
        public SimpleCurve rotationCurve;
        public SimpleCurve xPositionCurve;
        public SimpleCurve zPositionCurve;
        public bool rotateGraphicTowardsDirection;
        public bool flightFlippedHorizontally;

        // ---- 影子 / 淡入淡出 ----
        public string shadow = "Things/Skyfaller/SkyfallerShadowCircle";
        public Vector2 shadowSize = Vector2.one;
        public int fadeInTicks;
        public int fadeOutTicks;

        // ---- 屋頂 / 落地 ----
        public bool hitRoof = true;
        public bool minimalRoofDestruction;
        /// <summary>落地時每格揚起的塵土數（同 Skyfaller.Impact）。</summary>
        public int motesPerCell = 3;
        public float cameraShake;

        // ---- 離場巡航（docs/Missile_Skyfaller轉Projectile_計畫.md §8.3）----
        // 對地離場彈在升空段中途交接，轉向世界航跡的出發方位，沿 speedCurve 加速飛出地圖邊緣。
        // 攔截彈與沒有水平方位的發射（跨圖層）不交接，照原本跑完升空動畫。

        /// <summary>升空段交接時刻（TimeInAnimation 比例）。</summary>
        public float ascentHandoff = 0.3f;
        /// <summary>
        /// 或彈體畫面位移離發射點達此格數時交接（取先到者），確保交接時仍在畫面附近。
        /// 現有 def 都是這個先到：16 格 ≈ 點火後 55 tick；彈道族（較快）由產生器覆寫為 22 格 ≈ 44 tick（2026-10-26：原 12 格轉得太早）。
        /// </summary>
        public float handoffMaxCells = 16f;
        /// <summary>
        /// 交接後轉向出發方位的最大轉速（度/tick）。轉速從 0 在 <see cref="turnRampTicks"/> 內升到上限，接近目標時再漸減，
        /// 所以大角度轉向比小角度久（原本固定 45 tick 的 smoothstep，150° 轉向峰值達 5°/tick，太急）。彈道族覆寫為 1.2。
        /// </summary>
        public float maxTurnRate = 2f;
        /// <summary>轉速從 0 升到 <see cref="maxTurnRate"/> 的 tick 數。</summary>
        public int turnRampTicks = 15;
        /// <summary>
        /// 最大轉彎半徑（格）：速度快時轉速上限提高到「速度 ÷ 此半徑」，避免巡航段沿 speedCurve 加速後繞出半張地圖、
        /// 從出發方位以外的邊離場（驗收 F5：地圖中央發射、180° 迴轉時曾從西邊出圖）。低速時仍由 maxTurnRate 決定。
        /// </summary>
        public float maxTurnRadius = 25f;
        /// <summary>巡航速度倍率（相對交接瞬間的速度，於轉向期間漸入）。</summary>
        public float cruiseSpeedFactor = 1f;
        /// <summary>巡航速度上下限（格/tick）。</summary>
        public float minCruiseSpeed = 0.4f;
        public float maxCruiseSpeed = 6f;
        /// <summary>巡航最長 tick（安全上限，超過即視為離開地圖）。</summary>
        public int maxCruiseTicks = 1200;

        // ---- 音效 ----
        public SoundDef anticipationSound;
        public int anticipationSoundTicks = 100;
        public SoundDef floatingSound;
        public SoundDef impactSound;

        /// <summary>
        /// 由原版 <see cref="SkyfallerProperties"/> 複製出等值的擴充。
        /// 用於新舊軌跡比對測試與 def 轉換驗證；曲線以參照共用（唯讀使用）。
        /// </summary>
        public static MissileFlightExtension FromSkyfaller(SkyfallerProperties sp)
        {
            if (sp == null) { return null; }
            return new MissileFlightExtension
            {
                reversed = sp.reversed,
                ticksToImpactRange = sp.ticksToImpactRange,
                ticksToDiscardInReverse = sp.ticksToDiscardInReverse,
                movementType = sp.movementType,
                speed = sp.speed,
                speedCurve = sp.speedCurve,
                angleCurve = sp.angleCurve,
                rotationCurve = sp.rotationCurve,
                xPositionCurve = sp.xPositionCurve,
                zPositionCurve = sp.zPositionCurve,
                rotateGraphicTowardsDirection = sp.rotateGraphicTowardsDirection,
                flightFlippedHorizontally = sp.flightFlippedHorizontally,
                shadow = sp.shadow,
                shadowSize = sp.shadowSize,
                fadeInTicks = sp.fadeInTicks,
                fadeOutTicks = sp.fadeOutTicks,
                hitRoof = sp.hitRoof,
                minimalRoofDestruction = sp.minimalRoofDestruction,
                motesPerCell = sp.motesPerCell,
                cameraShake = sp.cameraShake,
                anticipationSound = sp.anticipationSound,
                anticipationSoundTicks = sp.anticipationSoundTicks,
                floatingSound = sp.floatingSound,
                impactSound = sp.impactSound,
                // 巡航欄位用預設值（skyfaller 沒有對應欄位）。
            };
        }
    }
}
