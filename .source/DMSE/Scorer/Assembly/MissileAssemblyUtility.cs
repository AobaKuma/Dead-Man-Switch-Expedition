using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    [DefOf]
    public static class DMSE_MissileJobDefOf
    {
        public static JobDef DMSE_DeliverMissileResource;
        public static JobDef DMSE_AssembleMissile;

        static DMSE_MissileJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DMSE_MissileJobDefOf));
        }
    }

    public static class MissileAssemblyUtility
    {
        private static List<ThingDef> cachedDefs;

        /// <summary>所有掛了 CompMissileConfig 的物品定義（快取）。</summary>
        public static List<ThingDef> MissileDefs
        {
            get
            {
                if (cachedDefs == null)
                {
                    // 只取可搬運的物品（排除發射器等建築，它們同樣帶 CompMissileConfig 但只用於接收已裝填設定）。
                    cachedDefs = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(d => d.category == ThingCategory.Item
                                    && d.comps != null && d.comps.Any(c => c is CompProperties_MissileConfig))
                        .ToList();
                }
                return cachedDefs;
            }
        }

        /// <summary>尋找可搬運、可預約且未被禁用的指定資源。</summary>
        public static Thing FindResource(Pawn pawn, ThingDef def)
        {
            return GenClosest.ClosestThingReachable(
                pawn.Position, pawn.Map,
                ThingRequest.ForDef(def),
                PathEndMode.ClosestTouch,
                TraverseParms.For(pawn),
                9999f,
                x => !x.IsForbidden(pawn) && pawn.CanReserve(x));
        }
    }
}
