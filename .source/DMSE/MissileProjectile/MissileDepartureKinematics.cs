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
        /// <summary>航向與機頭各自的轉向慣性狀態，供逐 tick 修正及存檔續接。</summary>
        public float motionTurnRate;
        public float noseTurnRate;
        /// <summary>目前機頭角（繪製與尾焰用）。</summary>
        public float nose;
        /// <summary>目前航向（轉向中逐 tick 逼近 <see cref="heading"/>）。</summary>
        public float motion;

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
            Scribe_Values.Look(ref motionTurnRate, "cruiseMotionTurnRate", 0f);
            Scribe_Values.Look(ref noseTurnRate, "cruiseNoseTurnRate", 0f);
            Scribe_Values.Look(ref nose, "cruiseNose", 0f);
            Scribe_Values.Look(ref motion, "cruiseMotion", float.NaN);
            // 舊存檔（固定 tick 轉向時）沒有航向：從機頭接續，轉向照新規則完成。
            if (Scribe.mode == LoadSaveMode.PostLoadInit && float.IsNaN(motion)) { motion = nose; }
        }
    }

    /// <summary>
    /// 導彈離場的運動學（docs/Missile_Skyfaller轉Projectile_計畫.md §8.3）。純函式：
    /// <see cref="Projectile_MissileBase"/> 的實際飛行與 <see cref="SimulateDepartureTicks"/>（波次打擊時間軸估算）共用同一套，
    /// 所以預估的離場 tick 與實際相同。
    ///
    /// 升空段：逐 tick 重算原版 Skyfaller 公式（<see cref="AscentPose"/>），與舊 ScorerProjectile 相同。
    /// 交接：動畫進度達 <see cref="MissileFlightExtension.ascentHandoff"/> 或畫面位移達 handoffMaxCells 時。
    /// 巡航段：從交接位置與速度出發，航向與機頭以欠阻尼角速度控制過轉並回正（<see cref="TurnCorrectionStep"/>）；
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
            rotation = angle + AscentCurveOffset(f, rot, Progress(ticksToImpact, leaveMapAfterTicks), ref pos);
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
                motion = speed > 1e-4f ? MissileMapEntryUtility.HeadingOf(v) : noseAngle,
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

        /// <summary>無狀態的一步轉向入口，保留供純數學呼叫端使用；飛行中應保存角速度並呼叫 TurnCorrectionStep。</summary>
        public static float TurnStep(MissileFlightExtension f, float current, float target, int tick, float speed = 0f, float radius = -1f, int preferSign = 0)
        {
            float turnRate = 0f;
            return TurnCorrectionStep(f, current, target, tick, speed, radius, preferSign, ref turnRate);
        }

        /// <summary>
        /// 欠阻尼轉向一步：角度偏差產生角加速度、角速度形成阻尼，讓彈體帶慣性越過目標後反向回修；
        /// 阻尼比控制過轉後往復衰減的次數。輸出受漸增轉速、轉彎半徑與最大角加速度限制。
        /// </summary>
        public static float TurnCorrectionStep(MissileFlightExtension f, float current, float target, int tick, float speed, float radius,
            int preferSign, ref float turnRate)
        {
            float error = Mathf.DeltaAngle(current, target);
            if (Mathf.Abs(error) > UTurnAngle && preferSign != 0) { error = preferSign * Mathf.Abs(error); }
            if (radius < 0f) { radius = f.maxTurnRadius; }

            // 一般轉速依 turnRampTicks 漸增；半徑限制用較短漸增，避免高速時轉彎過大。
            float rateLimit = Mathf.Max(
                f.maxTurnRate * Mathf.Clamp01((float)tick / Mathf.Max(1, f.turnRampTicks)),
                radius > 0f ? speed / radius * Mathf.Rad2Deg * Mathf.Clamp01((float)tick / RadiusRampTicks) : 0f);
            float naturalFrequency = Mathf.Max(0f, f.turnNaturalFrequency);
            float dampingRatio = Mathf.Clamp(f.turnDampingRatio, 0f, 1f);
            float accelerationLimit = Mathf.Max(0f, f.turnMaxAcceleration);
            float angularAcceleration = naturalFrequency * naturalFrequency * error
                - 2f * dampingRatio * naturalFrequency * turnRate;
            angularAcceleration = Mathf.Clamp(angularAcceleration, -accelerationLimit, accelerationLimit);
            turnRate = Mathf.Clamp(turnRate + angularAcceleration, -rateLimit, rateLimit);
            return current + turnRate;
        }

        /// <summary>半徑限制轉速的漸增 tick 數（比 turnRampTicks 短）。</summary>
        public const float RadiusRampTicks = 4f;

        /// <summary>超過這個角度的轉向算迴轉：往地圖內側轉（<see cref="CruiseStep"/>），不取短邊。</summary>
        public const float UTurnAngle = 150f;

        /// <summary>
        /// 轉彎半徑依「沿目前航向到地圖邊緣的距離」縮小（純函式）：最多一半，讓轉彎在出圖前完成，不從出發方位以外的邊離場
        /// （驗收 F5：靠近地圖角落發射時，晚轉、緩轉曾從側邊出圖）。下限 4 格。
        /// </summary>
        public static float TurnRadiusAt(MissileFlightExtension f, Vector3 pos, float motion, IntVec3 mapSize)
        {
            if (mapSize.x <= 0) { return f.maxTurnRadius; }
            float ahead = (MissileMapEntryUtility.EdgePoint(mapSize, pos, motion) - pos).WithY(0f).magnitude;
            return Mathf.Clamp(ahead * 0.5f, 4f, f.maxTurnRadius);
        }

        /// <summary>迴轉時往地圖中心那一側轉：+1 = 順時針（羅盤角增加）、−1 = 逆時針。</summary>
        public static int InwardSide(Vector3 pos, float motion, IntVec3 mapSize)
        {
            if (mapSize.x <= 0) { return 0; }
            Vector3 toCenter = new Vector3(mapSize.x * 0.5f - pos.x, 0f, mapSize.z * 0.5f - pos.z);
            if (toCenter.sqrMagnitude < 1f) { return 0; }
            float d = Mathf.DeltaAngle(motion, MissileMapEntryUtility.HeadingOf(toCenter));
            return Mathf.Abs(d) < 1f ? 0 : (d > 0f ? 1 : -1);
        }

        /// <summary>轉向完成比例（0 = 交接時、1 = 航向已對準出發方位），給 cruiseSpeedFactor 漸入用。</summary>
        public static float TurnBlend(in MissileCruiseState s)
        {
            float total = Mathf.Abs(Mathf.DeltaAngle(s.motion0, s.heading));
            return total < 1e-3f ? 1f : Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(s.motion, s.heading)) / total);
        }

        /// <summary>巡航段前進一 tick（時間軸已推進後呼叫）。</summary>
        public static void CruiseStep(MissileFlightExtension f, ref MissileCruiseState s, int ticksToImpact, int leaveMapAfterTicks, IntVec3 mapSize = default)
        {
            s.ticks++;
            float speed = CruiseSpeed(f, s, ticksToImpact, leaveMapAfterTicks, TurnBlend(s));
            float radius = TurnRadiusAt(f, s.pos, s.motion, mapSize);
            int side = InwardSide(s.pos, s.motion, mapSize);
            s.motion = TurnCorrectionStep(f, s.motion, s.heading, s.ticks, speed, radius, side, ref s.motionTurnRate);
            s.nose = TurnCorrectionStep(f, s.nose, s.heading, s.ticks, speed, radius, side, ref s.noseTurnRate);
            s.pos += MissileMapEntryUtility.HeadingVector(s.motion) * speed;
        }

        public static bool InMap(Vector3 pos, IntVec3 mapSize)
            => pos.x >= 0f && pos.z >= 0f && pos.x < mapSize.x && pos.z < mapSize.z;

        /// <summary>
        /// 模擬一枚離場彈從發射到飛出地圖的 tick 數（與 <see cref="Projectile_MissileBase"/> 的實際飛行逐 tick 相同；
        /// 時間軸長度取 ticksToDiscardInReverse 的下限，現有 def 皆為固定值）。
        /// 沒有出發方位（<paramref name="heading"/> 為 null）時回傳升空動畫長度。
        /// </summary>
        public static int SimulateDepartureTicks(MissileFlightExtension f, IntVec3 cell, IntVec3 mapSize, Rot4 rot, float? heading, float? launchAngle = null)
        {
            int leave = f.ticksToDiscardInReverse != IntRange.Zero ? f.ticksToDiscardInReverse.min : 220;
            if (leave <= 0) { leave = 220; }
            if (heading == null || !f.reversed) { return leave; }

            Vector3 center = cell.ToVector3Shifted();
            float angle = launchAngle ?? rot.AsAngle;
            MissileCruiseState cruise = default;
            for (int tick = 1, tti = 0; tick <= leave + f.maxCruiseTicks + 1; tick++)
            {
                Vector3 previous = AscentPose(f, center, rot, tti, angle, leave, out _);
                tti++;
                if (cruise.active)
                {
                    CruiseStep(f, ref cruise, tti, leave, mapSize);
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
