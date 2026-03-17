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

        protected PlanetTile _start = PlanetTile.Invalid;
        protected PlanetTile _end = PlanetTile.Invalid;
        public PlanetTile start { get => _start; set => _start = value; }
        public PlanetTile end { get => _end; set => _end = value; }
        public abstract void Setup(PlanetTile origin, PlanetTile destination);
        public abstract void Arrive();

        protected bool isTraveling = false;

        private const float TravelSpeed = 0.00025f;

        public void StartTraveling()
        {
            isTraveling = true;
        }
        protected override void Tick()
        {
            Log.Message($"TravelingObject Tick: {progress}");
            base.Tick();
            if (isTraveling)
            {
                float step = ComputeTravelStep();
                progress = Mathf.Clamp01(progress + step);

                if (progress >= 1f)
                {
                    isTraveling = false;
                    Arrive();
                }
            }
        }

        private float ComputeTravelStep()
        {
            Vector3 startPoint = Find.WorldGrid.GetTileCenter(start);
            Vector3 endPoint = Find.WorldGrid.GetTileCenter(end);
            if (startPoint == endPoint)
                return 1f;
            float dist = GenMath.SphericalDistance(startPoint.normalized, endPoint.normalized);
            if (dist == 0f)
                return 1f;
            return TravelSpeed / dist;
        }
    }

    public class WorldObject_Transfer : TravelingObject
    {
        private float traveledPct;

        private Vector3 Start => Find.WorldGrid.GetTileCenter(_start);
        private Vector3 End => Find.WorldGrid.GetTileCenter(_end);
        public override Vector3 DrawPos => Vector3.Slerp(Start, End, traveledPct);


        public override void Setup(PlanetTile origin, PlanetTile destination)
        {
            _start = origin;
            _end = destination;
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
            Scribe_Values.Look(ref _start, "initialTile");
            Scribe_Values.Look(ref _end, "destTile");
        }
    }
}
