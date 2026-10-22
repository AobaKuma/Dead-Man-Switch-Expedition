using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public class PlayerConfigSettings : ModSettings
    {
        public List<ImpactCraterRecord> records;
        public bool showBVRWorldCoverage;
        public bool allowSpaceTraversal;
        public bool strikeWaveReadyAlert = true;
        public bool godModeInstantAssembly = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref allowSpaceTraversal, "allowSpaceTraversal", false);
            Scribe_Values.Look(ref showBVRWorldCoverage, "showBVRWorldCoverage");
            Scribe_Values.Look(ref strikeWaveReadyAlert, "strikeWaveReadyAlert", true);
            Scribe_Values.Look(ref godModeInstantAssembly, "godModeInstantAssembly", true);
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && records == null)
                records = new List<ImpactCraterRecord>();
        }
    }
}
