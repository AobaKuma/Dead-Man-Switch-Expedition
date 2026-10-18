using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public class BVRWorldTuningDef : Def
    {
        public float ticksPerTileAtSpeed1 = 150f;
        public float interceptorSpeedMultiplier = 3f;
        public float stealthDetectFactor = 0.4f;
        public float antiStealthRangeBonusPerLevel = 0.25f;
        public float maxAntiStealthRangeMultiplier = 1.5f;
        public int minInterceptMarginTicks = 60;
        public float airburstRadiusTiles = 1f;
        public int scanIntervalTicks = 15;
        /// <summary>Strike guidance period: a launched strike missile holds a fire-control channel this long.</summary>
        public int strikeGuidanceTicks = 600;
        /// <summary>Wave strike: suggested arrival gap after the previous wave when a wave would arrive too early.</summary>
        public int strikeArrivalSpacingTicks = 60;
        /// <summary>Wave strike: firing solutions are recomputed once the target has moved this many tiles (0 = never).</summary>
        public float strikeRecomputeMoveTiles = 5f;
        public static BVRWorldTuningDef Current => DefDatabase<BVRWorldTuningDef>.GetNamedSilentFail("DMSE_BVRWorldTuning") ?? fallback;
        private static readonly BVRWorldTuningDef fallback = new BVRWorldTuningDef();
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (ticksPerTileAtSpeed1 <= 0 || interceptorSpeedMultiplier <= 0 || scanIntervalTicks < 1
                || minInterceptMarginTicks < 0 || airburstRadiusTiles < 0 || strikeGuidanceTicks < 0)
                yield return "BVR motion, interval and range values must be positive (margin/radius may be zero).";
        }
    }
}
