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
        /// <summary>從導彈儲存架取出導彈並裝填至發射平台。</summary>
        public static JobDef DMSE_LoadMissileLauncher;

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

        /// <summary>
        /// 從目標 Thing 取得需要裝配的 <see cref="CompMissileConfig"/>。
        /// 若 t 直接帶有 Comp 則回傳之；
        /// 若 t 為 <see cref="Building_MissileRack"/>，則回傳其中第一個
        /// <c>NeedsAssembly &amp;&amp; assemblyConfirmed</c> 的導彈 Comp。
        /// 回傳 null 表示此目標目前不需要裝配。
        /// </summary>
        public static CompMissileConfig GetAssemblyComp(Thing t)
        {
            // 直接情況：導彈物品在地面
            CompMissileConfig direct = t.TryGetComp<CompMissileConfig>();
            if (direct != null) { return direct; }

            // 間接情況：目標為儲存架，搜尋架內第一枚待裝配且已確認的導彈
            if (t is Building_MissileRack rack)
            {
                IReadOnlyList<Thing> held = rack.HeldThings;
                if (held == null) { return null; }
                for (int i = 0; i < held.Count; i++)
                {
                    CompMissileConfig c = held[i]?.TryGetComp<CompMissileConfig>();
                    if (c != null && c.NeedsAssembly && c.assemblyConfirmed) { return c; }
                }
            }
            return null;
        }

        /// <summary>
        /// 取得可用於生成資源的位置（Map + 座標）。
        /// 若 comp.parent 未生成，沿 ParentHolder 鏈找到最近的已生成建築。
        /// </summary>
        public static bool TryGetSpawnLocation(CompMissileConfig comp, out Map map, out IntVec3 pos)
        {
            map = null;
            pos = IntVec3.Invalid;
            if (comp?.parent == null) { return false; }

            if (comp.parent.Spawned)
            {
                map = comp.parent.Map;
                pos = comp.parent.Position;
                return true;
            }

            // 沿容器鏈向上找已生成的建築
            IThingHolder holder = comp.parent.ParentHolder;
            while (holder != null)
            {
                if (holder is Thing building && building.Spawned)
                {
                    map = building.Map;
                    pos = building.Position;
                    return true;
                }
                holder = holder.ParentHolder;
            }
            return false;
        }
    }
}
