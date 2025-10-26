using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_VacuumPump : CompProperties
    {
        public float vacuumPerTick = 0.05f;//會被房間的大小影響。
        public CompProperties_VacuumPump()
        {
            this.compClass = typeof(CompVacuumPump);
        }
    }
    public class CompVacuumPump : ThingComp
    {
        public CompProperties_VacuumPump Props => (CompProperties_VacuumPump)props;
        private CompPowerTrader powerComp;
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            powerComp = parent.GetComp<CompPowerTrader>();
        }
        public override void CompTickInterval(int delta)
        {
            if (!parent.Spawned) return;
            if (powerComp == null || !powerComp.PowerOn) return;
            // 讓房間更新狀態
            Room room = parent.GetRoom();
            if (room != null && room.Vacuum < 1f)
            {
                float increaseRate = Props.vacuumPerTick / room.CellCount;
                room.Vacuum = Mathf.Min(1f, room.Vacuum + increaseRate);
                powerComp.PowerOutput = -powerComp.Props.PowerConsumption;
            }
            else
            {
                if (powerComp.Props.idlePowerDraw != -1f)
                {
                    powerComp.PowerOutput = -powerComp.Props.idlePowerDraw;
                }
            }
        }
    }
}