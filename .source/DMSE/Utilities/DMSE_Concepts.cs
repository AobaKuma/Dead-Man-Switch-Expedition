using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 學習輔助（ConceptDef）的觸發入口。
    ///
    /// <see cref="ConceptDef.needsOpportunity"/> 為 true 的條目不會自己彈出，
    /// 必須由遊戲中的事件呼叫 <see cref="LessonAutoActivator.TeachOpportunity"/>。
    /// 這裡集中管理 defName，避免各處硬寫字串；條目不存在或已學過時安靜略過。
    /// </summary>
    public static class DMSE_Concepts
    {
        public const string BVRChain = "DMSE_BVRChain";
        public const string SeekerTargeting = "DMSE_SeekerTargeting";
        public const string ElectronicWarfare = "DMSE_ElectronicWarfare";
        public const string MissileAssembly = "DMSE_MissileAssembly";
        public const string StrikePlanning = "DMSE_StrikePlanning";
        public const string BallisticPlatforms = "DMSE_BallisticPlatforms";
        public const string EnemyMissileBases = "DMSE_EnemyMissileBases";
        public const string NPCAirDefenseCircles = "DMSE_NPCAirDefenseCircles";

        public static void Teach(string defName, OpportunityType type = OpportunityType.GoodToKnow)
        {
            if (Current.Game == null || Find.Tutor == null)
            {
                return;
            }
            ConceptDef def = DefDatabase<ConceptDef>.GetNamedSilentFail(defName);
            if (def == null || PlayerKnowledgeDatabase.IsComplete(def))
            {
                return;
            }
            LessonAutoActivator.TeachOpportunity(def, type);
        }

        /// <summary>玩家雷達第一次偵測到來襲波次。</summary>
        public static void TeachBVRChain()
            => Teach(BVRChain, OpportunityType.Important);

        /// <summary>玩家打擊導彈第一次抵達目標地圖、由導引頭選落點。</summary>
        public static void TeachSeekerTargeting()
            => Teach(SeekerTargeting);

        /// <summary>玩家第一次發射帶箔條或反輻射導引的導彈。</summary>
        public static void TeachElectronicWarfare()
            => Teach(ElectronicWarfare);

        /// <summary>玩家第一次打開導彈裝配介面（物品或儲存架）。</summary>
        public static void TeachMissileAssembly()
            => Teach(MissileAssembly);

        /// <summary>玩家第一次打開打擊規劃視窗。</summary>
        public static void TeachStrikePlanning()
            => Teach(StrikePlanning);

        /// <summary>玩家第一次打開彈道發射井／塔架的裝配介面。</summary>
        public static void TeachBallisticPlatforms()
            => Teach(BallisticPlatforms);

        /// <summary>敵方導彈陣地第一次向玩家殖民地發射。</summary>
        public static void TeachEnemyMissileBases()
            => Teach(EnemyMissileBases, OpportunityType.Important);

        /// <summary>完成「雷達探測網」研究，世界地圖開始顯示 NPC 防空圈。</summary>
        public static void TeachNPCAirDefenseCircles()
            => Teach(NPCAirDefenseCircles, OpportunityType.Important);
    }
}
