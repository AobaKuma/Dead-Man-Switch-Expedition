using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace DMSE
{
    public class ScorerProjectile : Skyfaller
    {
        protected override void LeaveMap()
        {
            base.LeaveMap();
            Find.WorldObjects.Add(this.worldObject);
            CameraJumper.TryJump(this.worldObject);
        }
        protected override void GetDrawPositionAndRotation(ref Vector3 drawLoc, out float extraRotation)
        {
            extraRotation = 0f;  
            if (def.skyfaller.zPositionCurve != null && (this.Rotation == Rot4.East || this.Rotation == Rot4.West))
            {
                drawLoc.z += this.def.skyfaller.zPositionCurve.Evaluate(this.TimeInAnimation);
            } 
            switch (base.Rotation.AsInt)
            {
                case 1: 
                    extraRotation += this.def.skyfaller.rotationCurve.Evaluate(base.TimeInAnimation);
                    break;
                case 3:
                    extraRotation -= this.def.skyfaller.rotationCurve.Evaluate(base.TimeInAnimation);
                    break;
            } 
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref this.worldObject, "worldObject");
        }

        public ScorerProjectile_WorldObject worldObject;
    }
}
