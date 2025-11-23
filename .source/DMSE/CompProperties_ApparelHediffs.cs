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
        protected new CompProperties_ApparelCauseHediff_Microgravity Props => (CompProperties_ApparelCauseHediff_Microgravity)props;
        public static bool InMicroGravity(Pawn pawn)
        {
            if (pawn is null || !pawn.Spawned) return false;
            if (!ModsConfig.OdysseyActive) Log.WarningOnce($"Warning, {pawn} checking Gravity without OdysseyActive.", 123457);

            if (pawn.Map?.TileInfo?.Layer?.Def == PlanetLayerDefOf.Surface)
            {
                return false;
            }
            return true;
        }
        protected void ApplyHediff(Pawn pawn)
        {
            if (pawn.health.hediffSet.GetFirstHediffOfDef(Props.hediff) == null)
            {
                HediffComp_RemoveIfApparelDropped hediffComp_RemoveIfApparelDropped = pawn.health.AddHediff(Props.hediff, pawn.health.hediffSet.GetNotMissingParts().FirstOrFallback((BodyPartRecord p) => p.def == Props.part)).TryGetComp<HediffComp_RemoveIfApparelDropped>();
                if (hediffComp_RemoveIfApparelDropped != null)
                {
                    hediffComp_RemoveIfApparelDropped.wornApparel = (Apparel)parent;
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
            RemoveHediff(pawn);
            if (InMicroGravity(pawn) && Props.revert == false) //如果在微重力環境下，且不是反向作用
            {
                ApplyHediff(pawn);
            }
            else if (Props.revert) //如果是反向作用
            {
                ApplyHediff(pawn);
            }
        }
        public override void Notify_Unequipped(Pawn pawn)
        {
            wearer = null;
            RemoveHediff(pawn);
        }
        public override void Notify_WearerDied()
        {
            RemoveHediff(wearer);
        }
    }
}