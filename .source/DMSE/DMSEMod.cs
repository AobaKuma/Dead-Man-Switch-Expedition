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
            listing.CheckboxLabeled("DMSE.Platform.Settings.SpaceTraversal".Translate(), ref Settings.allowSpaceTraversal,
                "DMSE.Platform.Settings.SpaceTraversalDesc".Translate());
            listing.CheckboxLabeled("DMSE.BVR.World.ShowCoverage".Translate(), ref Settings.showBVRWorldCoverage,
                "DMSE.BVR.World.ShowCoverageDesc".Translate());
            listing.CheckboxLabeled("DMSE.Strike.Settings.WaveReadyAlert".Translate(), ref Settings.strikeWaveReadyAlert,
                "DMSE.Strike.Settings.WaveReadyAlertDesc".Translate());
            listing.CheckboxLabeled("DMSE.Missile.Settings.GodModeInstantAssembly".Translate(), ref Settings.godModeInstantAssembly,
                "DMSE.Missile.Settings.GodModeInstantAssemblyDesc".Translate());
            listing.CheckboxLabeled("DMSE.BVR.Settings.InterceptDebrisDamage".Translate(), ref Settings.interceptDebrisDamage,
                "DMSE.BVR.Settings.InterceptDebrisDamageDesc".Translate());
            listing.Label("DMSE.BVR.Settings.InterceptFxDensity".Translate(), tooltip: "DMSE.BVR.Settings.InterceptFxDensityDesc".Translate());
            for (int level = 0; level <= 2; level++)
            {
                if (listing.RadioButton(("DMSE.BVR.Settings.InterceptFxDensity" + level).Translate(), Settings.interceptFxDensity == level, 12f))
                {
                    Settings.interceptFxDensity = level;
                }
            }
            listing.End();
        }

        public override string SettingsCategory() => "DMSE";
    }
}
