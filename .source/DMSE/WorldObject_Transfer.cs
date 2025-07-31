using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
        public override Material Material => this.worldObjec?.Material;
        public override Material ExpandingMaterial => this.worldObjec?.ExpandingMaterial;
        public override Texture2D ExpandingIcon => this.worldObjec?.ExpandingIcon;
        public override Color ExpandingIconColor => this.worldObjec.ExpandingIconColor;
        public override string Label => this.worldObjec?.Label;
        public override string GetDescription()
        {
            return this.worldObjec?.GetDescription();
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
                this.worldObjec.Tile = this.end;
                if (this.worldObjec is MapParent parent && parent.Map is Map map
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
            Scribe_References.Look(ref this.worldObjec, "worldObjec");
            Scribe_Values.Look(ref this.progress, "progress");
            Scribe_Values.Look(ref this.start, "start");
            Scribe_Values.Look(ref this.end, "end");
        }


        public float progress;
        public PlanetTile start;
        public PlanetTile end;
        public WorldObject worldObjec;
    }
}
