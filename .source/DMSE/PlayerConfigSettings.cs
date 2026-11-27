using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// DMSE 自己的設定：太空穿越與隕石坑紀錄。飛彈、防空與打擊的設定在 2026-11 隨飛彈體系移到 FFF（FFF_AerospaceSettings），舊值不搬遷。
    /// </summary>
    public class PlayerConfigSettings : ModSettings
    {
        public List<ImpactCraterRecord> records;
        public bool allowSpaceTraversal;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref allowSpaceTraversal, "allowSpaceTraversal", false);
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && records == null)
                records = new List<ImpactCraterRecord>();
        }
    }
}
