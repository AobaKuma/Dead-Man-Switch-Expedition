using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 中過程攔截彈（視覺 skyfaller）。離場時依 <see cref="hitChance"/> 擲骰：
    /// 成功則乾淨擊毀目標（不生成殘骸）；無論成敗都釋放占用的火力通道。
    /// </summary>
    public class InterceptProjectile : Skyfaller, IMissileFlight, IInterceptorShot
    {
        public int targetId = -1;
        public float hitChance = 0.9f;
        public Vector3 launchOffset;

        public int TargetId => targetId;
        public float HitChance { get => hitChance; set => hitChance = value; }

        public Vector3 trueDrawPos = Vector3.zero;
        public float trueRotation = 0f;

        public Vector3 LaunchDrawPos => trueDrawPos;
        public float LaunchRotation => trueRotation;
        public Vector3 VisualPos => DrawPos;
        public IntVec3 GroundCell => Position;
        public int AscentTicks => ticksToDiscard;
        public float EffectSpeed(float evaluate) => def.skyfaller.speedCurve.Evaluate(evaluate);
        public float EffectEvaluate(int ticksSinceSpawn, int ignitionDelayTicks) => (float)ticksSinceSpawn / (float)(ticksToDiscard + ignitionDelayTicks);

        /// <summary>依目前時間軸計算 <see cref="DrawAt"/> 會得到的位置與朝向（供 <see cref="MissileFlightTrace"/> 比對）。</summary>
        public void ComputeLaunchPose(out Vector3 pos, out float rotation)
        {
            pos = DrawPos;
            GetDrawPositionAndRotation(ref pos, out float extraRotation);
            rotation = Rotation.AsAngle + extraRotation;
        }

        protected override void Tick()
        {
            base.Tick();
            if (MissileFlightTrace.Active)
            {
                ComputeLaunchPose(out Vector3 pos, out float rot);
                MissileFlightTrace.RecordTick(this, ticksToImpact, pos, rot);
            }
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            GetDrawPositionAndRotation(ref drawLoc, out float extraRotation);
            trueDrawPos = drawLoc;
            trueRotation = Rotation.AsAngle + extraRotation;
        }

        protected override void LeaveMap()
        {
            MapComponent_BVRCombat comp = Map != null ? Map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp != null && targetId >= 0)
            {
                BVRTarget target = comp.GetTarget(targetId);
                if (target != null)
                {
                    // 僅在此目標最後一枚攔截彈結算後釋放火力通道。
                    target.NotifyInterceptorResolved();

                    if (Rand.Chance(hitChance))
                    {
                        // 地圖外攔截光效與音效（中過程）。
                        MapComponent_InterceptEffects.Trigger(Map, InterceptEventData.From(target, Map, terminal: false, IntVec3.Invalid));
                        comp.RemoveTarget(targetId); // 中過程攔截成功：乾淨消滅，不留殘骸。
                        if (Prefs.DevMode) { Log.Message($"[DMSE BVR] 中過程攔截成功 target#{targetId}"); }
                    }
                    else if (Prefs.DevMode)
                    {
                        Log.Message($"[DMSE BVR] 中過程攔截失敗 target#{targetId}");
                    }
                }
            }
            base.LeaveMap();
        }

        protected override void GetDrawPositionAndRotation(ref Vector3 drawLoc, out float extraRotation)
        {
            drawLoc += launchOffset;
            extraRotation = 0f;
            if (def.skyfaller.zPositionCurve != null && (Rotation == Rot4.East || Rotation == Rot4.West))
            {
                drawLoc.z += def.skyfaller.zPositionCurve.Evaluate(TimeInAnimation);
            }
            switch (base.Rotation.AsInt)
            {
                case 1:
                    extraRotation += def.skyfaller.rotationCurve.Evaluate(base.TimeInAnimation);
                    break;
                case 3:
                    extraRotation -= def.skyfaller.rotationCurve.Evaluate(base.TimeInAnimation);
                    break;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref targetId, "targetId", -1);
            Scribe_Values.Look(ref hitChance, "hitChance", 0.9f);
            Scribe_Values.Look(ref launchOffset, "launchOffset", Vector3.zero);
        }
    }
}
