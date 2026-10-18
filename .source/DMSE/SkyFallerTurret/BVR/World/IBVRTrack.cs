using RimWorld;
using RimWorld.Planet;
using UnityEngine;

namespace DMSE
{
    public interface IBVRTrack
    {
        WorldObject Self { get; }
        Faction Attacker { get; }
        PlanetTile Origin { get; }
        PlanetTile Destination { get; }
        BVRTargetProps TargetProps { get; }
        MissileConfig Config { get; }
        float AngularSpeed { get; }
        int TicksToArrival { get; }
        Vector3 PositionAt(int ticksAhead);
        BVRTrackState State { get; }
        void Notify_Intercepted(Faction by);
    }
}
