using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 每張地圖上已生成的飛彈平台（<see cref="IMissileRack"/>）與組裝台註冊表，不存檔：平台與組裝台在生成時自行登記。
    ///
    /// 存在的理由是效能：搬運／裝填／裝配的 WorkGiver 以前每次找工作都掃描殖民地全部建築，
    /// 裝填工作還對每座未滿的發射器再掃一次（工作坊回報：蓋了 96 座以上垂發後明顯卡頓）。
    /// 陣營可能在生成後改變（佔領敵方陣地），所以這裡收所有陣營，呼叫端自己過濾。
    /// </summary>
    public class MapComponent_MissileRacks : MapComponent
    {
        private readonly List<IMissileRack> racks = new List<IMissileRack>();
        private readonly List<CompMissileAssemblyBench> benches = new List<CompMissileAssemblyBench>();

        public MapComponent_MissileRacks(Map map) : base(map) { }

        /// <summary>依生成順序排列的所有平台（含其他陣營）。</summary>
        public IReadOnlyList<IMissileRack> Racks => racks;

        /// <summary>所有組裝台（含其他陣營）。</summary>
        public IReadOnlyList<CompMissileAssemblyBench> Benches => benches;

        private static Map cachedMap;
        private static MapComponent_MissileRacks cachedComp;

        public static MapComponent_MissileRacks For(Map map)
        {
            if (map == null) { return null; }
            if (cachedMap != map || cachedComp == null || cachedComp.map != map)
            {
                cachedComp = map.GetComponent<MapComponent_MissileRacks>();
                cachedMap = map;
            }
            return cachedComp;
        }

        public static void Register(IMissileRack rack, Map map)
        {
            MapComponent_MissileRacks comp = For(map);
            if (comp != null && rack != null && !comp.racks.Contains(rack)) { comp.racks.Add(rack); }
        }

        public static void Deregister(IMissileRack rack, Map map)
        {
            For(map)?.racks.Remove(rack);
        }

        public static void Register(CompMissileAssemblyBench bench, Map map)
        {
            MapComponent_MissileRacks comp = For(map);
            if (comp != null && bench != null && !comp.benches.Contains(bench)) { comp.benches.Add(bench); }
        }

        public static void Deregister(CompMissileAssemblyBench bench, Map map)
        {
            For(map)?.benches.Remove(bench);
        }
    }
}
