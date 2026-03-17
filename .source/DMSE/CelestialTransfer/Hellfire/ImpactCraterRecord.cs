using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public class ImpactCraterRecord : IExposable
    {
        public string planetSeedString;
        public string craterName;
        public float longitude;       // -180 ~ 180
        public float latitude;        // -90 ~ 90
        public int radiusInTiles = 0; // 0=只有中心tile
        public bool enabled = true;

        public void ExposeData()
        {
            Scribe_Values.Look(ref planetSeedString, "planetSeedString");
            Scribe_Values.Look(ref craterName, "craterName");
            Scribe_Values.Look(ref longitude, "longitude", 0f);
            Scribe_Values.Look(ref latitude, "latitude", 0f);
            Scribe_Values.Look(ref radiusInTiles, "radiusInTiles", 0);
            Scribe_Values.Look(ref enabled, "enabled", true);
        }

        public bool IsValid()
        {
            return !planetSeedString.NullOrEmpty()
                && !craterName.NullOrEmpty()
                && latitude >= -90f && latitude <= 90f
                && longitude >= -180f && longitude <= 180f
                && radiusInTiles >= 0;
        }

        public string Key => $"{planetSeedString}::{craterName}";
    }

}