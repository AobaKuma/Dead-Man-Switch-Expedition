using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_EffecterColdLaunch : CompProperties_EffecterBase
    {
        int delayTicks = 100;
        public EffecterDef launchEffectWarmup;
        public EffecterDef launchEffectTrigger;
        public CompProperties_EffecterColdLaunch()
        {
            compClass = typeof(CompEffecterColdLaunch);
        }
    }

    public class CompEffecterColdLaunch : ThingComp
    {
        private Effecter effecter;

        public CompProperties_EffecterColdLaunch Props => (CompProperties_EffecterColdLaunch)props;

        protected virtual bool ShouldShowEffecter()
        {
            if (parent.Spawned)
            {
                return parent.MapHeld == Find.CurrentMap;
            }
            return false;
        }

        public override void CompTick()
        {
            base.CompTick();
            if (ShouldShowEffecter())
            {
                if (effecter == null)
                {
                    effecter = Props.effecterDef.SpawnAttached(parent, parent.MapHeld);
                }
                effecter?.EffectTick(parent, parent);
            }
            else
            {
                effecter?.Cleanup();
                effecter = null;
            }
        }
    }
}