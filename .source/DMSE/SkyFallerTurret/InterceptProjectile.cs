using DMSE.SkyFallerTurret;
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
    public class InterceptProjectile : Skyfaller
    {
        protected override void LeaveMap()
        {
            if (Rand.Chance(HitChance))
            {
                var comp = this.Map.GetComponent<MapComponent_InterceptSkyfaller>();
                var pod = comp.Pods.Find(p => p.pod == this.faller);
                if (pod != null)
                {
                    comp.Pods.Remove(pod);
                    Pawn pawn = null;
                    if (pod.pod.innerContainer.First() is ActiveTransporter transporter &&
                        transporter.Contents.SingleContainedThing  is Pawn pawn2)
                    {
                        pawn = pawn2;
                    }
                    if (pod.pod.innerContainer.First() is Pawn p)
                    {
                        pawn = p;
                    }
                    if (pawn != null) 
                    {
                        pawn.Kill(new DamageInfo(DamageDefOf.Blunt, 50f));
                        var info = new ActiveTransporterInfo();
                        Corpse corpse = null;
                        if (pawn.Corpse != null) 
                        {
                            corpse = pawn.Corpse;
                        }
                        if (corpse == null)
                        {
                            corpse = pawn.MakeCorpse(null, null);
                        }
                        if (corpse == null)
                        {
                            if (Prefs.DevMode)
                            {
                                Log.Message("尸体生成失败");
                            }
                        }
                        else 
                        {
                            if (Prefs.DevMode)
                            {
                                Log.Message("尸体" + corpse);
                            }
                        }
                        if (corpse.holdingOwner != null)
                        {
                            corpse.holdingOwner.Remove(corpse);
                        }
                        if (info.innerContainer.TryAdd(corpse)) 
                        {
                            if (Prefs.DevMode)
                            {
                                Log.Message("尸体进入残骸成功");
                            } 
                        }
                        else if (Prefs.DevMode)
                        {
                            Log.Message("尸体进入残骸失败");
                        }
                        DropPodUtility.MakeDropPodAt(pod.position, this.Map, info);
                        if (Prefs.DevMode)
                        {
                            Log.Message("生成残骸空投" + pod.position);
                        }
                    }
                    if (Prefs.DevMode)
                    {
                        Log.Message("拦截成功");
                    }
                }
            }
            else if (Prefs.DevMode)
            {
                Log.Message("拦截失败");
            }
            base.LeaveMap();
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
            Scribe_References.Look(ref this.faller, "faller");
        }

        public Skyfaller faller;

        public static float HitChance = 0.9f;
    }
}
