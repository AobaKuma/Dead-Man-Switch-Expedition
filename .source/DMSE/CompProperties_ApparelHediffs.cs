using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace DMSE
{

    public class CompProperties_ApparelCauseHediff_Microgravity : CompProperties
    {
        public bool revert = false;
        public HediffDef hediff;
        public BodyPartDef part;

        public CompProperties_ApparelCauseHediff_Microgravity() => compClass = typeof(CompApparelCauseHediff_Microgravity);
    }
    public class CompApparelCauseHediff_Microgravity : ThingComp
    {
        protected Pawn wearer;
        protected CompProperties_ApparelCauseHediff_Microgravity Props => (CompProperties_ApparelCauseHediff_Microgravity)props;

        public static bool InMicroGravity(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned) return false;
            if (!ModsConfig.OdysseyActive) Log.WarningOnce($"Warning, {pawn} checking Gravity without OdysseyActive.", 123457);

            return pawn.Map?.TileInfo?.Layer?.Def != PlanetLayerDefOf.Surface;
        }

        private bool ShouldHaveHediff(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned) return false;

            bool inMicroGravity = InMicroGravity(pawn);
            return Props.revert ? !inMicroGravity : inMicroGravity;
        }

        private void SyncHediff(Pawn pawn)
        {
            if (pawn == null) return;

            if (ShouldHaveHediff(pawn))
            {
                ApplyHediff(pawn);
            }
            else
            {
                RemoveHediff(pawn);
            }
        }

        protected void ApplyHediff(Pawn pawn)
        {
            if (pawn.health.hediffSet.GetFirstHediffOfDef(Props.hediff) == null)
            {
                HediffComp_RemoveIfApparelDropped comp = pawn.health
                    .AddHediff(Props.hediff, pawn.health.hediffSet.GetNotMissingParts().FirstOrFallback(p => p.def == Props.part))
                    .TryGetComp<HediffComp_RemoveIfApparelDropped>();

                if (comp != null)
                {
                    comp.wornApparel = (Apparel)parent;
                }
            }
        }

        protected void RemoveHediff(Pawn pawn)
        {
            Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(Props.hediff);
            if (hediff != null)
            {
                pawn.health.RemoveHediff(hediff);
            }
        }

        public override void Notify_Equipped(Pawn pawn)
        {
            wearer = pawn;
            SyncHediff(pawn);
        }

        public override void Notify_Unequipped(Pawn pawn)
        {
            RemoveHediff(pawn);
            if (wearer == pawn) wearer = null;
        }

        public override void Notify_WearerDied()
        {
            if (wearer != null)
            {
                RemoveHediff(wearer);
                wearer = null;
            }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();

            Apparel apparel = parent as Apparel;
            Pawn currentWearer = apparel?.Wearer;
            if (currentWearer == null) return;

            wearer = currentWearer;
            SyncHediff(currentWearer);
        }
    }
}