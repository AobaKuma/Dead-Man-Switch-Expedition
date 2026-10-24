using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 取下不再符合儲存設定的導彈：只處理 <see cref="CompMissileRackStorage"/> 平台。
    ///
    /// <see cref="Building_MissileRack"/> 本身是 <see cref="IHaulSource"/>，原版搬運會自動取出；
    /// comp 不是 Thing、無法登記為 haul source，容器內的導彈原版看不到，故由此 WorkGiver 補足。
    /// </summary>
    public class WorkGiver_UnloadMissileRack : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (Building b in pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building>())
            {
                if (b is IHaulSource) { continue; }
                CompMissileRackStorage rack = b.TryGetComp<CompMissileRackStorage>();
                if (rack != null && FirstDisallowed(rack) != null)
                {
                    yield return b;
                }
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is IHaulSource) { return false; }
            CompMissileRackStorage rack = t.TryGetComp<CompMissileRackStorage>();
            if (rack == null || FirstDisallowed(rack) == null) { return false; }
            if (t.IsForbidden(pawn)) { return false; }
            return pawn.CanReserve(t, 1, -1, null, forced);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
            => JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_UnloadMissileRack, t);

        /// <summary>容器內第一枚不符合儲存設定的導彈（沒有則為 null）。</summary>
        public static Thing FirstDisallowed(IMissileRack rack)
        {
            if (rack.StoredCount == 0) { return null; }
            StorageSettings settings = rack.GetStoreSettings();
            foreach (Thing m in rack.HeldThings)
            {
                if (!settings.AllowedToAccept(m))
                {
                    return m;
                }
            }
            return null;
        }
    }
}
