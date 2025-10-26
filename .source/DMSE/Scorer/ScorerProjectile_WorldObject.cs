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
    public class ScorerProjectile_WorldObject : WorldObject
    {
        private Vector3 Start
        {
            get
            {
                return Find.WorldGrid.GetTileCenter(initialTile);
            }
        }
        private Vector3 End
        {
            get
            {
                return Find.WorldGrid.GetTileCenter(destinationTile);
            }
        }
        public override Vector3 DrawPos
        {
            get
            {
                return Vector3.Slerp(Start, End, traveledPct);
            }
        }
        public override bool ExpandingIconFlipHorizontal
        {
            get
            {
                return GenWorldUI.WorldToUIPosition(Start).x > GenWorldUI.WorldToUIPosition(End).x;
            }
        }
        public override Color ExpandingIconColor
        {
            get
            {
                return base.Faction.Color;
            }
        }
        public override float ExpandingIconRotation
        {
            get
            {
                if (!this.def.rotateGraphicWhenTraveling)
                {
                    return base.ExpandingIconRotation;
                }
                Vector2 vector = GenWorldUI.WorldToUIPosition(this.Start);
                Vector2 vector2 = GenWorldUI.WorldToUIPosition(this.End);
                float num = Mathf.Atan2(vector2.y - vector.y, vector2.x - vector.x) * 57.29578f;
                if (num > 180f)
                {
                    num -= 180f;
                }
                return num + 90f;
            }
        }
        private float TraveledPctStepPerTick
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
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look<PlanetTile>(ref this.destinationTile, "destinationTile", default(PlanetTile), false);
            Scribe_Values.Look<bool>(ref this.arrived, "arrived", false, false);
            Scribe_Values.Look<PlanetTile>(ref this.initialTile, "initialTile", default(PlanetTile), false);
            Scribe_Values.Look<float>(ref this.traveledPct, "traveledPct", 0f, false);
        }
        public override void PostAdd()
        {
            base.PostAdd();
            this.initialTile = base.Tile;
        }
        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            this.traveledPct += this.TraveledPctStepPerTick * (float)delta;
            if (this.traveledPct >= 1f)
            {
                this.traveledPct = 1f;
                this.Arrived();
            }
        }
        private void Arrived()
        {
            if (arrived)
            {
                return;
            }
            arrived = true;
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(destinationTile, WorldObjectDefOf.Camp);
            Current.Game.GetComponent<GameComponent_MissileEngage>().times.SetOrAdd(map,
                Find.TickManager.TicksGame + (GenDate.TicksPerDay * 2));
            CameraJumper.TryJump(map.Center, map);
            ThingDef def = ThingDef.Named(ProjectileDefName);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.Targeter.BeginTargeting(new TargetingParameters()
            {
                canTargetLocations = true,
                canTargetItems = false
            }, t =>
                {
                    SkyfallerMaker.SpawnSkyfaller(def, t.Cell, map);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                }, t => Find.TickManager.CurTimeSpeed = TimeSpeed.Paused);
            this.Destroy();
        }

        public PlanetTile destinationTile = PlanetTile.Invalid;
        private bool arrived;
        private PlanetTile initialTile = PlanetTile.Invalid;
        private float traveledPct;
        private const float TravelSpeed = 0.00025f;

        private const string ProjectileDefName = "MeteoriteIncoming";
        //选中并生成的坠落物定义，用原版的陨石当占位符
    }
}