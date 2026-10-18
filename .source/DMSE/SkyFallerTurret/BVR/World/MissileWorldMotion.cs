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
        public static float DetectionRange(float range, int antiStealth, BVRTargetProps target)
        {
            BVRWorldTuningDef tuning = BVRWorldTuningDef.Current;
            int gap = antiStealth - target.stealthLevel;
            float factor = gap < 0 ? tuning.stealthDetectFactor
                : Mathf.Min(1f + tuning.antiStealthRangeBonusPerLevel * gap, tuning.maxAntiStealthRangeMultiplier);
            return Mathf.Max(0f, range * target.distanceLevel * factor);
        }
    }
}
