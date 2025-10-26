using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{

    /// <summary>
    /// TODO: 氧烛，減少房間真空，消耗燃料。
    /// </summary>
    public class CompProperties_OxygenCandle : CompProperties
    {
        public float vacuumReductionPerTick = 0.05f;//會被房間的大小影響。
        public CompProperties_OxygenCandle()
        {
            this.compClass = typeof(CompOxygenCandle);
        }
    }
    public class CompOxygenCandle : ThingComp
    {
        public CompProperties_OxygenCandle Props => (CompProperties_OxygenCandle)props;
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
        }
        public override void CompTick()
        {
            base.CompTick();
            if (!parent.IsHashIntervalTick(60)) return;
            if (!parent.Spawned) return;
            // 讓房間更新狀態
            Room room = parent.GetRoom();
            if (room != null && room.Vacuum > 0)
            {
                float reduceRate = Props.vacuumReductionPerTick / room.CellCount;
                room.Vacuum = Mathf.Max(0, room.Vacuum - reduceRate);
                Log.Message(reduceRate);
            }
        }
    }
}