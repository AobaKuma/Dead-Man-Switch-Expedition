using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>打擊任務的隨機名稱（RulePackDef <c>DMSE_NamerStrikeMission</c>），避開進行中任務已用的名稱。</summary>
    public static class StrikeMissionNamer
    {
        /// <summary>產生一個沒有被進行中任務使用、也不等於 <paramref name="avoid"/>（重擲時的目前名稱）的名稱。</summary>
        public static string RandomName(string avoid = null)
        {
            RulePackDef pack = DMSE_StrikeDefOf.DMSE_NamerStrikeMission;
            if (pack == null) { return null; }
            HashSet<string> used = new HashSet<string>();
            if (!avoid.NullOrEmpty()) { used.Add(avoid); }
            foreach (Map map in Find.Maps)
            {
                MapComponent_StrikePlanner planner = map.GetComponent<MapComponent_StrikePlanner>();
                if (planner == null) { continue; }
                foreach (CompStrikeConsole console in planner.consoles)
                {
                    string name = console.mission?.name;
                    if (!name.NullOrEmpty()) { used.Add(name); }
                }
            }
            return NameGenerator.GenerateName(pack, used, false, "r_name");
        }
    }
}
