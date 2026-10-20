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

        /// <summary>箔條／干擾絲剩餘次數；-1 = 尚未初始化（第一次掃描時由導彈設定填入）。</summary>
        public int chaffLeft = -1;

        /// <summary>箔條成功斷鎖後，防空重新鎖定的延遲截止 tick。</summary>
        public int lockDelayUntil;

        /// <summary>已觸發的干擾次數（含失敗），僅供檢視與測試。</summary>
        public int jamCount;
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
            Scribe_Values.Look(ref chaffLeft, "chaffLeft", -1);
            Scribe_Values.Look(ref lockDelayUntil, "lockDelayUntil", 0);
            Scribe_Values.Look(ref jamCount, "jamCount", 0);
        }
    }
}
