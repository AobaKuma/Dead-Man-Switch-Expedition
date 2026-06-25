using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 發射動畫介面：提供發射特效（排氣火焰、煙霧）所需的「實際繪製位置與旋轉」。
    /// 巡航導彈與攔截彈皆實作，使各種發射特效（冷射/熱射）可共用同一套動畫。
    /// </summary>
    public interface ILaunchTarget
    {
        Vector3 LaunchDrawPos { get; }
        float LaunchRotation { get; }
    }

    /// <summary>發射特效共用參數。</summary>
    public class CompProperties_EffecterLaunch : CompProperties_EffecterBase
    {
        public float offsetY = -1.5f;
        public int delayTicks = 10;              // 點火前延遲（冷射的彈射段；熱射為 0）
        public SoundDef ignitionSound;
        public EffecterDef launchEffectTrigger;  // 點火瞬間的發射特效
        public FleckDef ExhaustFleck;
        public SimpleCurve ExhaustCurve;
        public FleckDef SmokeFleck;
        public SimpleCurve SmokeCurve;
    }

    /// <summary>發射特效共用基類：依發射目標的繪製位置噴出排氣火焰與煙霧。</summary>
    public abstract class CompEffecterLaunch : ThingComp
    {
        protected int ticksSinceSpawn = 0;
        protected bool hasIgnited;
        protected Effecter effecter;
        protected Skyfaller skyfaller;
        protected ILaunchTarget launchTarget;

        public CompProperties_EffecterLaunch Props => (CompProperties_EffecterLaunch)props;

        /// <summary>點火前延遲（tick）。冷射 = Props.delayTicks；熱射覆寫為 0（立即點火）。</summary>
        protected virtual int IgnitionDelayTicks => Props.delayTicks;

        /// <summary>發射初期的排氣放大倍率（熱射 > 1，模擬尾管立即全功率噴焰）。</summary>
        protected virtual float InitialBlastScale => 1f;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            skyfaller = parent as Skyfaller;
            launchTarget = parent as ILaunchTarget;
        }

        protected virtual bool ShouldShowEffecter()
        {
            if (skyfaller != null && launchTarget != null && parent.Spawned
                && skyfaller.DrawPos.InBounds(parent.Map) && ticksSinceSpawn >= IgnitionDelayTicks)
            {
                return parent.MapHeld == Find.CurrentMap;
            }
            return false;
        }

        public override void CompTick()
        {
            base.CompTick();
            ticksSinceSpawn++;
            if (ShouldShowEffecter())
            {
                if (!hasIgnited)
                {
                    hasIgnited = true;
                    Props.launchEffectTrigger?.Spawn(skyfaller.DrawPos.ToIntVec3(), parent.Map).Trigger(skyfaller, skyfaller);
                    Props.ignitionSound?.PlayOneShot(new TargetInfo(launchTarget.LaunchDrawPos.ToIntVec3(), parent.Map));
                }
                if (effecter == null)
                {
                    effecter = Props.effecterDef?.Spawn(parent, parent.Map);
                }
                effecter?.EffectTick(skyfaller, skyfaller);

                float evaluate = (float)ticksSinceSpawn / (float)(skyfaller.ticksToDiscard + IgnitionDelayTicks);
                ThrowExhaust(launchTarget.LaunchDrawPos, launchTarget.LaunchRotation, evaluate,
                    skyfaller.def.skyfaller.speedCurve.Evaluate(evaluate));
            }
            else
            {
                effecter?.Cleanup();
                effecter = null;
            }
        }

        public void ThrowExhaust(Vector3 drawPos, float rotation, float evaluate, float speed)
        {
            var offset = new Vector3(Mathf.Sin(rotation * Mathf.Deg2Rad), 0, Mathf.Cos(rotation * Mathf.Deg2Rad)) * Props.offsetY;

            if (!skyfaller.Position.ShouldSpawnMotesAt(parent.MapHeld))
            {
                return;
            }

            // 發射初期的爆發放大（熱射用），隨進度淡回 1。
            float blast = Mathf.Lerp(InitialBlastScale, 1f, Mathf.Clamp01(evaluate * 3f));

            float timeFactor = Mathf.Clamp01(1f - (evaluate + 0.1f));
            float exhaustBaseScale = (Props.ExhaustCurve?.Evaluate(evaluate) ?? 0f) * blast;
            float smokeBaseScale = (Props.SmokeCurve?.Evaluate(evaluate) ?? 0f) * blast;

            for (int i = 0; i < 3; i++)
            {
                float randomFactor = Rand.RangeSeeded(-1, 1, GenTicks.TicksGame);

                if (Props.ExhaustFleck != null)
                {
                    float actualScale = Rand.Gaussian(exhaustBaseScale, 1) * timeFactor;
                    FleckCreationData exhaustData = FleckMaker.GetDataStatic(drawPos + offset, parent.Map, Props.ExhaustFleck);
                    exhaustData.scale = actualScale;
                    exhaustData.velocityAngle = rotation + randomFactor * 5;
                    exhaustData.velocitySpeed = Mathf.Clamp01(exhaustBaseScale - actualScale) * speed;

                    float speedFactor = 1f - Mathf.Clamp01(exhaustData.velocitySpeed / speed);
                    exhaustData.solidTimeOverride = 0.4f * (timeFactor + speedFactor);
                    exhaustData.targetSize = actualScale * timeFactor * 3f;

                    parent.MapHeld.flecks.CreateFleck(exhaustData);

                    for (int j = 0; j < 5; j++)
                    {
                        exhaustData.spawnPosition += offset / 2;
                        parent.MapHeld.flecks.CreateFleck(exhaustData);
                    }
                }

                if (Props.SmokeFleck != null)
                {
                    FleckCreationData smokeData = FleckMaker.GetDataStatic(drawPos + offset, parent.Map, Props.SmokeFleck);

                    for (int j = 0; j < 2; j++)
                    {
                        float angleOffset = randomFactor * timeFactor * 180f - 180f;
                        float angleFactor = 1f - (Mathf.Abs(angleOffset) / 180f);
                        smokeData.scale = Rand.Gaussian(smokeBaseScale, 2f) * (angleFactor + timeFactor);
                        smokeData.velocityAngle = rotation + (angleOffset) + (Rand.Gaussian(0, 20) * timeFactor);
                        smokeData.velocitySpeed = (smokeBaseScale - smokeData.scale) * (angleFactor + timeFactor);
                        smokeData.rotationRate = smokeData.velocitySpeed;
                        smokeData.solidTimeOverride = smokeData.scale * (timeFactor + angleFactor);
                        parent.MapHeld.flecks.CreateFleck(smokeData);
                    }
                }
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref ticksSinceSpawn, "ticksSinceSpawn", 0);
            Scribe_Values.Look(ref hasIgnited, "hasIgnited", false);
        }
    }

    // ==================== 冷射（彈射段後點火，巡航導彈用） ====================
    public class CompProperties_EffecterColdLaunch : CompProperties_EffecterLaunch
    {
        public CompProperties_EffecterColdLaunch()
        {
            compClass = typeof(CompEffecterColdLaunch);
        }
    }

    public class CompEffecterColdLaunch : CompEffecterLaunch
    {
    }
}
