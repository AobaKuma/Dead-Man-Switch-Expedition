using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 目前地圖是未摧毀的導彈陣地時，列出剩餘的必要目標（雷達與世界選靶發射器，見 <see cref="MapComponent_MissileBase"/>）；
    /// 點擊依序跳到各目標。
    /// </summary>
    public class Alert_MissileBaseObjectives : Alert
    {
        private readonly List<Thing> culprits = new List<Thing>();
        private int radars, launchers;

        public Alert_MissileBaseObjectives()
        {
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "DMSE.MissileBase.ObjectivesAlert".Translate(radars, launchers);

        public override TaggedString GetExplanation() => "DMSE.MissileBase.ObjectivesAlertDesc".Translate(radars, launchers);

        public override AlertReport GetReport()
        {
            culprits.Clear();
            radars = launchers = 0;
            MapComponent_MissileBase comp = Find.CurrentMap?.GetComponent<MapComponent_MissileBase>();
            if (comp == null || !comp.ShowsObjectives) { return AlertReport.Inactive; }
            foreach (Building b in comp.RemainingRadars) { culprits.Add(b); radars++; }
            foreach (Building b in comp.RemainingLaunchers) { culprits.Add(b); launchers++; }
            return AlertReport.CulpritsAre(culprits);
        }
    }
}
