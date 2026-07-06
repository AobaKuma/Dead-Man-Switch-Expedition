using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>階段一：搜索雷達。提供預警並計算攔截窗口。</summary>
    public class CompProperties_SearchRadar : CompProperties
    {
        /// <summary>搜索距離：越大偵測範圍越廣、預警窗口越長。</summary>
        public int searchDistance = 100;

        /// <summary>功率等級：放大預警窗口（對應設計圖 Σ(搜索距離 × 功率)）。</summary>
        public int powerLevel = 1;

        /// <summary>反隱身等級：需 >= 目標隱身值才能完整偵測，否則窗口打折。</summary>
        public int antiStealthLevel = 0;

        /// <summary>每「搜索距離」單位換算的窗口 ticks。</summary>
        public float ticksPerDistance = 12f;

        /// <summary>
        /// 是否不受反輻射導引頭（<see cref="GuidanceType_AntiRadiation"/>）的目標選擇影響。
        /// 設為 true 時，此雷達不會被列入反輻射制導的候選目標清單（例如純被動聲學早期預警裝置，
        /// 不發射任何主動電磁波，理論上無法被反輻射導引頭偵測鎖定）。
        /// </summary>
        public bool immuneToAntiRadiationSeeker = false;

        public CompProperties_SearchRadar()
        {
            compClass = typeof(CompSearchRadar);
        }
    }

    public class CompSearchRadar : CompBVRDevice
    {
        public CompProperties_SearchRadar Props => (CompProperties_SearchRadar)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_BVRCombat m = Manager;
            if (m != null) { m.searchRadars.Add(this); }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_BVRCombat m = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (m != null) { m.searchRadars.Remove(this); }
        }

        public override string CompInspectStringExtra()
        {
            return "DMSE.BVR.SearchRadar".Translate(Props.searchDistance, Props.antiStealthLevel,
                Active ? "DMSE.BVR.Online".Translate() : "DMSE.BVR.Offline".Translate());
        }
    }
}
