using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public abstract class TravelingObject : WorldObject, ITravelingShip
    {
        public WorldObject worldObject;
        public float progress { get; set; }
        public PlanetTile destinationTile { get; }
        public abstract void Setup(PlanetTile origin, PlanetTile destination);
        public abstract void Arrive();
        public  WorldObject WO { get; set; }
    }

    public class WorldObject_Transfer : TravelingObject
    {
        private float traveledPct;
        private PlanetTile initialTile = PlanetTile.Invalid;
        private PlanetTile destTile = PlanetTile.Invalid;

        private Vector3 Start => Find.WorldGrid.GetTileCenter(initialTile);
        private Vector3 End => Find.WorldGrid.GetTileCenter(destTile);
        public override Vector3 DrawPos => Vector3.Slerp(Start, End, traveledPct);


        public override void Setup(PlanetTile origin, PlanetTile destination)
        {
            initialTile = origin;
            destTile = destination;
            traveledPct = 0f;
            Tile = origin;
        }

        public override void Arrive()
        {
            if (Spawned)
                Find.WorldObjects.Remove(this);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref traveledPct, "traveledPct");
            Scribe_Values.Look(ref initialTile, "initialTile");
            Scribe_Values.Look(ref destTile, "destTile");
        }
    }
}
