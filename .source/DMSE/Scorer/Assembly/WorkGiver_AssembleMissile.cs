using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 掃描需要裝配的導彈：尚缺資源時發配「搬運資源」工作，資源齊全時發配「裝配」工作。
    /// </summary>
    public class WorkGiver_AssembleMissile : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            // 地面上已生成的導彈物品
            List<ThingDef> defs = MissileAssemblyUtility.MissileDefs;
            for (int i = 0; i < defs.Count; i++)
            {
                List<Thing> things = pawn.Map.listerThings.ThingsOfDef(defs[i]);
                for (int j = 0; j < things.Count; j++)
                {
                    yield return things[j];
                }
            }

            // 架內 unspawned 導彈：以儲存架本身作為工作目標（架已生成且有 map 位置）
            foreach (Building_MissileRack rack in pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building_MissileRack>())
            {
                IReadOnlyList<Thing> held = rack.HeldThings;
                if (held == null) { continue; }
                for (int i = 0; i < held.Count; i++)
                {
                    CompMissileConfig c = held[i]?.TryGetComp<CompMissileConfig>();
                    if (c != null && c.NeedsAssembly && c.assemblyConfirmed)
                    {
                        yield return rack;
                        break; // 一個架只 yield 一次，JobOnThing 會取第一枚待裝配的導彈
                    }
                }
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompMissileConfig comp = MissileAssemblyUtility.GetAssemblyComp(t);
            // GetAssemblyComp 對架內已過濾 NeedsAssembly + assemblyConfirmed；
            // 對地面物品則直接回傳，需在此補充檢查。
            if (comp == null || !comp.NeedsAssembly || !comp.assemblyConfirmed) { return false; }
            if (t.IsForbidden(pawn) || !pawn.CanReserve(t, 1, -1, null, forced)) { return false; }

            Dictionary<ThingDef, int> still = comp.StillNeeded();
            if (still.Count == 0) { return true; }

            foreach (KeyValuePair<ThingDef, int> kv in still)
            {
                if (MissileAssemblyUtility.FindResource(pawn, kv.Key) != null) { return true; }
            }
            JobFailReason.Is("DMSE.Missile.NoResources".Translate());
            return false;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompMissileConfig comp = MissileAssemblyUtility.GetAssemblyComp(t);
            if (comp == null || !comp.NeedsAssembly || !comp.assemblyConfirmed) { return null; }

            Dictionary<ThingDef, int> still = comp.StillNeeded();
            if (still.Count > 0)
            {
                foreach (KeyValuePair<ThingDef, int> kv in still)
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

            return JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_AssembleMissile, t);
        }
    }
}
