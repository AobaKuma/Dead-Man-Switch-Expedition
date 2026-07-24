using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 持久化的 Occultech 知識點數存放區。無 Anomaly 時 GetProgress 對知識型專案永遠回傳 0、
    /// ApplyKnowledge(category) 也被 CheckAnomaly 擋下，故自行以字典保存進度，並由
    /// Patch_ResearchManager_GetProgress 於無 Anomaly 時接回 GetProgress。
    /// Anomaly 啟用時本組件完全不介入。
    /// </summary>
    public class GameComponent_OccultechKnowledge : GameComponent
    {
        private Dictionary<ResearchProjectDef, float> stored = new Dictionary<ResearchProjectDef, float>();

        private List<ResearchProjectDef> tmpKeys;
        private List<float> tmpVals;

        private static GameComponent_OccultechKnowledge cached;

        public GameComponent_OccultechKnowledge(Game game) { }

        /// <summary>取得目前遊戲的組件實例；若尚未進入遊戲則回傳 null（不拋例外）。</summary>
        public static GameComponent_OccultechKnowledge CompSafe
        {
            get
            {
                if (cached != null)
                {
                    return cached;
                }
                Game game = Current.Game;
                if (game == null)
                {
                    return null;
                }
                cached = game.GetComponent<GameComponent_OccultechKnowledge>();
                return cached;
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            cached = this;
            if (stored == null)
            {
                stored = new Dictionary<ResearchProjectDef, float>();
            }
        }

        public float GetStored(ResearchProjectDef proj)
        {
            if (proj == null || stored == null)
            {
                return 0f;
            }
            return stored.TryGetValue(proj, out float v) ? v : 0f;
        }

        public void SetStored(ResearchProjectDef proj, float value)
        {
            if (proj == null)
            {
                return;
            }
            stored[proj] = Mathf.Max(0f, value);
        }

        public void AddStored(ResearchProjectDef proj, float delta)
        {
            SetStored(proj, GetStored(proj) + delta);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref stored, "occultechKnowledge", LookMode.Def, LookMode.Value, ref tmpKeys, ref tmpVals);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && stored == null)
            {
                stored = new Dictionary<ResearchProjectDef, float>();
            }
        }
    }

    /// <summary>
    /// Occultech 知識點數的注入 / 路由工具。對外暴露 AddKnowledge 與 HasResearchTarget。
    /// </summary>
    public static class OccultechKnowledgeUtility
    {
        private const string OccultechTabDefName = "DMSE_ExpeditionalKnowledge";
        private const string RestrictedCategoryDefName = "DMSE_Restricted";

        private static KnowledgeCategoryDef cachedRestricted;
        private static ResearchTabDef cachedTab;

        /// <summary>基礎知識類別（Restricted）；點數預設由此注入並向上溢流。</summary>
        public static KnowledgeCategoryDef RestrictedCategory
        {
            get
            {
                if (cachedRestricted == null)
                {
                    cachedRestricted = DefDatabase<KnowledgeCategoryDef>.GetNamedSilentFail(RestrictedCategoryDefName);
                }
                return cachedRestricted;
            }
        }

        private static ResearchTabDef OccultechTab
        {
            get
            {
                if (cachedTab == null)
                {
                    cachedTab = DefDatabase<ResearchTabDef>.GetNamedSilentFail(OccultechTabDefName);
                }
                return cachedTab;
            }
        }

        /// <summary>依 Occultech 分頁定義的順序取得知識類別（Restricted → Sealed → Occulted）。</summary>
        private static List<KnowledgeCategoryDef> OrderedCategories()
        {
            List<KnowledgeCategoryDef> list = ResearchTabUtility.GetCategoriesForTab(OccultechTab);
            if (list != null && list.Count > 0)
            {
                return list;
            }
            list = new List<KnowledgeCategoryDef>();
            if (RestrictedCategory != null)
            {
                list.Add(RestrictedCategory);
            }
            return list;
        }

        /// <summary>
        /// 對指定知識類別注入知識。Anomaly 啟用時走原生 ApplyKnowledge；否則套用到未完成且
        /// 前置達成的專案，滿溢後往上一層類別溢流。
        /// </summary>
        public static void AddKnowledge(KnowledgeCategoryDef category, float amount)
        {
            if (category == null || amount <= 0f)
            {
                return;
            }

            ResearchManager manager = Find.ResearchManager;
            if (manager == null)
            {
                return;
            }

            if (ModsConfig.AnomalyActive)
            {
                manager.ApplyKnowledge(category, amount);
                return;
            }

            GameComponent_OccultechKnowledge store = GameComponent_OccultechKnowledge.CompSafe;
            if (store == null)
            {
                return;
            }

            List<KnowledgeCategoryDef> order = OrderedCategories();
            int startIdx = order.IndexOf(category);
            if (startIdx < 0)
            {
                ApplyWithinCategory(store, category, amount, out _);
                return;
            }

            float remaining = amount;
            for (int i = startIdx; i < order.Count && remaining > 0f; i++)
            {
                ApplyWithinCategory(store, order[i], remaining, out remaining);
            }
        }

        /// <summary>
        /// 是否存在「研究中／尚可推進」的 Occultech 專案，供萃取器判斷是否該持續消耗。
        /// Anomaly 啟用時：各類別中有被選為當前研究且未完成的專案即為 true。
        /// 無 Anomaly 時：分頁類別中存在任一未完成且前置達成的知識專案即為 true。
        /// 回傳 false（例如全部研究完畢）時，萃取器應停止抽取與耗能。
        /// </summary>
        public static bool HasResearchTarget()
        {
            List<KnowledgeCategoryDef> cats = OrderedCategories();
            if (cats == null || cats.Count == 0)
            {
                return false;
            }

            if (ModsConfig.AnomalyActive)
            {
                if (Find.ResearchManager == null)
                {
                    return false;
                }
                foreach (KnowledgeCategoryDef cat in cats)
                {
                    ResearchProjectDef proj = ResearchTabUtility.GetActiveProjectForCategory(cat);
                    if (proj != null && !proj.IsFinished)
                    {
                        return true;
                    }
                }
                return false;
            }

            List<ResearchProjectDef> all = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            foreach (KnowledgeCategoryDef cat in cats)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    ResearchProjectDef p = all[i];
                    if (p.knowledgeCategory == cat && p.baseCost <= 0f && p.knowledgeCost > 0f
                        && !p.IsFinished && p.PrerequisitesCompleted)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>將知識點依序灌入某一類別內的可用專案，填滿一個就完成一個，餘量往下帶。</summary>
        private static void ApplyWithinCategory(GameComponent_OccultechKnowledge store, KnowledgeCategoryDef category, float amount, out float remainder)
        {
            remainder = amount;
            if (category == null || amount <= 0f)
            {
                return;
            }

            List<ResearchProjectDef> candidates = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(p => p.knowledgeCategory == category && p.baseCost <= 0f && p.knowledgeCost > 0f)
                .Where(p => !p.IsFinished && p.PrerequisitesCompleted)
                .OrderBy(p => p.researchViewY)
                .ThenBy(p => p.researchViewX)
                .ThenBy(p => p.knowledgeCost)
                .ToList();

            foreach (ResearchProjectDef proj in candidates)
            {
                if (remainder <= 0f)
                {
                    break;
                }

                float need = proj.knowledgeCost - store.GetStored(proj);
                if (need <= 0f)
                {
                    FinishOccultProject(store, proj);
                    continue;
                }

                float give = Mathf.Min(need, remainder);
                store.AddStored(proj, give);
                remainder -= give;

                if (store.GetStored(proj) >= proj.knowledgeCost - 0.001f)
                {
                    FinishOccultProject(store, proj);
                }
            }
        }

        /// <summary>
        /// 標記某 Occultech 知識專案為完成：儲存量設為滿額，再呼叫原生 FinishProject 套用解鎖並發信。
        /// 同時把知識型前置也設為滿額，避免遞迴後前置仍顯示未完成。
        /// </summary>
        private static void FinishOccultProject(GameComponent_OccultechKnowledge store, ResearchProjectDef proj)
        {
            if (proj.prerequisites != null)
            {
                foreach (ResearchProjectDef pre in proj.prerequisites)
                {
                    if (pre != null && pre.baseCost <= 0f && pre.knowledgeCost > 0f)
                    {
                        store.SetStored(pre, pre.knowledgeCost);
                    }
                }
            }

            store.SetStored(proj, proj.knowledgeCost);
            Find.ResearchManager.FinishProject(proj, doCompletionDialog: false, null, doCompletionLetter: true);
        }
    }
}
