using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System.Linq;
using Verse;

namespace DMSE
{
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "LandingEnded")]
    public static class Patch_ConsumeSolidBooster_AfterLanding
    {
        private static readonly System.Reflection.FieldInfo GravshipField =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "gravship");

        public static void Prefix(WorldComponent_GravshipController __instance)
        {
            var gravship = GravshipField.GetValue(__instance) as Gravship;
            var engine = gravship?.Engine;
            if (engine == null) return;

            var boosters = engine.GravshipComponents
                .Select(c => c.parent.TryGetComp<CompSolidBoosterThruster>())
                .Where(c => c != null && !c.Consumed && c.CanBeActive)
                .ToList();

            foreach (var b in boosters)
                b.ConsumeAfterLanding(); // 內部可設 consumed=true + Destroy
        }
    }

    public class CompProperties_SolidBoosterThruster : CompProperties_GravshipThruster
    {
        public CompProperties_SolidBoosterThruster()
        {
            compClass = typeof(CompSolidBoosterThruster);
        }
    }

    public class CompSolidBoosterThruster : CompGravshipThruster
    {
        private bool consumed;

        public bool Consumed => consumed;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref consumed, "consumed", defaultValue: false);
        }

        public void ConsumeAfterLanding()
        {
            if (consumed || parent.Destroyed) return;
            consumed = true;
            parent.Destroy(DestroyMode.KillFinalize);
        }
    }
}