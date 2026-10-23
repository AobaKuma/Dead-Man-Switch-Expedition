using System.Collections.Generic;
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

        /// <summary>逐陣營情報（NPC 防空警戒）；<see cref="detectedByFaction"/> 只保留給玩家介面用的代表探測者。</summary>
        public List<AirDefenseObservation> observations = new List<AirDefenseObservation>();

        /// <summary>齊射／攻擊波分組 id（警戒事件去重用）；null = 尚未分組。</summary>
        public string alertWaveId;

        public AirDefenseObservation ObservationOf(Faction faction, bool create = false)
        {
            if (faction == null) { return null; }
            for (int i = 0; i < observations.Count; i++)
            {
                if (observations[i].faction == faction) { return observations[i]; }
            }
            if (!create) { return null; }
            var observation = new AirDefenseObservation { faction = faction };
            observations.Add(observation);
            return observation;
        }

        /// <summary>本陣營在 <paramref name="sinceTick"/> 之後實際看到過這條航跡（本地或同陣營資料鏈）。</summary>
        public bool FactionSees(Faction faction, int sinceTick) => ObservationOf(faction)?.SeenAt(sinceTick) == true;

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
            Scribe_Collections.Look(ref observations, "observations", LookMode.Deep);
            Scribe_Values.Look(ref alertWaveId, "alertWaveId");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                observations ??= new List<AirDefenseObservation>();
                observations.RemoveAll(o => o == null || o.faction == null);
            }
        }
    }
}
