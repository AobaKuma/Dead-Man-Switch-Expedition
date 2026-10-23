using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 搜尋未滿的導彈發射平台（<see cref="Building_MissileRack"/> 含 <see cref="CompMissileLauncher_Rail"/>
    /// 或 <see cref="CompMissileLauncher_Interceptor"/>），並安排殖民者從鄰近的導彈儲存架（純
    /// <see cref="Building_MissileRack"/>）取出相容導彈裝填。
    ///
    /// 正常地面堆放的導彈已由 <see cref="IHaulDestination"/> 的標準搬運系統自動處理；
    /// 此 WorkGiver 僅補足「從儲存架容器取出 → 裝填發射平台容器」的缺口：
    /// 儲存架內的導彈為 unspawned，標準搬運系統看不到它們。
    /// </summary>
    public class WorkGiver_LoadMissileLauncher : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (Building b in pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building>())
            {
                if (MissileRackUtil.Of(b) != null && IsLauncher(b))
                {
                    yield return b;
                }
            }
        }

        /// <summary>
        /// 判定建築是否為發射平台（含發射元件）。
        /// 純儲存架不含任何發射 comp，故不會被選為目標。
        /// </summary>
        public static bool IsLauncher(Building b)
        {
            return b.TryGetComp<CompMissileLauncher_Rail>() != null
                || b.TryGetComp<CompMissileLauncher_Interceptor>() != null;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IMissileRack launcher = MissileRackUtil.Of(t);
            if (launcher == null) { return false; }
            if (launcher.Full) { return false; }
            if (t.IsForbidden(pawn)) { return false; }
            if (!pawn.CanReserve(t, 1, -1, null, forced)) { return false; }
            if (FindSourceRack(pawn, launcher) == null)
            {
                JobFailReason.Is("DMSE.Missile.NoRackAvailable".Translate());
                return false;
            }
            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IMissileRack launcher = MissileRackUtil.Of(t);
            if (launcher == null || launcher.Full) { return null; }
            IMissileRack source = FindSourceRack(pawn, launcher);
            if (source == null) { return null; }
            return JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_LoadMissileLauncher, launcher.RackThing, source.RackThing);
        }

        /// <summary>
        /// 找到一個可用的儲存架：有相容導彈、pawn 可到達且可預約。
        /// </summary>
        public static IMissileRack FindSourceRack(Pawn pawn, IMissileRack launcher)
        {
            StorageSettings launcherSettings = launcher.GetStoreSettings();

            foreach (Building b in pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building>())
            {
                if (b == launcher.RackThing) { continue; }
                if (IsLauncher(b)) { continue; } // 不從其他發射平台搬
                IMissileRack rack = MissileRackUtil.Of(b);
                if (rack == null) { continue; }
                if (rack.StoredCount == 0) { continue; }
                if (b.IsForbidden(pawn)) { continue; }
                if (!pawn.CanReserve(b, 1, -1, null, false)) { continue; }
                if (!pawn.CanReach(b, PathEndMode.Touch, Danger.Deadly)) { continue; }

                foreach (Thing missile in rack.HeldThings)
                {
                    if (launcherSettings.AllowedToAccept(missile))
                    {
                        return rack;
                    }
                }
            }
            return null;
        }
    }
}
