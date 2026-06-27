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

        /// <summary>功率等級：與目標隱身值對衝；每超出 1 級隱身（stealthLevel - powerLevel）以 stealthMissFactor 遞減命中率，並延長鎖定時間。</summary>
        public int powerLevel = 1;

        /// <summary>導引精準時間（ticks）：可用導引時間達到此值時接近最大命中率。</summary>
        public float guidanceAccuracyTime = 600f;

        /// <summary>最大有效距離（以剩餘時間 ticks 近似）：剩餘時間大於此值時無法導引。</summary>
        public int maxRangeTicks = 6000;

        /// <summary>導引充分時的最大命中率。</summary>
        public float maxHitChance = 0.9f;

        /// <summary>每級隱身勝出（對衝後）對命中率的折扣係數。</summary>
        public float stealthMissFactor = 0.5f;

        /// <summary>基礎鎖定時間（ticks）：新目標計入火力通道後需鎖定此時間才發射；受隱身對衝與速度延長。</summary>
        public int lockOnTicks = 120;

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

        // 速度對命中率的影響參數。
        private const float SpeedRef = 15f;            // 此速度（含以下）無懲罰。
        private const float SpeedPenaltyPerUnit = 0.012f;
        private const float MinSpeedFactor = 0.2f;

        /// <summary>依導引時間、目標速度與隱身對衝計算單發攔截彈的命中率。</summary>
        public float ComputeHitChance(BVRTarget target, int guideTimeLeft)
        {
            float guide = Mathf.Clamp01(guideTimeLeft / Mathf.Max(1f, Props.guidanceAccuracyTime));
            float chance = Props.maxHitChance * guide;

            // 速度：越快越難命中。
            float speedFactor = Mathf.Clamp(1f - Mathf.Max(0f, target.speed - SpeedRef) * SpeedPenaltyPerUnit, MinSpeedFactor, 1f);
            chance *= speedFactor;

            // 隱身對衝：powerLevel 抵銷 stealthLevel，每超出 1 級以 stealthMissFactor 遞減。
            int stealthGap = Mathf.Max(0, target.stealthLevel - Props.powerLevel);
            if (stealthGap > 0)
            {
                chance *= Mathf.Pow(Props.stealthMissFactor, stealthGap);
            }

            return Mathf.Clamp01(chance);
        }

        /// <summary>新目標計入火力通道後所需的鎖定時間（ticks）：基礎時間受隱身對衝與速度延長。</summary>
        public int LockOnTicksFor(BVRTarget target)
        {
            int stealthGap = Mathf.Max(0, target.stealthLevel - Props.powerLevel);
            float mult = 1f + 0.5f * stealthGap + Mathf.Max(0f, target.speed - SpeedRef) / 60f;
            return Mathf.Max(1, Mathf.RoundToInt(Props.lockOnTicks * mult));
        }

        public override string CompInspectStringExtra()
        {
            return "DMSE.BVR.FireControl".Translate(Props.maxTargets,
                Active ? "DMSE.BVR.Online".Translate() : "DMSE.BVR.Offline".Translate());
        }
    }
}
