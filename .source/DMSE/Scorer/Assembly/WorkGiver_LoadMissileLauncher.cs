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
            // 先找有存貨的儲存架；沒有就沒有工作，不必逐座詢問發射器（數十座垂發時這是主要成本）。
            List<IMissileRack> sources = SourceRacks(pawn.Map);
            if (sources.Count == 0) { yield break; }
            IReadOnlyList<IMissileRack> racks = MapComponent_MissileRacks.For(pawn.Map)?.Racks;
            if (racks == null) { yield break; }
            for (int i = 0; i < racks.Count; i++)
            {
                IMissileRack launcher = racks[i];
                Thing b = launcher.RackThing;
                if (b.Faction != Faction.OfPlayer || launcher.Full || !IsLauncher(b)) { continue; }
                if (AnyAcceptable(launcher, sources)) { yield return b; }
            }
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
            => SourceRacks(pawn.Map).Count == 0;

        private static readonly List<IMissileRack> sourceScratch = new List<IMissileRack>();
        private static Map sourceScratchMap;
        private static int sourceScratchTick = -1;

        /// <summary>
        /// 玩家的純儲存架（非發射平台）中有存貨者。同一 tick 內共用結果：
        /// 一次找工作會對每個候選發射器問一次，存貨在同一 tick 內不會變。
        /// </summary>
        private static List<IMissileRack> SourceRacks(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (sourceScratchMap == map && sourceScratchTick == now) { return sourceScratch; }
            sourceScratch.Clear();
            sourceScratchMap = map;
            sourceScratchTick = now;
            IReadOnlyList<IMissileRack> racks = MapComponent_MissileRacks.For(map)?.Racks;
            if (racks == null) { return sourceScratch; }
            for (int i = 0; i < racks.Count; i++)
            {
                IMissileRack rack = racks[i];
                Thing b = rack.RackThing;
                if (b == null || !b.Spawned || b.Faction != Faction.OfPlayer || rack.StoredCount == 0) { continue; }
                if (IsLauncher(b)) { continue; } // 不從其他發射平台搬
                sourceScratch.Add(rack);
            }
            return sourceScratch;
        }

        private static bool AnyAcceptable(IMissileRack launcher, List<IMissileRack> sources)
        {
            StorageSettings settings = launcher.GetStoreSettings();
            if (settings == null) { return false; }
            for (int s = 0; s < sources.Count; s++)
            {
                if (sources[s].RackThing == launcher.RackThing) { continue; }
                IReadOnlyList<Thing> held = sources[s].HeldThings;
                for (int i = 0; i < held.Count; i++)
                {
                    if (settings.AllowedToAccept(held[i])) { return true; }
                }
            }
            return false;
        }

        /// <summary>
        /// 判定建築是否為發射平台（含發射元件）。
        /// 純儲存架不含任何發射 comp，故不會被選為目標。
        /// </summary>
        public static bool IsLauncher(Thing b)
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
            List<IMissileRack> sources = SourceRacks(pawn.Map);
            for (int s = 0; s < sources.Count; s++)
            {
                IMissileRack rack = sources[s];
                Thing b = rack.RackThing;
                if (b == launcher.RackThing) { continue; }
                if (rack.StoredCount == 0) { continue; } // 同一 tick 內可能已被取走
                // 便宜的相容檢查在前，預約與可達性（尋路）在後。
                bool compatible = false;
                foreach (Thing missile in rack.HeldThings)
                {
                    if (launcherSettings.AllowedToAccept(missile)) { compatible = true; break; }
                }
                if (!compatible) { continue; }
                if (b.IsForbidden(pawn)) { continue; }
                if (!pawn.CanReserve(b, 1, -1, null, false)) { continue; }
                if (!pawn.CanReach(b, PathEndMode.Touch, Danger.Deadly)) { continue; }
                return rack;
            }
            return null;
        }
    }
}
