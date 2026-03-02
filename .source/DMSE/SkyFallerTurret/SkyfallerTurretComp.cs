using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class SkyfallerTurretCompProperties : CompProperties
    {
        public SkyfallerTurretCompProperties() 
        {
            this.compClass = typeof(SkyfallerTurretComp);
        }
        public ThingDef projectile;
        public int cooldown = 600;
        public int countLimit = 3;

        //最后N秒内仍然有未拦截的时候触发
        public ThingDef projectile_Last;
        public float lastInterceptTick = 200;
        public int countLimitForLast = 5;
        public float interceptChance = 0.5f;
        
        // Guidance law parameters
        public float guidanceVelocity = 20f; // 导弹速度 (Projectile velocity)
        public float effectiveCrossSection = 10f; // 有效面积系数 (Effective cross section coefficient)
        public float interceptionThreshold = 0.5f; // 拦截阈值 (Interception threshold)
    }

    public class SkyfallerTurretComp : ThingComp
    {
        public SkyfallerTurretCompProperties Props => (SkyfallerTurretCompProperties)this.props;
        
        /// <summary>
        /// Calculate guidance-based fire window using radar calculations
        /// </summary>
        public float CalculateFireWindow(float remainingTime)
        {
            // Calculate projectile arrival time
            float arrivalTime = RadarUtility.CalculateArrivalTime(
                Props.effectiveCrossSection * Props.guidanceVelocity,
                0,
                Props.guidanceVelocity);

            // Calculate interception window
            return RadarUtility.CalculateInterceptionWindow(
                Props.effectiveCrossSection * Props.guidanceVelocity,
                Props.guidanceVelocity,
                remainingTime);
        }

        /// <summary>
        /// Calculate radar coverage contribution for this turret
        /// </summary>
        public float CalculateCoverageContribution(float distance)
        {
            float h = Props.effectiveCrossSection;
            float w = Mathf.Max(0, h - distance);
            return RadarUtility.CalculateRadarCrossSection(h, w);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            var list = map.GetComponent<MapComponent_InterceptSkyfaller>().turrets;
            if (list.Contains(this)) 
            {
                list.Remove(this);
            }
        }
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            var list = this.parent.Map.GetComponent<MapComponent_InterceptSkyfaller>().turrets;
            if (!list.Contains(this))
            {
                if (Prefs.DevMode) 
                {
                    Log.Message("添加Turrets:" + this.parent.Label);
                }
                list.Add(this);
            }
        }
        public override void CompTick()
        {
            base.CompTick();
            if (this.count < this.Props.countLimit && Find.TickManager.TicksGame >= this.cooldown) 
            {
                this.cooldown = Find.TickManager.TicksGame + this.Props.cooldown;
                this.count++;
            }
            if (this.cooldownLast > 0) 
            {
                this.cooldownLast--;
            }
        }
        public override void PostExposeData()
        {
            base.PostExposeData(); 
            Scribe_Values.Look(ref this.cooldown, "cooldown");
            Scribe_Values.Look(ref this.count, "count");
            Scribe_Values.Look(ref this.cooldownLast, "cooldownLast");
        }

         
        public int cooldown;
        public int count;

        public int cooldownLast;
    }
}
