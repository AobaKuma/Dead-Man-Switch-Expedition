using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>尾焰與地面光暈用的材質（MoteGlow，render queue 在迷霧之上）。必須在主執行緒建立。</summary>
    [StaticConstructorOnStartup]
    public static class LaunchFxMaterials
    {
        public static readonly Material Core = Glow("Things/Effects/MissileFx/Plume_Core", 6);
        public static readonly Material OuterA = Glow("Things/Effects/MissileFx/Plume_Outer/Plume_Outer_a", 5);
        public static readonly Material OuterB = Glow("Things/Effects/MissileFx/Plume_Outer/Plume_Outer_b", 5);
        public static readonly Material Halo = Glow("Things/Effects/InterceptDebris/Glow_Radial", 4);

        private static Material Glow(string path, int queueOffset)
            => InterceptFxMaterials.AboveFog(MaterialPool.MatFrom(path, ShaderDatabase.MoteGlow), queueOffset);
    }

    /// <summary>
    /// 一枚導彈的發射特效（docs/導彈發射特效_重製計畫.md §3–§4）：由 <see cref="CompEffecterLaunch"/> 每 tick 推進，
    /// 由 <see cref="MapComponent_MissileFx"/> 每幀繪製尾焰。不存檔（讀檔後從當下狀態重新開始）。
    ///
    /// 每 tick：記下噴口姿態（上一 tick／這一 tick，供每幀內插）、推力時間軸、一次性事件（冷射彈射、點火、助推器分離），
    /// 以及沿「上一噴口 → 這一噴口」線段等距發射煙團（§4.3，避免高速時斷成一顆顆）。
    /// 每幀：在內插後的噴口畫三層尾焰（外焰、白熱核心、光暈），點火後短時間在發射點畫地面光暈。
    /// </summary>
    public class LaunchFxEmitter
    {
        private readonly ThingWithComps parent;
        private readonly IMissileFlight flight;
        private readonly CompProperties_EffecterLaunch props;
        private readonly LaunchFxProfileDef profile;
        private readonly int seed;

        private Vector3 prevNozzle, curNozzle;
        private float prevRot, curRot;
        private float prevThrust, curThrust;
        private bool hasPose;
        private Vector3 launchPos;
        private bool ignited, separated, hot;
        private int ticksSinceIgnition = -1;
        /// <summary>熱射點火時決定的地面雲煙舌（之後的持續揚塵沿用同一組，形狀才連貫）。</summary>
        private GroundLobe[] lobes;
        private bool streamActive;
        private Vector3 lastEmitPos;

        // ---- 統計（驗收 V 組用） ----
        public int puffsEmitted, burstPuffs, debrisSpawned;
        /// <summary>同一條煙柱中相鄰兩顆煙團出生位置的最大間距（格）。</summary>
        public float maxStreamGap;
        /// <summary>同一條煙柱中相鄰兩顆的最大「間距 ÷ 煙團大小」：小於約 0.8 表示前後重疊、煙柱連續（§4.3）。</summary>
        public float maxGapToSize;
        public int ignitionTick = -1;

        public LaunchFxEmitter(ThingWithComps parent, IMissileFlight flight, CompProperties_EffecterLaunch props, int ticksSinceSpawn, int ignitionDelay)
        {
            this.parent = parent;
            this.flight = flight;
            this.props = props;
            profile = props.fxProfile;
            seed = parent.thingIDNumber;
            // 讀檔後才建立：已經點過火的不再重播點火與彈射。
            if (ticksSinceSpawn - ignitionDelay > 1) { ignited = true; }
            if (profile.boosterTicks > 0 && ticksSinceSpawn - ignitionDelay > profile.boosterTicks) { separated = true; }
        }

        public LaunchFxProfileDef Profile => profile;
        public float Thrust => curThrust;
        public Vector3 Nozzle => curNozzle;

        private float Scale => props.effectScale > 0f ? props.effectScale : 1f;

        /// <summary>噴口在彈體畫面位置後方的距離（沿用舊 comp 的 offsetY，負值 = 往後）。</summary>
        private float NozzleOffset => -props.offsetY;

        /// <summary>機頭方向（0 = 北、順時針；貼圖機頭朝北）。</summary>
        private static Vector3 Forward(float rotation) => MissileMapEntryUtility.HeadingVector(rotation);

        // ====================================================================
        //  每 tick
        // ====================================================================

        public void Tick(int ticksSinceSpawn, int ignitionDelay, float evaluate, bool hotLaunch)
        {
            Map map = parent.Map;
            if (map == null) { return; }
            MapComponent_MissileFx fx = MapComponent_MissileFx.For(map);
            if (fx == null) { return; }
            hot = hotLaunch;

            float rot = flight.LaunchRotation;
            Vector3 nozzle = flight.LaunchDrawPos - Forward(rot) * NozzleOffset;
            nozzle.y = 0f;
            if (!hasPose)
            {
                prevNozzle = curNozzle = nozzle;
                prevRot = curRot = rot;
                launchPos = parent.TrueCenter();
                launchPos.y = 0f;
                hasPose = true;
            }
            else
            {
                prevNozzle = curNozzle;
                prevRot = curRot;
                curNozzle = nozzle;
                curRot = rot;
            }
            prevThrust = curThrust;
            int t = ticksSinceSpawn - ignitionDelay;
            curThrust = ThrustAt(profile, props.burnDurationTicks, t, evaluate);
            if (ticksSinceIgnition >= 0) { ticksSinceIgnition++; }

            bool visible = map == Find.CurrentMap;
            FxCanvas canvas = fx.Canvas;
            Rand.PushState(Gen.HashCombineInt(seed, ticksSinceSpawn));
            try
            {
                if (ticksSinceSpawn == 1 && ignitionDelay > 0 && visible) { EjectionPuff(canvas); }
                if (!ignited && t >= 1)
                {
                    ignited = true;
                    ticksSinceIgnition = 0;
                    ignitionTick = Find.TickManager.TicksGame;
                    if (visible) { Ignite(map, canvas); }
                }
                if (!separated && profile.boosterTicks > 0 && t >= profile.boosterTicks)
                {
                    separated = true;
                    Separate(map, canvas, visible);
                }
                if (visible && hot && lobes != null && ticksSinceIgnition > 0 && ticksSinceIgnition <= GroundFeedTicks
                    && ticksSinceIgnition % GroundFeedInterval == 0 && curThrust > 0.3f
                    && LaunchFxBudget.Pressure(canvas.Smoke.Count) < 2 && Rand.Chance(LaunchFxBudget.Density == 0 ? 0.5f : 1f))
                {
                    // 持續揚塵：越晚越慢、越靠近發射點（彈體升高，噴流打到地面的力道變小）。
                    float late = (float)ticksSinceIgnition / GroundFeedTicks;
                    GroundCloudPuff(canvas, GroundCloudPlacement(lobes), 1f - 0.6f * late);
                }
                if (visible) { EmitSmoke(map, canvas); }
                else { streamActive = false; }
            }
            finally { Rand.PopState(); }
        }

        /// <summary>推力時間軸（§4.1）：點火後 rampTicks 內升到 1；助推器燒完 10 tick 內降到 sustainThrust；舊 comp 的 burnDurationTicks 之後 8 tick 熄火。</summary>
        public static float ThrustAt(LaunchFxProfileDef profile, int burnDurationTicks, int t, float evaluate)
        {
            if (t < 1) { return 0f; }
            float thrust = Mathf.Clamp01(t / (float)Mathf.Max(1, profile.rampTicks));
            if (profile.boosterTicks > 0 && t > profile.boosterTicks)
            {
                thrust = Mathf.Min(thrust, Mathf.Lerp(1f, profile.sustainThrust, Mathf.Clamp01((t - profile.boosterTicks) / 10f)));
            }
            if (burnDurationTicks == 0) { return 0f; }
            if (burnDurationTicks > 0 && t > burnDurationTicks)
            {
                thrust *= 1f - Mathf.Clamp01((t - burnDurationTicks) / 8f);
            }
            if (profile.thrustCurve != null) { thrust *= Mathf.Max(0f, profile.thrustCurve.Evaluate(evaluate)); }
            return thrust;
        }

        // ---- 煙柱（§4.2–4.3） ----

        private void EmitSmoke(Map map, FxCanvas canvas)
        {
            // 噴口畫面位置出了地圖（邊緣外會被原版遮罩蓋住）或推力太小：這條煙柱中斷。
            if (curThrust < 0.03f || InterceptGeometry.DistanceOutside(map.Size, curNozzle) > 2f)
            {
                streamActive = false;
                return;
            }
            int pressure = LaunchFxBudget.Pressure(canvas.Smoke.Count);
            float size0 = profile.smokeSize * (0.6f + 0.4f * curThrust) * Scale;
            float spacing = Mathf.Max(0.15f, profile.spacingFactor * size0) * (pressure >= 1 ? 2f : 1f);
            Vector3 from = streamActive ? lastEmitPos : prevNozzle;
            Vector3 seg = curNozzle - from;
            float dist = seg.magnitude;
            int maxPerTick = pressure == 2 ? 1 : LaunchFxBudget.MaxPuffsPerTick;
            // 剛點火的 30 tick 彈體在畫面上幾乎不動：每 tick 至少一顆，讓升空瞬間的煙夠濃。
            int minCount = !streamActive || ticksSinceIgnition < 30 ? 1 : 0;
            int count = Mathf.Clamp(Mathf.FloorToInt(dist / spacing), minCount, maxPerTick);
            if (count == 0) { return; }
            // 數量被上限截斷時，平均分佈在整段上（間距變大，但煙柱不會斷成兩截）。
            float step = dist / count;
            float life = profile.trailLifeTicks * LaunchFxBudget.LifeFactor * (pressure >= 1 ? 0.5f : 1f);
            Vector3 f = Forward(curRot);
            Vector3 side = new Vector3(f.z, 0f, -f.x);
            Vector3 missileVel = curNozzle - prevNozzle;
            for (int i = 1; i <= count; i++)
            {
                Vector3 p = dist > 0f ? from + seg * (i * step / dist) : curNozzle;
                if (streamActive || i > 1)
                {
                    float gap = (p - lastEmitPos).magnitude;
                    maxStreamGap = Mathf.Max(maxStreamGap, gap);
                    maxGapToSize = Mathf.Max(maxGapToSize, gap / size0);
                }
                var spec = new SmokePuffSpec
                {
                    def = DMSE_DefOf.DMSE_LaunchSmokeLight,
                    pos = p + side * Rand.Range(-0.08f, 0.08f) * size0,
                    velocity = (-f * profile.exhaustSpeed * Rand.Range(0.6f, 1f) * curThrust
                                + side * Rand.Range(-0.15f, 0.15f) * profile.exhaustSpeed
                                + missileVel * profile.inherit) * Scale,
                    size = size0 * Rand.Range(0.8f, 1.2f),
                    growth = profile.smokeGrowth,
                    lifeTicks = Mathf.Max(30, Mathf.RoundToInt(life * Rand.Range(0.85f, 1.15f))),
                    drag = 0.08f,
                    buoyancy = profile.buoyancy,
                    windFactor = 0.002f,
                    hotColor = profile.smokeHotColor,
                    color = ShadeTint(profile.smokeColor, profile.smokeShade, Rand.Range(-1f, 1f)),
                    hotTicks = 9,
                    rotation = Rand.Range(0f, 360f),
                    spin = Rand.Range(-1.5f, 1.5f)
                };
                canvas.Smoke.Spawn(spec);
                lastEmitPos = p;
                puffsEmitted++;
            }
            streamActive = true;
        }

        /// <summary>每顆煙團深淺的隨機幅度（tint 亮度 ±）。</summary>
        public const float ShadeJitter = 0.07f;

        /// <summary>
        /// 煙的深淺（純函式，單元測試用）：淺灰貼圖乘上灰色 tint。亮度取舊版「淺／中／深三張貼圖按 (1−s)²、2s(1−s)、s² 混合」的平均
        /// （貼圖灰階 0.95／0.6／0.32，相對淺灰貼圖 = 1、0.63、0.34），所以 profile 的 smokeShade 看起來和以前一樣深，
        /// 但不再有一顆顆深淺跳動。<paramref name="jitter"/> ∈ [−1, 1]。只動 rgb，透明度不變。
        /// 改成單一貼圖的原因見 docs/導彈發射特效_重製計畫.md §0.5（多材質在同一高度會互相亂序）。
        /// </summary>
        public static Color ShadeTint(Color color, float shade, float jitter)
        {
            float s = Mathf.Clamp01(shade);
            float k = (1f - s) * (1f - s) + 2f * s * (1f - s) * (0.6f / 0.95f) + s * s * (0.32f / 0.95f);
            k = Mathf.Clamp01(k + ShadeJitter * Mathf.Clamp(jitter, -1f, 1f));
            return new Color(color.r * k, color.g * k, color.b * k, color.a);
        }

        // ---- 一次性爆發（§4.4） ----

        /// <summary>冷射彈射：發射點一團白色燃氣（沒有火焰）。</summary>
        private void EjectionPuff(FxCanvas canvas)
        {
            int n = LaunchFxBudget.BurstCount(profile.ejectionPuffs, canvas.Smoke.Count);
            Burst(canvas, launchPos, n, 0.18f, new FloatRange(1.5f, 2.5f), 1.6f, 240, DMSE_DefOf.DMSE_LaunchSmokeLight,
                new Color(1f, 1f, 1f, 0.5f), new Color(1f, 1f, 1f, 0.5f), Vector3.zero, 0f);
        }

        private void Ignite(Map map, FxCanvas canvas)
        {
            Vector3 back = -Forward(curRot);
            canvas.Emit(DMSE_DefOf.DMSE_InterceptFlash, curNozzle, profile.ignitionFlash * Scale * (hot ? 1f : 0.6f), 0f, new Color(1f, 0.95f, 0.8f, 1f));
            if (hot)
            {
                // 熱射：地面雲沿地面衝出幾道不等長的煙舌，加上發射點一團濃核心（§0.6）；之後 GroundFeedTicks 內持續補揚塵。
                int n = LaunchFxBudget.BurstCount(profile.groundCloudCount, canvas.Smoke.Count);
                lobes = PlanLobes(MissileMapEntryUtility.HeadingOf(back));
                for (int i = 0; i < n; i++) { GroundCloudPuff(canvas, GroundCloudPlacement(lobes), 1f); }
            }
            else
            {
                // 冷射：空中點火，噴口一圈小煙環。
                int n = LaunchFxBudget.BurstCount(profile.airIgnitionRing, canvas.Smoke.Count);
                Burst(canvas, curNozzle, n, 0.12f * Scale, new FloatRange(0.8f, 1.4f) * Scale, 1.8f, 200, DMSE_DefOf.DMSE_LaunchSmokeLight,
                    profile.smokeHotColor, profile.smokeColor, Vector3.zero, 0f);
            }
            // 夜間：整圖微亮一下（同攔截空爆，強度很小）。
            if (map.skyManager.CurSkyGlow < 0.35f)
            {
                map.weatherManager.eventHandler.AddEvent(new WeatherEvent_MissileFlash(map, 0.2f, new Vector2(back.x, back.z) * 3f));
            }
        }

        /// <summary>助推器燒完：噴口一團亮白煙，掉下幾片殘骸（攔截特效的主殘骸；落地照常留碎屑，傷害依設定）。</summary>
        private void Separate(Map map, FxCanvas canvas, bool visible)
        {
            if (visible)
            {
                int n = LaunchFxBudget.BurstCount(6, canvas.Smoke.Count);
                Burst(canvas, curNozzle, n, 0.1f * Scale, new FloatRange(0.9f, 1.5f) * Scale, 1.5f, 240, DMSE_DefOf.DMSE_LaunchSmokeLight,
                    new Color(1f, 1f, 1f, 0.55f), new Color(1f, 1f, 1f, 0.5f), Vector3.zero, 0f);
            }
            Vector3 vel = curNozzle - prevNozzle;
            for (int i = 0; i < profile.boosterDebris; i++)
            {
                const float height = 6f;
                var s = new DebrisState
                {
                    ground = curNozzle - new Vector3(0f, 0f, height),
                    height = height,
                    velocity = new Vector3(vel.x, 0f, vel.z) * 0.35f + new Vector3(Rand.Range(-0.05f, 0.05f), 0f, Rand.Range(-0.05f, 0.05f)),
                    verticalSpeed = 0.02f,
                    rotation = Rand.Range(0f, 360f),
                    spin = Rand.Range(-8f, 8f),
                    mass = Rand.Range(1.2f, 2f)
                };
                if (!s.ground.ToIntVec3().InBounds(map)) { continue; }
                InterceptDebrisChunk.SpawnChunk(map, s, 0.8f, false, null, new Color(0.8f, 0.8f, 0.82f, 1f), "Things/Effects/InterceptDebris/Pod");
                debrisSpawned++;
            }
        }

        // ---- 地面雲（§4.4、§0.6） ----

        /// <summary>點火後持續補揚塵的時間與間隔（tick）。</summary>
        public const int GroundFeedTicks = 36, GroundFeedInterval = 3;
        /// <summary>落在發射點核心（慢、大、不往外衝）的比例。</summary>
        public const float GroundCoreShare = 0.3f;

        /// <summary>一道地面煙舌：方向、長度（速度倍率）、份量（被選中的權重）。</summary>
        public struct GroundLobe
        {
            public float heading, reach, weight;
        }

        /// <summary>一顆地面雲煙團的方向、速度倍率、大小倍率。</summary>
        public struct GroundPlacement
        {
            public float heading, speedFactor, sizeFactor;
        }

        /// <summary>
        /// 地面雲的煙舌（純函式，用 Rand；單元測試用）：3–5 道，方向隨機但約一半集中在尾焰後方 ±70°（噴流被地面反射往後衝），
        /// 長度 0.5–1.4 倍、份量 0.3–1 倍互不相同，所以不會是一圈均勻的環。
        /// </summary>
        public static GroundLobe[] PlanLobes(float backHeading)
        {
            var result = new GroundLobe[Rand.RangeInclusive(3, 5)];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new GroundLobe
                {
                    heading = i % 2 == 0 ? backHeading + Rand.Range(-70f, 70f) : Rand.Range(0f, 360f),
                    reach = Rand.Range(0.5f, 1.4f),
                    weight = Rand.Range(0.3f, 1f)
                };
            }
            return result;
        }

        /// <summary>
        /// 一顆地面雲煙團落在哪（純函式，用 Rand）：<see cref="GroundCoreShare"/> 的機率在發射點核心（慢、大）；
        /// 否則依份量選一道煙舌，方向 ±18°，速度沿煙舌長度隨機（近的較大、遠的較小），讓煙舌前端稀、根部濃。
        /// </summary>
        public static GroundPlacement GroundCloudPlacement(GroundLobe[] lobes)
        {
            if (lobes == null || lobes.Length == 0 || Rand.Chance(GroundCoreShare))
            {
                return new GroundPlacement { heading = Rand.Range(0f, 360f), speedFactor = Rand.Range(0f, 0.3f), sizeFactor = Rand.Range(1.1f, 1.5f) };
            }
            float total = 0f;
            foreach (GroundLobe l in lobes) { total += l.weight; }
            float r = Rand.Value * total;
            GroundLobe lobe = lobes[lobes.Length - 1];
            foreach (GroundLobe l in lobes)
            {
                if (r < l.weight) { lobe = l; break; }
                r -= l.weight;
            }
            float along = Mathf.Sqrt(Rand.Value);
            return new GroundPlacement
            {
                heading = lobe.heading + Rand.Range(-18f, 18f),
                speedFactor = lobe.reach * along * Rand.Range(0.8f, 1.25f),
                sizeFactor = Mathf.Lerp(1.15f, 0.7f, along) * Rand.Range(0.85f, 1.15f)
            };
        }

        private void GroundCloudPuff(FxCanvas canvas, GroundPlacement g, float strength)
        {
            Vector3 dir = MissileMapEntryUtility.HeadingVector(g.heading);
            canvas.Smoke.Spawn(new SmokePuffSpec
            {
                def = DMSE_DefOf.DMSE_LaunchGroundCloud,
                pos = launchPos + dir * Rand.Range(0f, 0.6f) * Scale,
                velocity = dir * profile.groundCloudSpeed * Scale * g.speedFactor * strength,
                size = profile.groundCloudSize.RandomInRange * Scale * g.sizeFactor,
                growth = 1.2f,
                lifeTicks = Mathf.Max(30, Mathf.RoundToInt(profile.groundCloudLifeTicks * LaunchFxBudget.LifeFactor * Rand.Range(0.8f, 1.2f))),
                drag = 0.06f,
                buoyancy = 0.001f,
                windFactor = 0.002f,
                hotColor = profile.smokeHotColor,
                color = ShadeTint(profile.groundCloudColor, 0.15f, Rand.Range(-1f, 1f)),
                hotTicks = 12,
                // 貼圖略扁（85 %）：可以斜一點，但不亂轉（像葉子，V3／V4）。
                rotation = Rand.Range(-35f, 35f),
                spin = Rand.Range(-0.15f, 0.15f)
            });
            burstPuffs++;
        }

        /// <summary>從 <paramref name="center"/> 放射出 n 顆煙團；<paramref name="bias"/> 方向以 <paramref name="biasShare"/> 的比例集中在 ±60°。</summary>
        private void Burst(FxCanvas canvas, Vector3 center, int n, float speed, FloatRange size, float growth, int life, FleckDef def,
            Color hotColor, Color color, Vector3 bias, float biasShare)
        {
            float biasHeading = bias.sqrMagnitude > 0f ? MissileMapEntryUtility.HeadingOf(bias) : 0f;
            for (int i = 0; i < n; i++)
            {
                float heading = bias.sqrMagnitude > 0f && Rand.Chance(biasShare) ? biasHeading + Rand.Range(-60f, 60f) : Rand.Range(0f, 360f);
                Vector3 dir = MissileMapEntryUtility.HeadingVector(heading);
                canvas.Smoke.Spawn(new SmokePuffSpec
                {
                    def = def,
                    pos = center + dir * Rand.Range(0f, 0.6f),
                    velocity = dir * speed * Rand.Range(0.5f, 1.3f),
                    size = size.RandomInRange,
                    growth = growth,
                    lifeTicks = Mathf.Max(30, Mathf.RoundToInt(life * LaunchFxBudget.LifeFactor * Rand.Range(0.8f, 1.2f))),
                    drag = 0.06f,
                    buoyancy = 0.001f,
                    windFactor = 0.002f,
                    hotColor = hotColor,
                    color = color,
                    hotTicks = 12,
                    rotation = Rand.Range(0f, 360f),
                    spin = Rand.Range(-1f, 1f)
                });
                burstPuffs++;
            }
        }

        // ====================================================================
        //  每幀
        // ====================================================================

        /// <summary>
        /// 畫尾焰與地面光暈（§4.1、§4.5）。<paramref name="alpha"/> = <see cref="TickInterpolation.Alpha"/>：在上一 tick 與這一 tick 的姿態間內插。
        /// </summary>
        public void Draw(DrawBatch batch, float alpha)
        {
            if (!hasPose) { return; }
            float thrust = Mathf.Lerp(prevThrust, curThrust, alpha);
            float scale = Scale;

            if (ticksSinceIgnition >= 0 && ticksSinceIgnition < profile.groundGlowTicks && profile.groundGlow > 0f)
            {
                float k = (1f - (ticksSinceIgnition + alpha) / profile.groundGlowTicks) * thrust * (hot ? 1f : 0.4f);
                if (k > 0.01f)
                {
                    Color c = profile.flameColor;
                    c.a = 0.55f * k;
                    Quad(batch, LaunchFxMaterials.Halo, launchPos, 0f, profile.groundGlow * scale, profile.groundGlow * scale, 0.5f, c);
                }
            }

            if (thrust < 0.02f) { return; }
            Vector3 pos = Vector3.Lerp(prevNozzle, curNozzle, alpha);
            float rot = Mathf.LerpAngle(prevRot, curRot, alpha);
            Vector3 f = Forward(rot);
            float time = Time.realtimeSinceStartup;
            float flicker = 1f + 0.1f * (0.6f * Mathf.Sin(time * 37f + seed) + 0.4f * Mathf.Sin(time * 23.7f + seed * 1.3f));
            float speed = (curNozzle - prevNozzle).magnitude;
            float length = profile.coreLength * thrust * flicker * (1f + 0.3f * Mathf.Min(speed / 3f, 1f)) * scale;
            float width = profile.plumeWidth * (0.6f + 0.4f * thrust) * scale;

            Material outer = ((Time.frameCount / 3 + seed) & 1) == 0 ? LaunchFxMaterials.OuterA : LaunchFxMaterials.OuterB;
            Quad(batch, outer, pos - f * (0.9f * length), rot, width, 1.8f * length, 2.2f, profile.flameColor);
            Quad(batch, LaunchFxMaterials.Core, pos - f * (0.5f * length), rot, 0.35f * width, length, 2.4f, profile.coreColor);
            Color halo = profile.flameColor;
            halo.a = 0.6f;
            float d = 2.5f * width * thrust;
            Quad(batch, LaunchFxMaterials.Halo, pos, 0f, d, d, 2.3f, halo);
        }

        private static void Quad(DrawBatch batch, Material mat, Vector3 center, float rotation, float width, float length, float altitudeOffset, Color color)
        {
            center.y = AltitudeLayer.MetaOverlays.AltitudeFor(altitudeOffset);
            Matrix4x4 m = Matrix4x4.TRS(center, Quaternion.AngleAxis(rotation, Vector3.up), new Vector3(width, 1f, length));
            batch.DrawMesh(MeshPool.plane10, m, mat, 0, color);
        }
    }
}
