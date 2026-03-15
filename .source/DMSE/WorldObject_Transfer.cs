using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class WorldObject_Transfer : WorldObject
    {
        private Vector3 Start
        {
            get
            {
                return Find.WorldGrid.GetTileCenter(this.start);
            }
        } 
        private Vector3 End
        {
            get
            {
                return Find.WorldGrid.GetTileCenter(this.end);
            }
        } 
        public override Vector3 DrawPos
        {
            get
            {
                return Vector3.Slerp(this.Start, this.End, this.progress);
            }
        }
        public override void SpawnSetup()
        {
            base.SpawnSetup();
            Patch_Visible.WO.Add(this); 
        }
        public override Material Material => this.worldObject?.Material;
        public override Material ExpandingMaterial => this.worldObject?.ExpandingMaterial;
        public override Texture2D ExpandingIcon => this.worldObject?.ExpandingIcon;
        public override Color ExpandingIconColor => this.worldObject.ExpandingIconColor;
        public override string Label => this.worldObject?.Label;
        public override string GetDescription()
        {
            return this.worldObject?.GetDescription();
        }
        public float TraveledPctStepPerTick
        {
            get
            {
                Vector3 start = this.Start;
                Vector3 end = this.End;
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
        }
        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            this.progress += this.TraveledPctStepPerTick * (float)delta;
            if (this.progress >= 1f)
            {
                this.progress = 1f;
                this.worldObject.Tile = this.end;
                if (this.worldObject is MapParent parent && parent.Map is Map map
                    && map.GetComponent<MapComponent_Ship>() is MapComponent_Ship comp) 
                {
                    comp.End();
                }
                this.Destroy();
                Patch_Visible.WO.Remove(this);
            }
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref this.worldObject, "worldObject");
            Scribe_Values.Look(ref this.progress, "progress");
            Scribe_Values.Look(ref this.start, "start");
            Scribe_Values.Look(ref this.end, "end");
        }


        public float progress;
        public PlanetTile start;
        public PlanetTile end;
        public WorldObject worldObject;
    }
}
