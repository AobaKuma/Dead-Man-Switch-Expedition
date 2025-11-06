using RimWorld;
using UnityEngine;
using UnityEngine.UIElements;
using Verse;
using Verse.Noise;
using Verse.Sound;

namespace DMSE
{
    public class CompProperties_EffecterColdLaunch : CompProperties_EffecterBase
    {
        public float offsetY = -1.5f;
        public int delayTicks = 10;
        public SoundDef ignitionSound;
        public EffecterDef launchEffectTrigger;
        public FleckDef ExhaustFleck;
        public SimpleCurve ExhaustCurve;

        public FleckDef SmokeFleck;
        public SimpleCurve SmokeCurve;
        public CompProperties_EffecterColdLaunch()
        {
            compClass = typeof(CompEffecterColdLaunch);
        }
    }

    public class CompEffecterColdLaunch : ThingComp
    {
        private int ticksSinceSpawn = 0;
        private Effecter effecter;
        private ScorerProjectile skyfaller;

        public CompProperties_EffecterColdLaunch Props => (CompProperties_EffecterColdLaunch)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            skyfaller = parent as ScorerProjectile;
        }
        protected virtual bool ShouldShowEffecter()
        {
            if (parent.Spawned && skyfaller.DrawPos.InBounds(parent.Map) && ticksSinceSpawn >= Props.delayTicks)
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
                if (ticksSinceSpawn == Props.delayTicks)
                {
                    Props.launchEffectTrigger?.Spawn(skyfaller.DrawPos.ToIntVec3(), parent.Map).Trigger(skyfaller, skyfaller);
                    Props.ignitionSound?.PlayOneShot(new TargetInfo(skyfaller.trueDrawPos.ToIntVec3(), parent.Map));
                }
                if (effecter == null)
                {
                    effecter = Props.effecterDef?.Spawn(parent, parent.Map);
                }
                effecter?.EffectTick(skyfaller, skyfaller);

                float evaluate = (float)ticksSinceSpawn / (float)(skyfaller.ticksToDiscard + Props.delayTicks);
                ThrowExhaust(skyfaller.trueDrawPos, skyfaller.trueRotation, evaluate, skyfaller.def.skyfaller.speedCurve.Evaluate(evaluate));
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

            // 預先計算共用參數
            float timeFactor = Mathf.Clamp01(1f - (evaluate + 0.1f));
            float exhaustBaseScale = Props.ExhaustCurve?.Evaluate(evaluate) ?? 0f;
            float smokeBaseScale = Props.SmokeCurve?.Evaluate(evaluate) ?? 0f;

            for (int i = 0; i < 3; i++)
            {
                float randomFactor = Rand.RangeSeeded(-1, 1, GenTicks.TicksGame);

                // 處理排氣效果
                if (Props.ExhaustFleck != null)
                {
                    float actualScale = Rand.Gaussian(exhaustBaseScale, 1)* timeFactor;
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

                // 處理煙霧效果
                if (Props.SmokeFleck != null)
                {
                    FleckCreationData smokeData = FleckMaker.GetDataStatic(drawPos + offset, parent.Map, Props.SmokeFleck);

                    for (int j = 0; j < 2; j++)
                    {
                        float angleOffset = randomFactor * timeFactor * 180f - 180f; // -180 到 180 之間
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
        }
    }
}