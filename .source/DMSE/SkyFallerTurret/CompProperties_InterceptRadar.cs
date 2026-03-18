using RimWorld;
using Verse;

namespace DMSE
{
    public class CompProperties_InterceptRadar : CompProperties
    {
        public int delayTicks = 2500;

        public CompProperties_InterceptRadar()
        {
            compClass = typeof(CompInterceptRadar);
        }
    }
    public class CompInterceptRadar : ThingComp
    {
        public CompProperties_InterceptRadar Props
        {
            get { return (CompProperties_InterceptRadar)props; }
        }

        public int DelayTicks
        {
            get { return Props.delayTicks; }
        }

        public bool Active
        {
            get
            {
                if (parent == null || !parent.Spawned || parent.Destroyed)
                {
                    return false;
                }

                CompBreakdownable breakdownable = parent.TryGetComp<CompBreakdownable>();
                if (breakdownable != null && breakdownable.BrokenDown)
                {
                    return false;
                }

                CompFlickable flickable = parent.TryGetComp<CompFlickable>();
                if (flickable != null && !flickable.SwitchIsOn)
                {
                    return false;
                }

                CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn)
                {
                    return false;
                }

                CompStunnable stunnable = parent.TryGetComp<CompStunnable>();
                if (stunnable != null && stunnable.StunHandler != null && stunnable.StunHandler.Stunned)
                {
                    return false;
                }

                CompRefuelable refuelable = parent.TryGetComp<CompRefuelable>();
                if (refuelable != null && refuelable.Fuel <= 0f)
                {
                    return false;
                }

                return true;
            }
        }
    }

}