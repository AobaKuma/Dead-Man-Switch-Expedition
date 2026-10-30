using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 掃描需要裝配的導彈與有訂單的組裝台：尚缺資源時發配「搬運資源」工作，資源齊全時發配「裝配」工作。
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

            // 架內 unspawned 導彈：以儲存架本身作為工作目標（架已生成且有 map 位置）。
            // 被打擊任務保留的導彈不裝配（GetAssemblyComp 同樣略過），待裝配的變更擱置到保留解除。
            foreach (Building b in pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building>())
            {
                // 有訂單的組裝台：以台本身為工作目標（GetAssemblyTarget 先看組裝台）。
                if (b.TryGetComp<CompMissileAssemblyBench>()?.WantsWork == true)
                {
                    yield return b;
                    continue;
                }
                IMissileRack rack = MissileRackUtil.Of(b);
                if (rack == null) { continue; }
                IReadOnlyList<Thing> held = rack.HeldThings;
                if (held == null) { continue; }
                for (int i = 0; i < held.Count; i++)
                {
                    CompMissileConfig c = held[i]?.TryGetComp<CompMissileConfig>();
                    if (c != null && c.NeedsAssembly && c.assemblyConfirmed && !StrikeReservation.IsReserved(held[i]))
                    {
                        yield return rack.RackThing;
                        break; // 一個架只 yield 一次，JobOnThing 會取第一枚待裝配的導彈
                    }
                }
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IMissileAssemblyTarget comp = MissileAssemblyUtility.GetAssemblyTarget(t);
            // 架內與組裝台已在 GetAssemblyTarget 過濾；地面物品直接回傳，需在此補充檢查。
            if (comp == null || !comp.WantsWork) { return false; }
            if (t.IsForbidden(pawn) || !pawn.CanReserve(t, 1, -1, null, forced)) { return false; }

            Dictionary<ThingDef, int> still = comp.StillNeeded();
            if (still.Count == 0)
            {
                // 料已齊但出廠暫存已滿：不派施工，等成品被搬走。
                if (!comp.CanFinishNow)
                {
                    JobFailReason.Is("DMSE.Bench.OutputFull".Translate());
                    return false;
                }
                return true;
            }

            foreach (KeyValuePair<ThingDef, int> kv in still)
            {
                if (MissileAssemblyUtility.FindResource(pawn, kv.Key) != null) { return true; }
            }
            JobFailReason.Is("DMSE.Missile.NoResources".Translate());
            return false;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IMissileAssemblyTarget comp = MissileAssemblyUtility.GetAssemblyTarget(t);
            if (comp == null || !comp.WantsWork) { return null; }

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

            return comp.CanFinishNow ? JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_AssembleMissile, t) : null;
        }
    }
}
