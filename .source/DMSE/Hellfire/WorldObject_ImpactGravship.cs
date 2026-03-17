using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class WorldObject_ImpactGravship : WorldObject, ITravelingShip
    {
        private float traveledPct;
        private PlanetTile initialTile = PlanetTile.Invalid;
        private PlanetTile destTile = PlanetTile.Invalid;

        // 新增：紀錄引擎，供撞擊時計算 Thruster 數量
        public Building_GravEngine engine;

        public float progress
        {
            get => traveledPct;
            set => traveledPct = value;
        }

        public PlanetTile destinationTile => destTile;

        private Vector3 Start => Find.WorldGrid.GetTileCenter(initialTile);
        private Vector3 End => Find.WorldGrid.GetTileCenter(destTile);
        public override Vector3 DrawPos => Vector3.Slerp(Start, End, traveledPct);

        public void Setup(PlanetTile origin, PlanetTile destination)
        {
            initialTile = origin;
            destTile = destination;
            traveledPct = 0f;
            Tile = origin;
        }

        public void Arrive()
        {
            // TODO: impact-specific arrival logic (crater generation, etc.)
            if (Spawned)
                Find.WorldObjects.Remove(this);
        }

        // No TickInterval override — MapComponent_Ship drives progress.

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref traveledPct, "traveledPct");
            Scribe_Values.Look(ref initialTile, "initialTile");
            Scribe_Values.Look(ref destTile, "destTile");
        }
    }
}