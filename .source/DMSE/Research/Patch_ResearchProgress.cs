using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 讓 Occultech 知識型研究專案在「沒有 Anomaly DLC」時也能記錄進度。
    ///
    /// 原生 ResearchManager.GetProgress 對知識型專案（baseCost==0 且 knowledgeCost&gt;0）
    /// 只有在 ModsConfig.AnomalyActive 時才回傳實際知識點，否則永遠回傳 0，
    /// 導致 ProgressPercent / IsFinished 皆失效。
    ///
    /// 本 postfix 僅在無 Anomaly 時介入，且只針對知識型專案，把結果改為
    /// <see cref="GameComponent_OccultechKnowledge"/> 保存的點數。其餘專案完全不受影響。
    /// </summary>
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.GetProgress))]
    public static class Patch_ResearchManager_GetProgress
    {
        [HarmonyPostfix]
        public static void Postfix(ResearchProjectDef proj, ref float __result)
        {
            // Anomaly 啟用時交由原生機制，不介入。
            if (ModsConfig.AnomalyActive)
            {
                return;
            }
            if (proj == null)
            {
                return;
            }
            // 一般（baseCost）研究不碰。
            if (proj.baseCost > 0f)
            {
                return;
            }
            // 非知識型專案不碰。
            if (proj.knowledgeCost <= 0f)
            {
                return;
            }

            GameComponent_OccultechKnowledge store = GameComponent_OccultechKnowledge.CompSafe;
            if (store == null)
            {
                return;
            }

            __result = store.GetStored(proj);
        }
    }
}
