using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 結構樁相關的圖示、疊加層材質與顏色。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PileResources
    {
        public static readonly Texture2D BuildBeamIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/BuildBeam");

        public static readonly Texture2D DismantleBeamIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/DismantleBeam");

        public static readonly Texture2D CancelBeamIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/CancelBeam");

        public static readonly Texture2D LinkSelectedIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/LinkSelectedPiles");

        public static readonly Texture2D SelectNetworkIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/SelectNetwork");

        public static readonly Texture2D PlanTransferIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/PlanTransfer");

        public static readonly Texture2D AbortTransferIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/AbortTransfer");

        public static readonly Texture2D LaunchTransferIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/LaunchTransfer");

        public static readonly Texture2D ShowFootprintIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/ShowFootprint");

        public static readonly Texture2D PlanImpactIcon = ContentFinder<Texture2D>.Get("UI/DMSE/Transfer/PlanImpact");

        public static readonly Color FootprintColor = new Color(0.55f, 0.85f, 1f);

        public static readonly Color ExhaustZoneColor = new Color(1f, 0.6f, 0.15f);

        public static readonly Color CoverageGhostColor = new Color(0.55f, 0.85f, 1f);

        public static readonly Color LinkRangeColor = new Color(1f, 1f, 1f, 0.35f);

        public static readonly Color ContestedColor = ColorLibrary.Orange;

        public static readonly Color TornApartColor = Color.red;

        public static readonly Color UnlinkedPileColor = Color.yellow;

        /// <summary>已完工連結（選取時在地面上層顯示，讓有地板處也看得到）。</summary>
        public static readonly Material LinkLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(1f, 1f, 1f, 0.85f));

        /// <summary>選取目標時，範圍內可連結的候選樁。</summary>
        public static readonly Material CandidateLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(1f, 1f, 1f, 0.25f));

        public static readonly Material ValidTargetLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(0.3f, 1f, 0.3f, 0.9f));

        public static readonly Material InvalidTargetLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(1f, 0.25f, 0.25f, 0.9f));

        /// <summary>待施工鋼樑（藍圖色）。</summary>
        public static readonly Material PendingBuildLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(0.45f, 0.72f, 1f, 0.55f));

        /// <summary>施工進度。</summary>
        public static readonly Material BuildProgressLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(0.8f, 0.92f, 1f, 0.9f));

        /// <summary>待拆除鋼樑。</summary>
        public static readonly Material PendingDismantleLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(1f, 0.55f, 0.2f, 0.6f));

        public static float OverlayAltitude => AltitudeLayer.MetaOverlays.AltitudeFor();
    }
}
