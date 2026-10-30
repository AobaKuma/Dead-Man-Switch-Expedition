using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 彈體 → 導彈物品 → 配方 的反查（組裝台用）。
    /// 關聯方向是物品以 <see cref="CompProperties_MissileConfig.defaultBody"/> 宣告彈體；
    /// 物品型彈體的研究門檻在配方上（<see cref="MissileBodyDef.researchPrerequisites"/> 只對 Launcher 型有意義）。
    /// </summary>
    public static class MissileItemUtility
    {
        private static Dictionary<MissileBodyDef, ThingDef> itemByBody;
        private static Dictionary<ThingDef, RecipeDef> recipeByItem;
        private static List<MissileBodyDef> producibleBodies;

        /// <summary>以此彈體為 defaultBody 的導彈物品；沒有（彈道、固定構型巡飛彈等）為 null。</summary>
        public static ThingDef ItemDefFor(MissileBodyDef body)
        {
            if (body == null) { return null; }
            if (itemByBody == null)
            {
                itemByBody = new Dictionary<MissileBodyDef, ThingDef>();
                foreach (ThingDef d in MissileAssemblyUtility.MissileDefs)
                {
                    CompProperties_MissileConfig p = d.GetCompProperties<CompProperties_MissileConfig>();
                    if (p?.defaultBody != null && !itemByBody.ContainsKey(p.defaultBody)) { itemByBody.Add(p.defaultBody, d); }
                }
            }
            return itemByBody.TryGetValue(body, out ThingDef t) ? t : null;
        }

        /// <summary>產出此物品的配方（取第一個；目前每種導彈只有列印台一個配方）。</summary>
        public static RecipeDef RecipeFor(ThingDef item)
        {
            if (item == null) { return null; }
            if (recipeByItem == null)
            {
                recipeByItem = new Dictionary<ThingDef, RecipeDef>();
                foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
                {
                    ThingDef prod = r.ProducedThingDef;
                    if (prod != null && !recipeByItem.ContainsKey(prod)) { recipeByItem.Add(prod, r); }
                }
            }
            return recipeByItem.TryGetValue(item, out RecipeDef rd) ? rd : null;
        }

        /// <summary>組裝台的產品範圍：有物品型態、可在物品上自定義、沒有 stuff 的彈體，依量級與 label 排序。</summary>
        public static List<MissileBodyDef> ProducibleBodies
        {
            get
            {
                if (producibleBodies == null)
                {
                    producibleBodies = DefDatabase<MissileBodyDef>.AllDefsListForReading
                        .Where(b => b.customizeLocation == MissileCustomizeLocation.Item
                                    && ItemDefFor(b) is ThingDef item && !item.MadeFromStuff)
                        .OrderBy(b => (int)b.sizeClass).ThenBy(b => b.label)
                        .ToList();
                }
                return producibleBodies;
            }
        }

        /// <summary>
        /// 組裝台此刻能否生產此彈體：在產品範圍內，且配方目前可用（研究等）。
        /// 上帝模式即時裝配時略過研究，與 ITab 一致。
        /// </summary>
        public static bool CanProduce(MissileBodyDef body, out string reason)
        {
            reason = null;
            if (body == null || !ProducibleBodies.Contains(body))
            {
                reason = "DMSE.Bench.NotProducible".Translate();
                return false;
            }
            if (MissileAssemblyUtility.GodModeInstant) { return true; }
            RecipeDef recipe = RecipeFor(ItemDefFor(body));
            if (recipe != null && !recipe.AvailableNow)
            {
                reason = "DMSE.Bench.NeedsResearch".Translate(MissingResearchLabel(recipe));
                return false;
            }
            return true;
        }

        /// <summary>部件此刻能否選用（研究；上帝模式即時裝配略過）。</summary>
        public static bool PartUsable(MissilePartDef part, out string reason)
        {
            reason = null;
            if (part == null || part.ResearchSatisfied || MissileAssemblyUtility.GodModeInstant) { return true; }
            reason = "DMSE.Bench.NeedsResearch".Translate(string.Join(", ",
                part.researchPrerequisites.Where(r => !r.IsFinished).Select(r => r.LabelCap.ToString())));
            return false;
        }

        private static string MissingResearchLabel(RecipeDef recipe)
        {
            List<string> missing = new List<string>();
            if (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished)
            {
                missing.Add(recipe.researchPrerequisite.LabelCap);
            }
            if (recipe.researchPrerequisites != null)
            {
                missing.AddRange(recipe.researchPrerequisites.Where(r => !r.IsFinished).Select(r => r.LabelCap.ToString()));
            }
            return missing.Count > 0 ? string.Join(", ", missing.Distinct()) : "?";
        }
    }
}
