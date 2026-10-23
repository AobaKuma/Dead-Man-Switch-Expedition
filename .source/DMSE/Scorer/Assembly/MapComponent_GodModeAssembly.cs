using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 上帝模式即時裝配（Mod 設定）的補漏掃描：每 250 ticks 把地圖上待裝配的導彈物品、儲存架內的導彈與彈道井直接套用設定。
    /// ITab 編輯與彈道井發射時另有即時呼叫；這裡處理其他來源，例如開啟上帝模式前就待裝配的導彈。
    /// </summary>
    public class MapComponent_GodModeAssembly : MapComponent
    {
        private const int IntervalTicks = 250;

        public MapComponent_GodModeAssembly(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % IntervalTicks != 0 || !MissileAssemblyUtility.GodModeInstant) { return; }
            // 先收集再處理：退回已搬運資源會生成物品
            List<Thing> targets = new List<Thing>();
            List<ThingDef> defs = MissileAssemblyUtility.MissileDefs;
            for (int i = 0; i < defs.Count; i++) { targets.AddRange(map.listerThings.ThingsOfDef(defs[i])); }
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (MissileRackUtil.Of(b) != null || b.TryGetComp<CompMissileLauncher_Ballistic>() != null) { targets.Add(b); }
            }
            for (int i = 0; i < targets.Count; i++) { MissileAssemblyUtility.TryInstantAssemble(targets[i]); }
        }
    }
}
