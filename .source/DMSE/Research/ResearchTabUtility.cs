using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// Helper utility for determining dual-slot research tab behavior.
    /// Centralizes the logic for identifying which research tabs should use Anomaly-style dual-slot UI.
    /// </summary>
    public static class ResearchTabUtility
    {
        /// <summary>
        /// Defnames of research tabs that should use dual-slot (Basic/Advanced) UI.
        /// Add your custom tab defNames here to enable dual-slot behavior.
        /// </summary>
        private static readonly string[] UniqueTabDefNames = new[]
        {
            "DMSE_ExpeditionalKnowledge"        // Custom example tab
        };

        /// <summary>
        /// Determines whether a given research tab should display dual-slot (multi-category) UI.
        /// This no longer requires the Anomaly DLC to be active. Instead it will return true
        /// if the ResearchTabDef either has a ModExtension_DualSlotResearchTab or its defName
        /// appears in the fallback DualSlotTabDefNames list.
        /// </summary>
        public static bool ShouldUseUniqueTabUI(ResearchTabDef tabDef)
        {
            if (tabDef == null)
            {
                return false;
            }

            // If the tab has our mod extension, enable multi-category UI
            var ext = tabDef.GetModExtension<ModExtension_UniqueResearchTab>();
            if (ext != null && ext.categories != null && ext.categories.Count > 0)
            {
                return true;
            }

            // Fallback: older approach using defName list
            return IsUniqueTab(tabDef);
        }

        /// <summary>
        /// Internal method: checks if a tab defName is in the dual-slot list.
        /// </summary>
        private static bool IsUniqueTab(ResearchTabDef tabDef)
        {
            foreach (string dualSlotDefName in UniqueTabDefNames)
            {
                if (tabDef.defName == dualSlotDefName)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Gets the number of active project slots for a research tab.
        /// </summary>
        /// <param name="tabDef">The research tab definition.</param>
        /// <returns>2 if dual-slot, 1 if single-slot.</returns>
        public static int GetProjectSlotCount(ResearchTabDef tabDef)
        {
            // If mod extension present, use number of categories defined there
            if (tabDef != null)
            {
                var ext = tabDef.GetModExtension<ModExtension_UniqueResearchTab>();
                if (ext != null && ext.categories != null && ext.categories.Count > 0)
                {
                    return ext.categories.Count;
                }
            }

            return ShouldUseUniqueTabUI(tabDef) ? 2 : 1;
        }

        /// <summary>
        /// Returns the knowledge categories to display for a multi-category research tab.
        /// If no mod extension is defined, returns Basic/Advanced for legacy dual-slot tabs.
        /// </summary>
        public static List<KnowledgeCategoryDef> GetCategoriesForTab(ResearchTabDef tabDef)
        {
            var list = new List<KnowledgeCategoryDef>();
            if (tabDef == null)
            {
                return list;
            }

            var ext = tabDef.GetModExtension<ModExtension_UniqueResearchTab>();
            if (ext != null && ext.categories != null && ext.categories.Count > 0)
            {
                list.AddRange(ext.categories);
                return list;
            }

            // Legacy fallback: Anomaly style Basic/Advanced
            if (IsUniqueTab(tabDef))
            {
                list.Add(KnowledgeCategoryDefOf.Basic);
                list.Add(KnowledgeCategoryDefOf.Advanced);
            }

            return list;
        }

        /// <summary>
        /// Registers a custom research tab for dual-slot UI support.
        /// Call this in your mod's initialization to add your custom tab.
        /// </summary>
        /// <param name="defName">The defName of the ResearchTabDef to register.</param>
        public static void RegisterDualSlotTab(string defName)
        {
            // Note: This is a simple approach. In production, consider using a more robust collection.
            // For now, just document that tabs should be added to DualSlotTabDefNames array above.
            Log.Message($"[DMSE] Attempted to register dual-slot tab: {defName}. Please add to DualSlotTabDefNames array in ResearchTabUtility.cs");
        }

        /// <summary>
        /// Validates that required knowledge categories exist for dual-slot tabs.
        /// Call during mod initialization for debugging.
        /// </summary>
        public static void ValidateUniqueTabDefs()
        {
            // Verify Basic and Advanced categories exist for legacy fallback
            if (KnowledgeCategoryDefOf.Basic == null)
            {
                Log.Error("[DMSE] KnowledgeCategoryDefOf.Basic not found!");
            }

            if (KnowledgeCategoryDefOf.Advanced == null)
            {
                Log.Error("[DMSE] KnowledgeCategoryDefOf.Advanced not found!");
            }

            Log.Message("[DMSE] Dual-slot research UI check complete. Registered tabs: " + string.Join(", ", UniqueTabDefNames));
        }
    }
}