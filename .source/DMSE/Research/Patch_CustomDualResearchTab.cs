using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// Harmony patches for MainTabWindow_Research to support custom dual-slot research tabs.
    /// 
    /// Target: RimWorld.MainTabWindow_Research
    /// Purpose: Extend dual-slot (Basic/Advanced) UI behavior to custom research tabs, not just Anomaly DLC tab.
    /// Risk: Patches use transpiler and prefix methods which are brittle to game version changes.
    /// 
    /// Design approach:
    /// 1. Replace hard-coded checks like "if (ModsConfig.AnomalyActive && curTabInt == ResearchTabDefOf.Anomaly)"
    ///    with calls to ResearchTabUtility.ShouldUseDualSlotUI(curTabInt)
    /// 2. Use transpiler to modify IL bytecode where practical
    /// 3. Use prefix patches where transpiler is too fragile
    /// </summary>
    [HarmonyPatch(typeof(MainTabWindow_Research))]
    public static class Patch_MainTabWindow_Research
    {
        // Cached reflection info for private field access
        private static FieldInfo cachedCurrentTabField = null;
        private static FieldInfo currentTabField =>
            cachedCurrentTabField ?? (cachedCurrentTabField = typeof(MainTabWindow_Research).GetField("curTabInt", BindingFlags.NonPublic | BindingFlags.Instance));

        private static FieldInfo cachedSelectedProjectField = null;
        private static FieldInfo selectedProjectField =>
            cachedSelectedProjectField ?? (cachedSelectedProjectField = typeof(MainTabWindow_Research).GetField("selectedProject", BindingFlags.NonPublic | BindingFlags.Instance));

        private static FieldInfo cachedScrollPositionerField = null;
        private static FieldInfo scrollPositionerField =>
            cachedScrollPositionerField ?? (cachedScrollPositionerField = typeof(MainTabWindow_Research).GetField("scrollPositioner", BindingFlags.NonPublic | BindingFlags.Instance));

        /// <summary>
        /// Helper to safely get current tab from instance.
        /// </summary>
        private static ResearchTabDef GetCurrentTab(MainTabWindow_Research window)
        {
            try
            {
                return currentTabField?.GetValue(window) as ResearchTabDef;
            }
            catch (Exception ex)
            {
                Log.Error($"[DMSE] Failed to get current tab: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Helper to safely get selected project from instance.
        /// </summary>
        private static ResearchProjectDef GetSelectedProject(MainTabWindow_Research window)
        {
            try
            {
                return selectedProjectField?.GetValue(window) as ResearchProjectDef;
            }
            catch (Exception ex)
            {
                Log.Error($"[DMSE] Failed to get selected project: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Patch for UpdateSelectedProject method.
        /// Goal: Apply dual-slot selection logic to custom tabs, not just Anomaly.
        /// 
        /// Original behavior: Only checks Anomaly tab
        /// New behavior: Checks any tab that ShouldUseDualSlotUI returns true for
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("UpdateSelectedProject")]
        public static bool Patch_UpdateSelectedProject(MainTabWindow_Research __instance)
        {
            try
            {
                ResearchTabDef currentTab = GetCurrentTab(__instance);
                ResearchProjectDef selectedProject = null;

                // Use our utility to check if this tab should use dual-slot logic
                if (ResearchTabUtility.ShouldUseUniqueTabUI(currentTab))
                {
                    // For tabs with mod extension, pick first non-null project from defined categories
                    var categories = ResearchTabUtility.GetCategoriesForTab(currentTab);
                    foreach (var cat in categories)
                    {
                        var proj = Find.ResearchManager.GetProject(cat);
                        if (proj != null)
                        {
                            selectedProject = proj;
                            break;
                        }
                    }
                }
                else
                {
                    // Single-slot behavior: standard project selection
                    selectedProject = Find.ResearchManager.GetProject();
                }

                // Update the selectedProject field
                if (selectedProjectField != null)
                {
                    selectedProjectField.SetValue(__instance, selectedProject);
                }

                return false; // Skip original method
            }
            catch (Exception ex)
            {
                Log.Error($"[DMSE] Error in UpdateSelectedProject patch: {ex}");
                return true; // Fall back to original
            }
        }

        // Note: Removed specific GetProjectsPageSize patch because the method may not exist in all game versions.
        // Layout adjustments for multi-category tabs are handled in DrawProjectInfo and DrawDualSlotProjectInfo.

        /// <summary>
        /// Patch for DrawProjectInfo - the most critical patch.
        /// This method displays the active projects and progress bars.
        /// 
        /// We use a prefix to intercept and modify the entire method behavior.
        /// This is necessary because the original has complex branching logic for Anomaly vs normal tabs.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("DrawProjectInfo")]
        public static bool Patch_DrawProjectInfo(MainTabWindow_Research __instance, Rect rect)
        {
            try
            {
                ResearchTabDef currentTab = GetCurrentTab(__instance);
                ResearchProjectDef selectedProject = GetSelectedProject(__instance);

                // Determine number of active project slots
                int numSlots = ResearchTabUtility.GetProjectSlotCount(currentTab);
                
                // Calculate layout dimensions
                float buttonHeight = (numSlots > 1) ? (75f * numSlots) : 100f;
                Rect startButRect = new Rect(
                    rect.center.x - rect.width / 4f,
                    rect.yMax - buttonHeight - 10f - 55f,
                    rect.width / 2f + 20f,
                    55f
                );

                Rect bottomRect = new Rect(0f, rect.yMax - buttonHeight, rect.width, buttonHeight);
                Rect menuRect = bottomRect;
                Rect projectRect = bottomRect.ContractedBy(10f);
                projectRect.y += 5f;

                // Draw header
                Rect headerRect = new Rect(bottomRect.x, bottomRect.y - 30f, rect.width, 28f);
                Text.Font = GameFont.Medium;
                string headerKey = (numSlots > 1) ? "ActiveProjectPlural" : "ActiveProject";
                Widgets.Label(headerRect, headerKey.Translate());
                Text.Font = GameFont.Small;

                // Draw menu background
                Widgets.DrawMenuSection(menuRect);

                // Draw project info based on slot count
                if (numSlots > 1)
                {
                    // Dual-slot: Basic and Advanced
                    DrawDualSlotProjectInfo(__instance, projectRect, currentTab);
                }
                else
                {
                    // Single-slot: Standard behavior
                    DrawSingleSlotProjectInfo(__instance, projectRect, selectedProject);
                }

                // Draw start button
                DrawStartButton(__instance, startButRect);

                // Draw debug buttons if in dev mode
                if (Prefs.DevMode && selectedProject != null && !Find.ResearchManager.IsCurrentProject(selectedProject) && !selectedProject.IsFinished)
                {
                    Text.Font = GameFont.Tiny;
                    if (Widgets.ButtonText(new Rect(rect.xMax - 120f, headerRect.y, 120f, 25f), "Debug: Finish now"))
                    {
                        Find.ResearchManager.SetCurrentProject(selectedProject);
                        Find.ResearchManager.FinishProject(selectedProject);
                    }
                    Text.Font = GameFont.Small;
                }

                if (Prefs.DevMode && selectedProject != null && !selectedProject.TechprintRequirementMet)
                {
                    Text.Font = GameFont.Tiny;
                    if (Widgets.ButtonText(new Rect(rect.xMax - 300f, headerRect.y, 170f, 25f), "Debug: Apply techprint"))
                    {
                        Find.ResearchManager.ApplyTechprint(selectedProject, null);
                        SoundDefOf.TechprintApplied.PlayOneShotOnCamera();
                    }
                    Text.Font = GameFont.Small;
                }

                return false; // Skip original method
            }
            catch (Exception ex)
            {
                Log.Error($"[DMSE] Error in DrawProjectInfo patch: {ex}");
                return true; // Fall back to original
            }
        }

        /// <summary>
        /// Draw project progress for dual-slot (Basic/Advanced) layout.
        /// </summary>
        private static void DrawDualSlotProjectInfo(MainTabWindow_Research __instance, Rect rect, ResearchTabDef currentTab)
        {
            // Get categories for this tab (from mod extension or legacy)
            var categories = ResearchTabUtility.GetCategoriesForTab(currentTab);
            if (categories == null || categories.Count == 0)
            {
                using (new TextBlock(TextAnchor.MiddleCenter))
                {
                    Widgets.Label(rect, "NoProjectSelected".Translate());
                }
                return;
            }

            int cols = categories.Count;
            float colWidth = rect.width / cols;

            // Calculate prefix width for category labels across all categories
            float prefixWidth = 0f;
            foreach (var def in DefDatabase<KnowledgeCategoryDef>.AllDefs)
            {
                float labelWidth = Text.CalcSize(def.LabelCap + ":").x;
                if (labelWidth > prefixWidth) prefixWidth = labelWidth;
            }

            bool anyProject = false;
            for (int i = 0; i < cols; i++)
            {
                var cat = categories[i];
                Rect colRect = new Rect(rect.x + i * colWidth, rect.y, colWidth, rect.height);

                ResearchProjectDef proj = Find.ResearchManager.GetProject(cat);
                if (proj != null)
                {
                    anyProject = true;
                    DrawProjectProgress(__instance, colRect, proj, cat.LabelCap, prefixWidth);
                }
                else
                {
                    using (new TextBlock(TextAnchor.MiddleCenter))
                    {
                        Widgets.Label(colRect, "NoProjectSelected".Translate());
                    }
                }

                // Draw vertical separator except after last column
                if (i < cols - 1)
                {
                    float sepX = colRect.xMax;
                    Widgets.DrawLineVertical((int)sepX, (int)rect.y, (int)rect.height);
                }
            }

            if (!anyProject)
            {
                // nothing selected in any column
            }
        }

        /// <summary>
        /// Draw project progress for single-slot layout.
        /// </summary>
        private static void DrawSingleSlotProjectInfo(MainTabWindow_Research __instance, Rect rect, ResearchProjectDef project)
        {
            if (project == null)
            {
                using (new TextBlock(TextAnchor.MiddleCenter))
                {
                    Widgets.Label(rect, "NoProjectSelected".Translate());
                }
            }
            else
            {
                DrawProjectProgress(__instance, rect, project);
            }
        }

        /// <summary>
        /// Helper to draw progress bar for a single project.
        /// This mimics the original method behavior.
        /// </summary>
        private static void DrawProjectProgress(MainTabWindow_Research __instance, Rect rect, ResearchProjectDef project, string categoryLabel = null, float prefixWidth = 0f)
        {
            if (project == null)
            {
                return;
            }

            // Draw category label if provided (for dual-slot)
            if (!categoryLabel.NullOrEmpty())
            {
                Rect labelRect = rect;
                labelRect.width = prefixWidth;
                GUI.Label(labelRect, categoryLabel + ":");
                
                Rect projectRect = rect;
                projectRect.x += prefixWidth + 5f;
                projectRect.width -= prefixWidth + 5f;
                
                DrawProjectProgressBar(__instance, projectRect, project);
            }
            else
            {
                // No label - full width (single-slot)
                DrawProjectProgressBar(__instance, rect, project);
            }
        }

        /// <summary>
        /// Draw the actual progress bar using original game logic.
        /// </summary>
        private static void DrawProjectProgressBar(MainTabWindow_Research instance, Rect rect, ResearchProjectDef project)
        {
            if (project == null)
            {
                return;
            }

            // Call original rendering if available via reflection
            try
            {
                var method = typeof(MainTabWindow_Research).GetMethod(
                    "DrawProjectProgress",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(Rect), typeof(ResearchProjectDef) },
                    null
                );

                if (method != null)
                {
                    method.Invoke(instance, new object[] { rect, project });
                }
            }
            catch
            {
                // Fallback: Draw a simple progress bar
                float progress = project.ProgressPercent;
                Widgets.FillableBar(rect, progress, null, null, doBorder: true);
                
                string label = project.LabelCap + " " + Mathf.RoundToInt(progress * 100f) + "%";
                Text.Font = GameFont.Tiny;
                Widgets.Label(rect, label);
                Text.Font = GameFont.Small;
            }
        }

        /// <summary>
        /// Draw start button - delegates to helper to find and call original method.
        /// </summary>
        private static void DrawStartButton(MainTabWindow_Research __instance, Rect rect)
        {
            try
            {
                var method = typeof(MainTabWindow_Research).GetMethod(
                    "DrawStartButton",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(Rect) },
                    null
                );

                if (method != null)
                {
                    method.Invoke(__instance, new object[] { rect });
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DMSE] Could not invoke DrawStartButton: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Helper class for accessing private methods of MainTabWindow_Research.
    /// This encapsulates reflection operations to make patches more maintainable.
    /// </summary>
    public static class MainTabWindow_Research_Helper
    {
        private static MethodInfo cachedGetLabel = null;
        private static MethodInfo GetLabelMethod => cachedGetLabel ??
            (cachedGetLabel = typeof(MainTabWindow_Research).GetMethod(
                "GetLabel",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(ResearchProjectDef) },
                null
            ));

        private static MethodInfo cachedGetLabelWithNewline = null;
        private static MethodInfo GetLabelWithNewlineMethod => cachedGetLabelWithNewline ??
            (cachedGetLabelWithNewline = typeof(MainTabWindow_Research).GetMethod(
                "GetLabelWithNewlineCached",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null
            ));

        private static MethodInfo cachedPosX = null;
        private static MethodInfo PosXMethod => cachedPosX ??
            (cachedPosX = typeof(MainTabWindow_Research).GetMethod(
                "PosX",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(ResearchProjectDef) },
                null
            ));

        private static MethodInfo cachedPosY = null;
        private static MethodInfo PosYMethod => cachedPosY ??
            (cachedPosY = typeof(MainTabWindow_Research).GetMethod(
                "PosY",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(ResearchProjectDef) },
                null
            ));

        public static string GetLabelText(MainTabWindow_Research window, ResearchProjectDef project)
        {
            try
            {
                return GetLabelMethod?.Invoke(window, new object[] { project }) as string ?? project.label;
            }
            catch
            {
                return project.label;
            }
        }

        public static float GetPositionX(MainTabWindow_Research window, ResearchProjectDef project)
        {
            try
            {
                return (float)(PosXMethod?.Invoke(window, new object[] { project }) ?? 0f);
            }
            catch
            {
                return project.researchViewX;
            }
        }

        public static float GetPositionY(MainTabWindow_Research window, ResearchProjectDef project)
        {
            try
            {
                return (float)(PosYMethod?.Invoke(window, new object[] { project }) ?? 0f);
            }
            catch
            {
                return project.researchViewY;
            }
        }
    }
}
