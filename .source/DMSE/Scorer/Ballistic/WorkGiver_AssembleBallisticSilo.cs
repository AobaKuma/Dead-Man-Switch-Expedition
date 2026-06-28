using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 掃描需要裝配的彈道導彈發射架（建築），並分派搬運資源或執行裝配的工作。
    ///
    /// 與 <see cref="WorkGiver_AssembleMissile"/> 的差異：
    ///   - 掃描的是建築（<see cref="CompBallisticLauncher"/>），而非導彈物品。
    ///   - 重複使用現有的 <c>DMSE_DeliverMissileResource</c> 與新的
    ///     <c>DMSE_AssembleBallisticSilo</c> Job。
    /// </summary>
    public class WorkGiver_AssembleBallisticSilo : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (Building b in pawn.Map.listerBuildings.allBuildingsColonist)
            {
                CompBallisticLauncher launcher = b.TryGetComp<CompBallisticLauncher>();
                if (launcher == null || launcher.IsLoaded) { continue; }

                CompMissileConfig cfg = b.TryGetComp<CompMissileConfig>();
                if (cfg == null || !cfg.NeedsAssembly) { continue; }

                yield return b;
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompBallisticLauncher launcher = t.TryGetComp<CompBallisticLauncher>();
            CompMissileConfig cfg = t.TryGetComp<CompMissileConfig>();

            if (launcher == null || cfg == null) { return false; }
            if (launcher.IsLoaded) { return false; }
            if (!cfg.NeedsAssembly) { return false; }
            if (t.IsForbidden(pawn) || !pawn.CanReserve(t, 1, -1, null, forced)) { return false; }

            // 資源全齊 → 可裝配
            if (cfg.ResourcesComplete) { return true; }

            // 還有缺口 → 找得到資源才有工作
            Dictionary<ThingDef, int> still = cfg.StillNeeded();
            foreach (System.Collections.Generic.KeyValuePair<ThingDef, int> kv in still)
            {
                if (MissileAssemblyUtility.FindResource(pawn, kv.Key) != null) { return true; }
            }

            JobFailReason.Is("DMSE.Missile.NoResources".Translate());
            return false;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompBallisticLauncher launcher = t.TryGetComp<CompBallisticLauncher>();
            CompMissileConfig cfg = t.TryGetComp<CompMissileConfig>();

            if (launcher == null || cfg == null || launcher.IsLoaded || !cfg.NeedsAssembly)
            {
                return null;
            }

            // 尚有缺少的資源 → 分配搬運任務（複用現有 JobDriver）
            Dictionary<ThingDef, int> still = cfg.StillNeeded();
            if (still.Count > 0)
            {
                foreach (System.Collections.Generic.KeyValuePair<ThingDef, int> kv in still)
                {
                    Thing res = MissileAssemblyUtility.FindResource(pawn, kv.Key);
                    if (res != null)
                    {
                        Job job = JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_DeliverMissileResource, t, res);
                        job.count = kv.Value;
                        return job;
                    }
                }
                return null;
            }

            // 資源齊全 → 分配裝配任務
            return JobMaker.MakeJob(DMSE_BallisticJobDefOf.DMSE_AssembleBallisticSilo, t);
        }
    }
}
