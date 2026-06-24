using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>階段二：火控雷達。提供火力通道（最大目標數）並決定中過程攔截命中率。</summary>
    public class CompProperties_FireControlRadar : CompProperties
    {
        /// <summary>最大目標數 = 同時可導引的火力通道數。</summary>
        public int maxTargets = 1;

        /// <summary>反隱身等級：低於目標隱身值時命中率打折。</summary>
        public int antiStealthLevel = 0;

        /// <summary>功率等級（保留供低空目標偵測距離擴充用）。</summary>
        public int powerLevel = 1;

        /// <summary>導引精準時間（ticks）：可用導引時間達到此值時接近最大命中率。</summary>
        public float guidanceAccuracyTime = 600f;

        /// <summary>最大有效距離（以剩餘時間 ticks 近似）：剩餘時間大於此值時無法導引。</summary>
        public int maxRangeTicks = 6000;

        /// <summary>導引充分時的最大命中率。</summary>
        public float maxHitChance = 0.9f;

        /// <summary>反隱身不足時命中率的折扣係數。</summary>
        public float stealthMissFactor = 0.5f;

        public CompProperties_FireControlRadar()
        {
            compClass = typeof(CompFireControlRadar);
        }
    }

    public class CompFireControlRadar : CompBVRDevice
    {
        public CompProperties_FireControlRadar Props => (CompProperties_FireControlRadar)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_BVRCombat m = Manager;
            if (m != null) { m.fireControlRadars.Add(this); }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_BVRCombat m = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (m != null) { m.fireControlRadars.Remove(this); }
        }

        /// <summary>依可用導引時間與目標隱身計算單發攔截彈的命中率。</summary>
        public float ComputeHitChance(BVRTarget target, int guideTimeLeft)
        {
            float guide = Mathf.Clamp01(guideTimeLeft / Mathf.Max(1f, Props.guidanceAccuracyTime));
            float chance = Props.maxHitChance * guide;
            if (Props.antiStealthLevel < target.stealthLevel)
            {
                chance *= Props.stealthMissFactor;
            }
            return Mathf.Clamp01(chance);
        }

        public override string CompInspectStringExtra()
        {
            return "DMSE.BVR.FireControl".Translate(Props.maxTargets,
                Active ? "DMSE.BVR.Online".Translate() : "DMSE.BVR.Offline".Translate());
        }
    }
}
