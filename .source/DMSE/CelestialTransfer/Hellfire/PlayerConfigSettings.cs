using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public class PlayerConfigSettings : ModSettings
    {
        public List<ImpactCraterRecord> records;
        public bool showBVRWorldCoverage;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref showBVRWorldCoverage, "showBVRWorldCoverage");
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && records == null)
                records = new List<ImpactCraterRecord>();
        }
    }
}