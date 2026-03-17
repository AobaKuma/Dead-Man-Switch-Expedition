using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public abstract class TravelingObject : WorldObject, ITravelingShip
    {
        public WorldObject worldObject;
        public override Material Material => this.worldObject?.Material;
        public override Material ExpandingMaterial => this.worldObject?.ExpandingMaterial;
        public override Texture2D ExpandingIcon => this.worldObject?.ExpandingIcon;
        public override Color ExpandingIconColor => this.worldObject.ExpandingIconColor;
        public override string Label => this.worldObject?.Label;
        public override string GetDescription()
        {
            return this.worldObject?.GetDescription();
        }


        protected PlanetTile _start = PlanetTile.Invalid;
        protected PlanetTile _end = PlanetTile.Invalid;
        public PlanetTile start { get => _start; set => _start = value; }
        public PlanetTile end { get => _end; set => _end = value; }

        protected float progress = 0;

        public float Progress => progress;

        private Vector3 StartPos => Find.WorldGrid.GetTileCenter(_start);
        private Vector3 EndPos => Find.WorldGrid.GetTileCenter(_end);
        public override Vector3 DrawPos => Vector3.Slerp(StartPos, EndPos, progress);


        public abstract void Setup(PlanetTile origin, PlanetTile destination);

        public override void SpawnSetup()
        {
            base.SpawnSetup();
            Patch_Visible.WO.Add(this);
        }
        public virtual void Arrive()
        {
            worldObject.Tile = this.end;
            if (this.worldObject is MapParent parent && parent.Map is Map map
                && map.GetComponent<MapComponent_Ship>() is MapComponent_Ship comp)
            {
                comp.End();
            }
            this.Destroy();
            Patch_Visible.WO.Remove(this);
        }

        protected bool isTraveling = false;

        public void StartTraveling()
        {
            isTraveling = true;
        }
        protected override void Tick()
        {
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
            Vector3 start = StartPos;
            Vector3 end = EndPos;
            if (start == end)
            {
                return 1f;
            }
            float num = GenMath.SphericalDistance(start.normalized, end.normalized);
            if (num == 0f)
            {
                return 1f;
            }
            return 0.00025f / num;
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref this.worldObject, "worldObject");
            Scribe_Values.Look(ref progress, "progress");
            Scribe_Values.Look(ref _start, "initialTile");
            Scribe_Values.Look(ref _end, "destTile");
        }
    }

    public class WorldObject_Transfer : TravelingObject
    {
        public override void Setup(PlanetTile origin, PlanetTile destination)
        {
            _start = origin;
            _end = destination;
            progress = 0f;
            Tile = origin;
        }
    }
}
