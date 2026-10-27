using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 研究面板的「解鎖」清單：原版只收配方產物、ThingDef、地形、手術與靈能儀式，
    /// 這裡把以研究擋住的導彈彈體與裝配部件也列進去，像手術一樣顯示並可點開資訊卡。
    /// ResearchPrerequisitesUtility 的「需搭配其他研究」分組也從 UnlockedDefs 建立，所以只需補這一處。
    /// </summary>
    public static class MissileResearchUnlocks
    {
        private static Dictionary<ResearchProjectDef, List<Def>> byProject;

        public static List<Def> For(ResearchProjectDef project)
        {
            if (byProject == null)
            {
                byProject = new Dictionary<ResearchProjectDef, List<Def>>();
                IEnumerable<(Def def, List<ResearchProjectDef> research)> gated =
                    DefDatabase<MissileBodyDef>.AllDefs.Select(b => ((Def)b, b.researchPrerequisites))
                    .Concat(DefDatabase<MissilePartDef>.AllDefs.Select(p => ((Def)p, p.researchPrerequisites)));
                foreach ((Def def, List<ResearchProjectDef> research) in gated)
                {
                    if (research == null) { continue; }
                    foreach (ResearchProjectDef r in research)
                    {
                        if (r == null) { continue; }
                        if (!byProject.TryGetValue(r, out List<Def> list)) { byProject[r] = list = new List<Def>(); }
                        if (!list.Contains(def)) { list.Add(def); }
                    }
                }
                // 彈體排在部件前，各自依名稱排序。
                foreach (List<Def> list in byProject.Values)
                {
                    list.SortBy(d => d is MissileBodyDef ? 0 : 1, d => d.label ?? d.defName);
                }
            }
            return byProject.TryGetValue(project, out List<Def> result) ? result : null;
        }

        /// <summary>彈體圖示：自訂 uiIconPath 優先，否則用抵達時的 skyfaller 或發射 Projectile 的外觀。</summary>
        public static Texture BodyIcon(MissileBodyDef body)
        {
            if (body.Icon != null) { return body.Icon; }
            foreach (ThingDef thing in new[] { body.incomingSkyfaller, body.launchProjectile })
            {
                Texture2D tex = thing?.uiIcon;
                if (tex != null && tex != BaseContent.BadTex) { return tex; }
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(ResearchProjectDef), nameof(ResearchProjectDef.UnlockedDefs), MethodType.Getter)]
    public static class Patch_ResearchProjectDef_UnlockedDefs
    {
        public static void Postfix(ResearchProjectDef __instance, List<Def> __result)
        {
            if (__result == null) { return; }
            List<Def> missile = MissileResearchUnlocks.For(__instance);
            // 原版快取同一份清單，只在第一次補上。
            if (missile == null || __result.Contains(missile[0])) { return; }
            __result.AddRange(missile);
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.DefIcon))]
    public static class Patch_Widgets_DefIcon_Missile
    {
        public static void Postfix(Rect rect, Def def, float scale, Material material, float alpha)
        {
            Texture tex = def is MissileBodyDef body ? MissileResearchUnlocks.BodyIcon(body)
                : def is MissilePartDef part ? part.Icon : null;
            if (tex != null) { Widgets.DrawTextureFitted(rect, tex, scale, material, alpha); }
        }
    }
}
