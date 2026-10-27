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
        /// <summary>攔截殘骸落地造成小額鈍傷（docs/攔截殘骸特效_實作與技術美術計畫.md §1-3，預設關閉：只有視覺）。</summary>
        public bool interceptDebrisDamage;
        /// <summary>攔截特效密度：0 = 低、1 = 中（預設）、2 = 高（§8）。</summary>
        public int interceptFxDensity = 1;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref allowSpaceTraversal, "allowSpaceTraversal", false);
            Scribe_Values.Look(ref showBVRWorldCoverage, "showBVRWorldCoverage");
            Scribe_Values.Look(ref strikeWaveReadyAlert, "strikeWaveReadyAlert", true);
            Scribe_Values.Look(ref godModeInstantAssembly, "godModeInstantAssembly", true);
            Scribe_Values.Look(ref interceptDebrisDamage, "interceptDebrisDamage", false);
            Scribe_Values.Look(ref interceptFxDensity, "interceptFxDensity", 1);
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && records == null)
                records = new List<ImpactCraterRecord>();
        }
    }
}
