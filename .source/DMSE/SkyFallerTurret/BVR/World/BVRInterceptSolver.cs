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
    }
}
