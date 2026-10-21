using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>離場巡航段的狀態（交接後逐 tick 積分）。</summary>
    public struct MissileCruiseState
    {
        public bool active;
        /// <summary>交接後經過的 tick。</summary>
        public int ticks;
        /// <summary>目前畫面位置（地圖格座標，y 為繪製高度）。</summary>
        public Vector3 pos;
        /// <summary>出發方位（世界航跡在發射地的切線方位）。</summary>
        public float heading;
        /// <summary>交接瞬間的航向、機頭角、速度（格/tick）與動畫進度。</summary>
        public float motion0;
        public float nose0;
        public float speed0;
        public float progress0;
        /// <summary>目前機頭角（繪製與尾焰用）。</summary>
        public float nose;

        public void Expose()
        {
            Scribe_Values.Look(ref active, "cruiseActive", false);
            Scribe_Values.Look(ref ticks, "cruiseTicks", 0);
            Scribe_Values.Look(ref pos, "cruisePos");
            Scribe_Values.Look(ref heading, "cruiseHeading", 0f);
            Scribe_Values.Look(ref motion0, "cruiseMotion0", 0f);
            Scribe_Values.Look(ref nose0, "cruiseNose0", 0f);
            Scribe_Values.Look(ref speed0, "cruiseSpeed0", 0f);
            Scribe_Values.Look(ref progress0, "cruiseProgress0", 0f);
            Scribe_Values.Look(ref nose, "cruiseNose", 0f);
        }
    }

    /// <summary>
    /// 導彈離場的運動學（docs/Missile_Skyfaller轉Projectile_計畫.md §8.3）。純函式：
    /// <see cref="Projectile_MissileBase"/> 的實際飛行與 <see cref="SimulateDepartureTicks"/>（波次打擊時間軸估算）共用同一套，
    /// 所以預估的離場 tick 與實際相同。
    ///
    /// 升空段：逐 tick 重算原版 Skyfaller 公式（<see cref="AscentPose"/>），與舊 ScorerProjectile 相同。
    /// 交接：動畫進度達 <see cref="MissileFlightExtension.ascentHandoff"/> 或畫面位移達 handoffMaxCells 時。
    /// 巡航段：從交接位置與速度出發，航向與機頭以 smoothstep 在 turnTicks 內轉到出發方位；
    /// 速度 = 交接速度 × speedCurve(進度) / speedCurve(交接進度)，即升空段的加速曲線在巡航段繼續生效（進度超過 1 後取曲線末值）。
    /// 畫面位置離開地圖即離場。
    /// </summary>
    public static class MissileDepartureKinematics
    {
        /// <summary>升空段的動畫進度（同 Skyfaller.TimeInAnimation，reversed）。</summary>
        public static float Progress(int ticksToImpact, int leaveMapAfterTicks) => (float)ticksToImpact / leaveMapAfterTicks;

        /// <summary>升空段的原版 Skyfaller.DrawPos（未套用 z／旋轉曲線）。</summary>
        public static Vector3 AscentDrawPos(MissileFlightExtension f, Vector3 center, int ticksToImpact, float angle, int leaveMapAfterTicks)
        {
            float t = Progress(ticksToImpact, leaveMapAfterTicks);
            float speed = f.speedCurve == null ? f.speed : f.speedCurve.Evaluate(t) * f.speed;
            bool flip = f.flightFlippedHorizontally;
            switch (f.movementType)
            {
                case SkyfallerMovementType.ConstantSpeed:
                    return SkyfallerDrawPosUtility.DrawPos_ConstantSpeed(center, ticksToImpact, angle, speed, flip);
                case SkyfallerMovementType.Decelerate:
                    return SkyfallerDrawPosUtility.DrawPos_Decelerate(center, ticksToImpact, angle, speed, flip);
                default:
                    return SkyfallerDrawPosUtility.DrawPos_Accelerate(center, ticksToImpact, angle, speed, flip);
            }
        }

        /// <summary>
        /// 升空段的繪製位置與機頭角：DrawPos 再套用舊 ScorerProjectile／InterceptProjectile 的曲線規則
        /// （z 位置曲線與旋轉曲線只在東／西朝向生效）。
        /// </summary>
        public static Vector3 AscentPose(MissileFlightExtension f, Vector3 center, Rot4 rot, int ticksToImpact, float angle,
            int leaveMapAfterTicks, out float rotation)
        {
            Vector3 pos = AscentDrawPos(f, center, ticksToImpact, angle, leaveMapAfterTicks);
            rotation = rot.AsAngle + AscentCurveOffset(f, rot, Progress(ticksToImpact, leaveMapAfterTicks), ref pos);
            return pos;
        }

        /// <summary>升空曲線：修改 <paramref name="pos"/> 的 z，回傳額外旋轉。</summary>
        public static float AscentCurveOffset(MissileFlightExtension f, Rot4 rot, float t, ref Vector3 pos)
        {
            float extra = 0f;
            if (f.zPositionCurve != null && (rot == Rot4.East || rot == Rot4.West))
            {
                pos.z += f.zPositionCurve.Evaluate(t);
            }
            if (f.rotationCurve != null)
            {
                if (rot == Rot4.East) { extra += f.rotationCurve.Evaluate(t); }
                else if (rot == Rot4.West) { extra -= f.rotationCurve.Evaluate(t); }
            }
            return extra;
        }

        /// <summary>這一 tick（時間軸已推進後）是否交接到巡航段。</summary>
        public static bool ShouldHandoff(MissileFlightExtension f, int ticksToImpact, int leaveMapAfterTicks, Vector3 pose, Vector3 center)
        {
            if (Progress(ticksToImpact, leaveMapAfterTicks) >= f.ascentHandoff) { return true; }
            Vector3 d = pose - center;
            d.y = 0f;
            return d.magnitude >= f.handoffMaxCells;
        }

        /// <summary>以交接瞬間的姿態（本 tick 與前一 tick 的位置）建立巡航狀態。</summary>
        public static MissileCruiseState BeginCruise(MissileFlightExtension f, float heading, Vector3 pose, Vector3 previousPose,
            float noseAngle, int ticksToImpact, int leaveMapAfterTicks)
        {
            Vector3 v = pose - previousPose;
            v.y = 0f;
            float speed = v.magnitude;
            return new MissileCruiseState
            {
                active = true,
                ticks = 0,
                pos = pose,
                heading = heading,
                motion0 = speed > 1e-4f ? MissileMapEntryUtility.HeadingOf(v) : noseAngle,
                nose0 = noseAngle,
                nose = noseAngle,
                speed0 = Mathf.Clamp(speed, f.minCruiseSpeed, f.maxCruiseSpeed),
                progress0 = Progress(ticksToImpact, leaveMapAfterTicks),
            };
        }

        /// <summary>巡航段速度（格/tick）：交接速度沿 speedCurve 加速，cruiseSpeedFactor 隨轉向漸入，再夾在上下限內。</summary>
        public static float CruiseSpeed(MissileFlightExtension f, in MissileCruiseState s, int ticksToImpact, int leaveMapAfterTicks, float blend)
        {
            float ratio = 1f;
            if (f.speedCurve != null)
            {
                float start = f.speedCurve.Evaluate(s.progress0);
                float now = f.speedCurve.Evaluate(Progress(ticksToImpact, leaveMapAfterTicks));
                ratio = start > 1e-4f ? now / start : 1f;
            }
            float speed = s.speed0 * ratio * Mathf.Lerp(1f, f.cruiseSpeedFactor, blend);
            return Mathf.Clamp(speed, f.minCruiseSpeed, f.maxCruiseSpeed);
        }

        /// <summary>巡航段前進一 tick（時間軸已推進後呼叫）。</summary>
        public static void CruiseStep(MissileFlightExtension f, ref MissileCruiseState s, int ticksToImpact, int leaveMapAfterTicks)
        {
            s.ticks++;
            float k = f.turnTicks > 0 ? Mathf.Clamp01((float)s.ticks / f.turnTicks) : 1f;
            float blend = k * k * (3f - 2f * k); // smoothstep：轉向起訖都平順
            float motion = Mathf.LerpAngle(s.motion0, s.heading, blend);
            s.nose = Mathf.LerpAngle(s.nose0, s.heading, blend);
            s.pos += MissileMapEntryUtility.HeadingVector(motion) * CruiseSpeed(f, s, ticksToImpact, leaveMapAfterTicks, blend);
        }

        public static bool InMap(Vector3 pos, IntVec3 mapSize)
            => pos.x >= 0f && pos.z >= 0f && pos.x < mapSize.x && pos.z < mapSize.z;

        /// <summary>
        /// 模擬一枚離場彈從發射到飛出地圖的 tick 數（與 <see cref="Projectile_MissileBase"/> 的實際飛行逐 tick 相同；
        /// 時間軸長度取 ticksToDiscardInReverse 的下限，現有 def 皆為固定值）。
        /// 沒有出發方位（<paramref name="heading"/> 為 null）時回傳升空動畫長度。
        /// </summary>
        public static int SimulateDepartureTicks(MissileFlightExtension f, IntVec3 cell, IntVec3 mapSize, Rot4 rot, float? heading)
        {
            int leave = f.ticksToDiscardInReverse != IntRange.Zero ? f.ticksToDiscardInReverse.min : 220;
            if (leave <= 0) { leave = 220; }
            if (heading == null || !f.reversed) { return leave; }

            Vector3 center = cell.ToVector3Shifted();
            float angle = rot.AsAngle;
            MissileCruiseState cruise = default;
            for (int tick = 1, tti = 0; tick <= leave + f.maxCruiseTicks + 1; tick++)
            {
                Vector3 previous = AscentPose(f, center, rot, tti, angle, leave, out _);
                tti++;
                if (cruise.active)
                {
                    CruiseStep(f, ref cruise, tti, leave);
                    if (!InMap(cruise.pos, mapSize) || cruise.ticks >= f.maxCruiseTicks) { return tick; }
                    continue;
                }
                Vector3 pose = AscentPose(f, center, rot, tti, angle, leave, out float nose);
                if (ShouldHandoff(f, tti, leave, pose, center))
                {
                    cruise = BeginCruise(f, heading.Value, pose, previous, nose, tti, leave);
                    if (!InMap(cruise.pos, mapSize)) { return tick; }
                    continue;
                }
                if (tti >= leave) { return tick; }
            }
            return leave;
        }
    }
}
