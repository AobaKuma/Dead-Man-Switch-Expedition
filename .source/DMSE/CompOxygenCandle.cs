using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_OxygenCandle : CompProperties
    {
        public float vacuumReductionPerTick = 0.05f; // 會被房間大小影響
        public int tickInterval = 60;                // 每幾 tick 執行一次
        public float fuelConsumePerInterval = 1f;    // 每次執行消耗燃料量

        public CompProperties_OxygenCandle()
        {
            compClass = typeof(CompOxygenCandle);
        }
    }

    public class CompOxygenCandle : ThingComp
    {
        public CompProperties_OxygenCandle Props => (CompProperties_OxygenCandle)props;

        private CompRefuelable refuelComp;
        private CompFlickable flickComp;

        private bool Activated =>
            (flickComp == null || flickComp.SwitchIsOn) &&
            refuelComp != null &&
            refuelComp.HasFuel;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            refuelComp = parent.GetComp<CompRefuelable>();
            flickComp = parent.GetComp<CompFlickable>();
        }

        public override void CompTick()
        {
            base.CompTick();
            if (!parent.Spawned) return;
            if (!parent.IsHashIntervalTick(Props.tickInterval)) return;
            if (!Activated) return;

            refuelComp.ConsumeFuel(Props.fuelConsumePerInterval);

            if (parent.Destroyed) return;

            if (!refuelComp.HasFuel)
            {
                parent.Destroy(DestroyMode.KillFinalize);
                return;
            }

            Room room = parent.GetRoom();
            if (room != null && room.Vacuum > 0f && room.CellCount > 0)
            {
                float reduceRate = Props.vacuumReductionPerTick / room.CellCount;
                room.Vacuum = Mathf.Max(0f, room.Vacuum - reduceRate);
            }
        }
    }
}