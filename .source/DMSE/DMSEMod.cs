using Verse;
using UnityEngine;

namespace DMSE
{
    public class DMSEMod : Mod
    {
        internal static PlayerConfigSettings Settings;

        public DMSEMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PlayerConfigSettings>();
            ImpactCraterUtility.Initialize(Settings);
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("DMSE.BVR.World.ShowCoverage".Translate(), ref Settings.showBVRWorldCoverage,
                "DMSE.BVR.World.ShowCoverageDesc".Translate());
            listing.CheckboxLabeled("DMSE.Strike.Settings.WaveReadyAlert".Translate(), ref Settings.strikeWaveReadyAlert,
                "DMSE.Strike.Settings.WaveReadyAlertDesc".Translate());
            listing.End();
        }

        public override string SettingsCategory() => "DMSE";
    }
}