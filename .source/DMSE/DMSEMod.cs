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

        // 飛彈、防空與打擊的設定在 FFF 的設定頁（2026-11 隨飛彈體系移入 FFF，FFF_AerospaceSettings）
        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("DMSE.Platform.Settings.SpaceTraversal".Translate(), ref Settings.allowSpaceTraversal,
                "DMSE.Platform.Settings.SpaceTraversalDesc".Translate());
            listing.End();
        }

        public override string SettingsCategory() => "DMSE";
    }
}
