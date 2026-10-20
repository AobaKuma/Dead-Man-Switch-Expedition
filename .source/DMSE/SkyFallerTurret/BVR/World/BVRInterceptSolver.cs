using UnityEngine;
using Verse;

namespace DMSE
{
    public static class BVRInterceptSolver
    {
        // Point defense: both objects follow the same great circle towards one another.
        public static bool TrySolve(IBVRTrack track, Vector3 origin, float interceptorSpeed, out int ticks, out Vector3 point)
        {
            float distance = GenMath.SphericalDistance(origin.normalized, track.PositionAt(0).normalized);
            ticks = Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Max(0.0000001f, track.AngularSpeed + interceptorSpeed)));
            point = track.PositionAt(ticks);
            return ticks < track.TicksToArrival - BVRWorldTuningDef.Current.minInterceptMarginTicks;
        }

        /// <summary>
        /// Area defense: the target may cross the node's envelope instead of flying at it, so the head-on
        /// solution above does not apply. Solve g(t) = angle(origin, target(t)) - ω_i·t = 0 by bisection
        /// over [0, ETA]; g(0) > 0 and a hit exists exactly when g(ETA) ≤ 0.
        /// </summary>
        public static bool TrySolveGeneral(IBVRTrack track, Vector3 origin, float interceptorSpeed, out int ticks, out Vector3 point)
        {
            ticks = 0;
            point = origin;
            int eta = track.TicksToArrival;
            float speed = Mathf.Max(0.0000001f, interceptorSpeed);
            if (eta <= 0) { return false; }

            float Gap(int ahead)
                => GenMath.SphericalDistance(origin.normalized, track.PositionAt(ahead).normalized) - speed * ahead;

            if (Gap(eta) > 0f) { return false; }   // the target outruns the interceptor

            int lo = 0, hi = eta;
            for (int i = 0; i < 24 && hi - lo > 1; i++)
            {
                int mid = lo + (hi - lo) / 2;
                if (Gap(mid) <= 0f) { hi = mid; } else { lo = mid; }
            }
            ticks = hi;
            point = track.PositionAt(hi);
            return ticks < eta - BVRWorldTuningDef.Current.minInterceptMarginTicks;
        }
    }
}
