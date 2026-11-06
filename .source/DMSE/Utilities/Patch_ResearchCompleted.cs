using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 完成「雷達探測網」研究時，教學世界地圖的 NPC 防空圈。
    /// <see cref="DMSE_Concepts"/> 會自行確認條目存在、尚未學過，且當前是正常遊戲。
    /// </summary>
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    public static class Patch_ResearchCompleted
    {
        public static void Postfix(ResearchProjectDef proj)
        {
            if (proj != null && proj.defName == "DMSE_RadarNetwork")
            {
                DMSE_Concepts.TeachNPCAirDefenseCircles();
            }
        }
    }
}
