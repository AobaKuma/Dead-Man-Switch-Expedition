using UnityEngine;
using Verse;

namespace DMSE
{
    public static class MissileWorldMotion
    {
        // Invert the game's surface angular-distance conversion; no assumed planet subdivision.
        public static float BaseAngularSpeed => 1f / Mathf.Max(0.001f,
            Find.WorldGrid.ApproxDistanceInTiles(1f) * BVRWorldTuningDef.Current.ticksPerTileAtSpeed1);
        public static float AngularSpeed(MissileConfig config) => BaseAngularSpeed * (config?.WorldSpeedFactor ?? 1f);

        /// <summary>
        /// 高度對偵測距離（以及地圖層預警窗口）的係數：以雷達視距的平方根近似，
        /// 低空目標較晚被發現、高空目標較早被發現；夾在 Tuning 的最小／最大值之間。
        /// </summary>
        public static float AltitudeFactor(float altitude)
        {
            BVRWorldTuningDef tuning = BVRWorldTuningDef.Current;
            float reference = Mathf.Max(1f, tuning.altitudeRefMeters);
            float factor = Mathf.Sqrt(Mathf.Max(0f, altitude) / reference);
            return Mathf.Clamp(factor, tuning.minAltitudeFactor, Mathf.Max(tuning.minAltitudeFactor, tuning.maxAltitudeFactor));
        }

        public static float DetectionRange(float range, int antiStealth, BVRTargetProps target)
        {
            BVRWorldTuningDef tuning = BVRWorldTuningDef.Current;
            int gap = antiStealth - target.stealthLevel;
            float factor = gap < 0 ? tuning.stealthDetectFactor
                : Mathf.Min(1f + tuning.antiStealthRangeBonusPerLevel * gap, tuning.maxAntiStealthRangeMultiplier);
            return Mathf.Max(0f, range * target.distanceLevel * factor * AltitudeFactor(target.altitude));
        }
    }
}
