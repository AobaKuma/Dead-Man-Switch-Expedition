using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>一塊攔截殘骸的運動狀態（A 層 Projectile 與 B 層 fleck 共用）。</summary>
    public struct DebrisState
    {
        /// <summary>地面位置（地圖格座標，y = 0）。</summary>
        public Vector3 ground;
        /// <summary>假想高度（格）。</summary>
        public float height;
        /// <summary>水平速度（格/tick，y = 0）。</summary>
        public Vector3 velocity;
        public float verticalSpeed;
        public float rotation;
        /// <summary>旋轉速度（度/tick）。</summary>
        public float spin;
        /// <summary>相對質量（約 0.2–3），決定阻力、爆散與旋轉。</summary>
        public float mass;
        public int age;

        public bool Landed => height <= 0f;

        /// <summary>畫面位置：高度畫成往畫面上方的偏移（同原版 Projectile 弧線、Skyfaller）。</summary>
        public Vector3 DrawPos(InterceptDebrisPhysics p) => ground + new Vector3(0f, 0f, height * p.heightScale);
    }

    /// <summary>
    /// 攔截殘骸的幾何與運動學（docs/攔截殘骸特效_實作與技術美術計畫.md §3–§4）。純數學，不碰繪製，供 DMSE.BVRTests 驗證。
    /// 方位慣例同 <see cref="MissileMapEntryUtility"/>：0 = 北、順時針，heading 是「從哪裡來」，前進方向 = -HeadingVector(heading)。
    /// 用到 <see cref="Rand"/> 的函式由呼叫端負責 PushState（純視覺，不可擾動遊戲亂數序列）。
    /// </summary>
    public static class InterceptGeometry
    {
        /// <summary>來襲物的平均速度無法估算時的預設值（格/tick）。</summary>
        public const float FallbackSpeed = 0.6f;

        /// <summary>
        /// 來襲物的進入方位：導彈的世界進入方位 → 導彈墜落角 → 空投艙墜落角 → 退回 <paramref name="fallbackFrom"/> 指向落點的方位。
        /// </summary>
        public static float IncomingHeading(Thing payload, Vector3 landing, Vector3 fallbackFrom, out bool hasHeading)
        {
            hasHeading = true;
            if (payload is Projectile_MissileIncoming incoming && incoming.HasEntryHeading) { return incoming.EntryHeading; }
            if (payload is Projectile_MissileBase missile) { return missile.angle; }
            if (payload is Skyfaller skyfaller) { return skyfaller.angle; }
            hasHeading = false;
            return MissileMapEntryUtility.HeadingOf(landing - fallbackFrom);
        }

        /// <summary>來襲物整段墜落的平均速度（格/tick）：起點到落點距離 / 墜落 tick 數。</summary>
        public static float AverageSpeed(Thing payload)
        {
            if (payload is Projectile_MissileBase missile && missile.ticksToImpactMax > 0)
            {
                Vector3 start = MissileArrivalKinematics.FallDrawPos(missile.Flight, Vector3.zero, missile.ticksToImpactMax, missile.ticksToImpactMax, missile.angle);
                start.y = 0f;
                return Sanitize(start.magnitude / missile.ticksToImpactMax);
            }
            if (payload is Skyfaller skyfaller && skyfaller.def != null && skyfaller.def.skyfaller != null)
            {
                SkyfallerProperties f = skyfaller.def.skyfaller;
                // ticksToImpactMax 不公開：用剩餘墜落 tick（BVR 在首 tick 收起，接近全長），沒有時取 def 的平均長度。
                int ticks = skyfaller.ticksToImpact > 0 ? skyfaller.ticksToImpact : Mathf.Max(1, Mathf.RoundToInt(f.ticksToImpactRange.Average));
                Vector3 start;
                switch (f.movementType)
                {
                    case SkyfallerMovementType.ConstantSpeed:
                        start = SkyfallerDrawPosUtility.DrawPos_ConstantSpeed(Vector3.zero, ticks, skyfaller.angle, f.speed, false);
                        break;
                    case SkyfallerMovementType.Decelerate:
                        start = SkyfallerDrawPosUtility.DrawPos_Decelerate(Vector3.zero, ticks, skyfaller.angle, f.speed, false);
                        break;
                    default:
                        start = SkyfallerDrawPosUtility.DrawPos_Accelerate(Vector3.zero, ticks, skyfaller.angle, f.speed, false);
                        break;
                }
                start.y = 0f;
                return Sanitize(start.magnitude / ticks);
            }
            return FallbackSpeed;
        }

        private static float Sanitize(float speed) => float.IsNaN(speed) || speed <= 0.01f ? FallbackSpeed : speed;

        /// <summary>
        /// 末端攔截點（§3）：CIWS 本輪的掃射點 <paramref name="aim"/>，沿航線加一點抖動；高度依迫近進度內插。
        /// </summary>
        public static Vector3 TerminalBurstGround(Vector3 aim, float heading, float approach, InterceptDebrisPhysics p, out float height)
        {
            Vector3 g = aim + MissileMapEntryUtility.HeadingVector(heading) * Rand.Range(-p.terminalJitter, p.terminalJitter);
            g.y = 0f;
            height = Mathf.Lerp(p.terminalHeightFar, p.terminalHeightNear, Mathf.Clamp01(approach));
            return g;
        }

        /// <summary>
        /// 遠距末端攔截的虛擬爆心（2026-10-27）：地面點在入場邊緣外，沿航線外推、垂直航線隨機偏移；高度同末端。
        /// 外推距離隨迫近進度縮短（到 <see cref="InterceptDebrisPhysics.terminalRemoteApproach"/> 時貼邊），且不超過
        /// 代表性主殘骸前進距離的 <see cref="InterceptDebrisPhysics.remoteReach"/>，殘骸才飛得進畫面。
        /// 南側入場時高度偏移會把畫面位置拉回圖內，所以再沿航線外推到畫面位置也看不見（<see cref="DrawHidden"/>）。
        /// </summary>
        public static Vector3 RemoteBurstGround(IntVec3 mapSize, Vector3 landing, float heading, float approach, float targetSpeed,
            InterceptDebrisPhysics p, out float height)
        {
            height = Mathf.Lerp(p.terminalHeightFar, p.terminalHeightNear, Mathf.Clamp01(approach));
            float far = p.terminalRemoteApproach > 0f ? 1f - Mathf.Clamp01(approach / p.terminalRemoteApproach) : 0f;
            float distance = Mathf.Min(p.terminalRemoteDistance.RandomInRange * far, ForwardTravel(targetSpeed, height, p) * p.remoteReach);
            Vector3 outward = MissileMapEntryUtility.HeadingVector(heading);
            Vector3 side = new Vector3(outward.z, 0f, -outward.x);
            landing.y = 0f;
            Vector3 g = MissileMapEntryUtility.EdgePoint(mapSize, landing, heading) + outward * distance
                + side * Rand.Range(-p.remoteSpread, p.remoteSpread);
            g.y = 0f;
            for (int i = 0; i < 64 && !DrawHidden(g, height, mapSize, p); i++) { g += outward; }
            return g;
        }

        /// <summary>
        /// 代表性主殘骸（質量 2、平均繼承速度、不含爆散、無風）從 <paramref name="height"/> 落地前的水平前進距離（格）。不用 Rand。
        /// </summary>
        public static float ForwardTravel(float targetSpeed, float height, InterceptDebrisPhysics p)
        {
            float speed = Mathf.Min(Mathf.Max(0f, targetSpeed), p.maxInheritSpeed);
            DebrisState s = new DebrisState
            {
                height = height,
                velocity = new Vector3(0f, 0f, speed * p.inherit.Average),
                verticalSpeed = -speed * p.descentRatio,
                mass = 2f
            };
            return SimulateLanding(s, p, Vector3.zero, out _).magnitude;
        }

        /// <summary>點離地圖外框的距離（圖內為正、圖外為負）。</summary>
        public static float DistanceInside(IntVec3 mapSize, Vector3 pos)
            => Mathf.Min(Mathf.Min(pos.x, mapSize.x - pos.x), Mathf.Min(pos.z, mapSize.z - pos.z));

        /// <summary>
        /// 畫面位置（地面 + 高度偏移）在圖外或離外框不到 <see cref="InterceptDebrisPhysics.drawEdgeInset"/> 格：
        /// 原版邊緣遮罩蓋掉或太貼邊，這樣的爆心不畫（遠距攔截）。
        /// </summary>
        public static bool DrawHidden(Vector3 ground, float height, IntVec3 mapSize, InterceptDebrisPhysics p)
            => DistanceInside(mapSize, ground + new Vector3(0f, 0f, height * p.heightScale)) < p.drawEdgeInset;

        /// <summary>
        /// 遠距殘骸的不透明度：畫面位置在邊緣帶（<see cref="InterceptDebrisPhysics.drawEdgeInset"/>）內為 0，
        /// 再往內 <see cref="InterceptDebrisPhysics.remoteFadeCells"/> 格漸變到 1。
        /// </summary>
        public static float EdgeFade(IntVec3 mapSize, Vector3 drawPos, InterceptDebrisPhysics p)
            => Mathf.InverseLerp(p.drawEdgeInset, p.drawEdgeInset + p.remoteFadeCells, DistanceInside(mapSize, drawPos));

        /// <summary>
        /// 遠距殘骸會不會飛進畫面：模擬到落地或剔除，途中淡入到 <see cref="InterceptDebrisPhysics.remoteMinFade"/> 以上才算。
        /// 不用 Rand；<paramref name="wind"/> 要與實際飛行相同（FxWind）。
        /// </summary>
        public static bool BecomesVisible(DebrisState s, IntVec3 mapSize, InterceptDebrisPhysics p, Vector3 wind)
        {
            // 與飛行時同順序：落地或剔除的那一 tick 不再繪製，不算看得到。
            for (int i = 0; i <= p.maxLifeTicks + 1; i++)
            {
                if (s.Landed || ShouldCull(s, mapSize, p)) { return false; }
                if (EdgeFade(mapSize, s.DrawPos(p), p) >= p.remoteMinFade) { return true; }
                Step(ref s, p, wind);
            }
            return false;
        }

        /// <summary>
        /// 中過程攔截的虛擬爆心：放在入場邊緣外，確保爆炸不會顯示在地圖內；殘骸從此處飛入並淡入。
        /// </summary>
        public static Vector3 MidcourseRemoteBurstGround(IntVec3 mapSize, Vector3 landing, float heading, InterceptDebrisPhysics p, out float height)
        {
            landing.y = 0f;
            Vector3 edge = MissileMapEntryUtility.EdgePoint(mapSize, landing, heading);
            height = p.midcourseHeight.RandomInRange;
            Vector3 outward = MissileMapEntryUtility.HeadingVector(heading);
            // 北向畫面高度與飛行方向同向；南向則高度把畫面位置推回地圖，因此地面起點需額外外推。
            float distance = p.drawEdgeInset + p.midcourseInset.RandomInRange
                + (outward.z < 0f ? height * p.heightScale : 0f);
            Vector3 g = edge + outward * distance;
            g.y = 0f;
            // 側向入場時落點可能貼近地圖角落；繼續沿入場方向外推，直到畫面位置也確定在地圖外。
            for (int i = 0; i < 64 && !DrawHidden(g, height, mapSize, p); i++) { g += outward; }
            return g;
        }

        /// <summary>
        /// 讓畫面位置（地面 + 高度偏移）留在地圖內、離外框至少 <see cref="InterceptDebrisPhysics.drawEdgeInset"/> 格：
        /// 逐軸夾住畫面位置，再換算回地面點（高度不變）。
        /// </summary>
        public static Vector3 ClampDrawInside(Vector3 ground, float height, IntVec3 mapSize, InterceptDebrisPhysics p)
        {
            float lift = height * p.heightScale;
            float m = Mathf.Min(p.drawEdgeInset, Mathf.Min(mapSize.x, mapSize.z) * 0.5f);
            float x = Mathf.Clamp(ground.x, m, mapSize.x - m);
            float z = Mathf.Clamp(ground.z + lift, m, mapSize.z - m) - lift;
            return new Vector3(x, 0f, z);
        }

        /// <summary>
        /// 一塊殘骸的初始狀態（§4.1）：繼承來襲物的前進與俯衝速度，加上與質量相關的爆散速度。
        /// <paramref name="hulk"/> = 斷裂彈體：保留最大動量、爆散與旋轉都小。
        /// </summary>
        public static DebrisState Launch(Vector3 burstGround, float burstHeight, float heading, float targetSpeed, float mass,
            InterceptDebrisPhysics p, bool hulk = false)
        {
            mass = Mathf.Max(0.05f, mass);
            float speed = Mathf.Min(Mathf.Max(0f, targetSpeed), p.maxInheritSpeed);
            Vector3 forward = -MissileMapEntryUtility.HeadingVector(heading);
            float inherit = hulk ? p.inherit.max : p.inherit.RandomInRange;

            // 單位球上的均勻方向；垂直分量依 upBias 決定朝上或朝下。
            float y = Rand.Range(-1f, 1f);
            float phi = Rand.Range(0f, Mathf.PI * 2f);
            float r = Mathf.Sqrt(1f - y * y);
            y = Rand.Chance(p.upBias) ? Mathf.Abs(y) : -Mathf.Abs(y);
            float burst = p.burstSpeed * Mathf.Pow(mass, -0.35f) * (hulk ? 0.3f : 1f);

            DebrisState s = new DebrisState
            {
                ground = new Vector3(burstGround.x, 0f, burstGround.z),
                height = burstHeight,
                velocity = forward * speed * inherit + new Vector3(Mathf.Cos(phi) * r, 0f, Mathf.Sin(phi) * r) * burst,
                verticalSpeed = -speed * p.descentRatio + y * burst,
                rotation = Rand.Range(0f, 360f),
                spin = Rand.Range(-1f, 1f) * p.spinBase / Mathf.Sqrt(mass) * (hulk ? 0.3f : 1f),
                mass = mass,
                age = 0
            };
            return s;
        }

        /// <summary>每 tick 的積分（§4.2，半隱式 Euler）：阻力、重力、風。<paramref name="wind"/> 為水平風向量（格/tick 量級前的原始值）。</summary>
        public static void Step(ref DebrisState s, InterceptDebrisPhysics p, Vector3 wind)
        {
            float k = Mathf.Clamp(p.dragBase / Mathf.Sqrt(s.mass), 0f, 0.5f);
            s.velocity *= 1f - k;
            s.velocity += new Vector3(wind.x, 0f, wind.z) * (p.windFactor / s.mass);
            s.verticalSpeed = s.verticalSpeed * (1f - k) - p.gravity;
            s.ground += s.velocity;
            s.height += s.verticalSpeed;
            s.rotation += s.spin;
            s.age++;
        }

        /// <summary>地面點離地圖外框的距離（圖內為 0）。</summary>
        public static float DistanceOutside(IntVec3 mapSize, Vector3 ground)
        {
            float dx = Mathf.Max(0f, Mathf.Max(-ground.x, ground.x - mapSize.x));
            float dz = Mathf.Max(0f, Mathf.Max(-ground.z, ground.z - mapSize.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>殘骸是否該提前移除（§4.3）：太久或在圖外太遠。</summary>
        public static bool ShouldCull(in DebrisState s, IntVec3 mapSize, InterceptDebrisPhysics p)
            => s.age > p.maxLifeTicks || DistanceOutside(mapSize, s.ground) > p.offMapCull;

        /// <summary>模擬到落地（測試與預估陰影用）；回傳落地時的地面位置，<paramref name="ticks"/> 為所需 tick（超過上限則為上限）。</summary>
        public static Vector3 SimulateLanding(DebrisState s, InterceptDebrisPhysics p, Vector3 wind, out int ticks)
        {
            ticks = 0;
            while (!s.Landed && ticks < p.maxLifeTicks)
            {
                Step(ref s, p, wind);
                ticks++;
            }
            return s.ground;
        }
    }
}
