using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 某陣營對某航跡的已知情報（§4、§5.1）。多個陣營各自持有，不因玩家雷達優先而讓 NPC 的警報消失。
    /// 只在本陣營節點實際看到時更新；失聯後只能使用最後已知資料。
    /// </summary>
    public class AirDefenseObservation : IExposable
    {
        public Faction faction;
        public int firstSeenTick = -1;
        public int lastSeenTick = -1;
        public Vector3 lastPosition;
        public PlanetTile knownDestination = PlanetTile.Invalid;
        /// <summary>已知的目標陣營；改靶後重新評估。</summary>
        public Faction knownTargetFaction;
        /// <summary>已入帳過「過境」事件的據點（每航跡每據點一次）。</summary>
        public List<int> reportedSites = new List<int>();
        /// <summary>已對此目的地入帳過「瞄準己方」事件；改靶後重置。</summary>
        public PlanetTile targetedReportedFor = PlanetTile.Invalid;

        public bool SeenAt(int tick) => lastSeenTick >= 0 && lastSeenTick >= tick;
        public bool Valid(int now) => lastSeenTick >= 0 && now - lastSeenTick <= AirDefenseAlertTuningDef.Current.intelExpiryTicks;

        public void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref firstSeenTick, "firstSeenTick", -1);
            Scribe_Values.Look(ref lastSeenTick, "lastSeenTick", -1);
            Scribe_Values.Look(ref lastPosition, "lastPosition");
            Scribe_Values.Look(ref knownDestination, "knownDestination", PlanetTile.Invalid);
            Scribe_References.Look(ref knownTargetFaction, "knownTargetFaction");
            Scribe_Collections.Look(ref reportedSites, "reportedSites", LookMode.Value);
            Scribe_Values.Look(ref targetedReportedFor, "targetedReportedFor", PlanetTile.Invalid);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { reportedSites ??= new List<int>(); }
        }
    }
}
