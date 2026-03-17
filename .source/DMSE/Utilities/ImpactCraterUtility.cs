using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public static class ImpactCraterUtility
    {
        public static IImpactCraterService Service { get; private set; }

        public static void Initialize(PlayerConfigSettings settings)
        {
            Service = new ImpactCraterService(settings);
        }

        /// <summary>
        /// WorldGenStep entry: apply configured craters by seed.
        /// </summary>
        public static int ApplyConfiguredCraters(string seedString, PlanetLayer layer, string mutatorDefName)
        {
            if (Service == null || layer == null || seedString.NullOrEmpty()) return 0;

            TileMutatorDef mutatorDef = DefDatabase<TileMutatorDef>.GetNamedSilentFail(mutatorDefName);
            if (mutatorDef == null)
            {
                Log.Warning($"[ImpactCrater] TileMutatorDef not found: {mutatorDefName}");
                return 0;
            }

            return Service.ApplyToWorld(layer, seedString, mutatorDef);
        }

        /// <summary>
        /// Runtime: apply an impact crater mutator to a specific world tile.
        /// </summary>
        public static void ApplyImpactCraterAtTile(PlanetTile planetTile)
        {
            if (!planetTile.Valid) return;
            if (planetTile.LayerDef.isSpace) return;

            TileMutatorDef mutatorDef = DefDatabase<TileMutatorDef>.GetNamedSilentFail("DMSE_ImpactCrater");
            if (mutatorDef == null)
            {
                Log.Warning("[DMSE] TileMutatorDef 'DMSE_ImpactCrater' not found, skipping crater.");
                return;
            }

            planetTile.Tile.AddMutator(mutatorDef);
        }
    }
}