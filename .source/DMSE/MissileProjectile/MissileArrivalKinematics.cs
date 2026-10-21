using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 落地導彈的運動學（docs/Missile_Skyfaller轉Projectile_計畫.md §8.4）。
    ///
    /// 終端墜落沿用原版 Skyfaller 公式（<see cref="FallDrawPos"/>）：畫面位置 = 落點 + 方位 angle 方向 × 距離(ticksToImpact)。
    /// 原版 skyfaller 的 angle 與世界方位同一套羅盤角（見 <see cref="MissileMapEntryUtility"/>），所以把 angle 設成
    /// 世界航跡最後一段的進入方位，彈體就從那個方向飛來；再把起始 ticksToImpact 取成「起點恰好在地圖邊緣外」
    /// （<see cref="EntryTicks"/>），彈體就會從進入方位那一側的地圖邊緣飛入、直線飛向落點，速度與 speedCurve 完全照原公式。
    /// 沒有進入方位（跨圖層、除錯生成、BVR 測試齊射）時照原本：angle = -33.7、ticksToImpactRange 隨機。
    /// </summary>
    public static class MissileArrivalKinematics
    {
        /// <summary>起點落在地圖邊緣外多遠（格），讓彈體在畫面外出現、飛入地圖。</summary>
        public const float EntryMarginCells = 2f;

        /// <summary>搜尋起始 tick 的上限（約 650 格外，任何地圖都夠）。</summary>
        public const int MaxEntryTicks = 600;

        /// <summary>墜落段的原版 Skyfaller.DrawPos（正向：TimeInAnimation = 1 - ticksToImpact / ticksToImpactMax）。</summary>
        public static Vector3 FallDrawPos(MissileFlightExtension f, Vector3 target, int ticksToImpact, int ticksToImpactMax, float angle)
        {
            float t = 1f - (float)ticksToImpact / Mathf.Max(1, ticksToImpactMax);
            float speed = f.speedCurve == null ? f.speed : f.speedCurve.Evaluate(t) * f.speed;
            bool flip = f.flightFlippedHorizontally;
            switch (f.movementType)
            {
                case SkyfallerMovementType.ConstantSpeed:
                    return SkyfallerDrawPosUtility.DrawPos_ConstantSpeed(target, ticksToImpact, angle, speed, flip);
                case SkyfallerMovementType.Decelerate:
                    return SkyfallerDrawPosUtility.DrawPos_Decelerate(target, ticksToImpact, angle, speed, flip);
                default:
                    return SkyfallerDrawPosUtility.DrawPos_Accelerate(target, ticksToImpact, angle, speed, flip);
            }
        }

        /// <summary>從方位 <paramref name="heading"/> 飛入時，落點到地圖邊緣（含 <see cref="EntryMarginCells"/>）的距離。</summary>
        public static float EntryDistance(IntVec3 mapSize, IntVec3 target, float heading)
        {
            Vector3 c = target.ToVector3Shifted();
            c.y = 0f;
            Vector3 edge = MissileMapEntryUtility.EdgePoint(mapSize, c, heading);
            edge.y = 0f;
            return (edge - c).magnitude + EntryMarginCells;
        }

        /// <summary>
        /// 從 <paramref name="heading"/> 飛入落點 <paramref name="target"/> 的墜落長度（tick）：
        /// 起點恰好在地圖邊緣外的最小值，且不短於原本的 ticksToImpactRange 下限（太近時起點更遠，仍從同一側邊緣飛入）。
        /// </summary>
        public static int EntryTicks(MissileFlightExtension f, IntVec3 mapSize, IntVec3 target, float heading)
        {
            Vector3 c = target.ToVector3Shifted();
            c.y = 0f;
            float distance = EntryDistance(mapSize, target, heading);
            float angle = MissileMapEntryUtility.SkyfallerAngleForHeading(heading);
            int min = Mathf.Max(1, f.ticksToImpactRange.min);
            for (int ticks = 1; ticks <= MaxEntryTicks; ticks++)
            {
                Vector3 p = FallDrawPos(f, c, ticks, ticks, angle);
                p.y = 0f;
                if ((p - c).magnitude >= distance) { return Mathf.Max(ticks, min); }
            }
            return Mathf.Max(MaxEntryTicks, min);
        }
    }
}
