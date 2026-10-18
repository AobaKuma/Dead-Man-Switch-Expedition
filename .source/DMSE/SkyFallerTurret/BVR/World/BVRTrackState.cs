using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public class BVRTrackState : IExposable
    {
        public Faction detectedByFaction;
        public bool playerNotified;
        public int firstDetectedTick = -1;
        public int lockUntil = -1;
        public MapParent lockNode;
        public Faction lockFaction;
        public PlanetTile lockNodeTile = PlanetTile.Invalid;
        public WorldObject_Interceptor interceptor;
        public int interceptAttempts;
        public PlanetTile lastKnownDestination = PlanetTile.Invalid;
        public void ClearLock()
        {
            lockUntil = -1;
            lockNode = null;
            lockFaction = null;
            lockNodeTile = PlanetTile.Invalid;
        }
        public string StatusKey => interceptor != null && !interceptor.Destroyed ? "DMSE.BVR.World.InFlight"
            : lockUntil >= 0 ? "DMSE.BVR.World.Locking"
            : detectedByFaction != null ? "DMSE.BVR.World.Detected" : "DMSE.BVR.World.Undetected";
        public void ExposeData()
        {
            Scribe_References.Look(ref detectedByFaction, "detectedByFaction");
            Scribe_Values.Look(ref playerNotified, "playerNotified");
            Scribe_Values.Look(ref firstDetectedTick, "firstDetectedTick", -1);
            Scribe_Values.Look(ref lockUntil, "lockUntil", -1);
            Scribe_References.Look(ref lockNode, "lockNode");
            Scribe_References.Look(ref lockFaction, "lockFaction");
            Scribe_Values.Look(ref lockNodeTile, "lockNodeTile", PlanetTile.Invalid);
            Scribe_References.Look(ref interceptor, "interceptor");
            Scribe_Values.Look(ref interceptAttempts, "interceptAttempts");
            Scribe_Values.Look(ref lastKnownDestination, "lastKnownDestination", PlanetTile.Invalid);
        }
    }
}
