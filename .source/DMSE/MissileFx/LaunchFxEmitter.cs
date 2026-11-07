using System.Collections.Generic;
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
        /// <summary>轉向推進器（拋棄後）：Transparent 版才吃得到逐幀透明度淡出（§3.6）。</summary>
        public static readonly Material TurnPod = InterceptFxMaterials.AboveFog(
            MaterialPool.MatFrom("Things/Effects/MissileFx/TurnPod", ShaderDatabase.Transparent), 2, true);

        private static readonly Dictionary<string, Material> podMaterials = new Dictionary<string, Material>();

        /// <summary>profile 指定的轉向推進器貼圖（空 = 預設 TurnPod）。用導彈自己的鼻錐貼圖才看得出「就是它的頭」。</summary>
        public static Material TurnPodFor(string texPath)
        {
            if (texPath.NullOrEmpty()) { return TurnPod; }
            Material m;
            if (!podMaterials.TryGetValue(texPath, out m))
            {
                m = InterceptFxMaterials.AboveFog(
                    MaterialPool.MatFrom(texPath, ShaderDatabase.Transparent), 2, true);
                podMaterials[texPath] = m;
            }
            return m;
        }

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

        // ---- 轉向：RCS 側推／反推、轉向推進器拋棄、主推力全開（§3.6） ----
        /// <summary>這一 tick 的機頭角速度（度/tick，正 = 順時針）與角加速度。</summary>
        private float rotRate, lastRotRate, rotAccel;
        /// <summary>轉向段（側推工作中）；角速度過門檻或到 <see cref="LaunchFxProfileDef.turnStartTick"/> 時開始。</summary>
        private bool turnActive, hasTurned, jettisoned;
        private int turnStartT = -1, brakeAt = -1;
        /// <summary>轉向推進器拋棄時的 tick（自點火起）；−1 = 還沒拋。</summary>
        private int jettisonT = -1;
        /// <summary>RCS 這一 tick 的顯示強度（0 = 不噴）與力矩方向（+1 = 順時針）。</summary>
        private float turnJetStrength;
        private int lastPulseIndex = -1;
        private int pulsesFired;
        private bool pulseJustStarted;
        private int turnCouple;
        /// <summary>主要轉向方向（+1 = 順時針；側推時記下，反推不改）。</summary>
        private int turnDir;
        /// <summary>主推力全開的漸入比例（0 = 助推／續航段、1 = 全開）。</summary>
        private float mainBlend;
        /// <summary>拋棄後的轉向推進器：年齡（−1 = 不在畫面上）、位置、速度、角度、自轉。</summary>
        private int podAge = -1;
        private Vector3 podPos, podVel;
        private float podRot, podSpin;
        /// <summary>目前點放第幾 tick（0 = 剛點火，噴嘴閃一下；−1 = 不在點放中）。</summary>
        private int jetPhase = -1;
        /// <summary>主推力點火光球的年齡（−1 = 不畫）。</summary>
        private int bloomAge = -1;

        // ---- 統計（驗收 V 組用） ----
        public int puffsEmitted, burstPuffs, debrisSpawned;
        /// <summary>同一條煙柱中相鄰兩顆煙團出生位置的最大間距（格）。</summary>
        public float maxStreamGap;
        /// <summary>同一條煙柱中相鄰兩顆的最大「間距 ÷ 煙團大小」：小於約 0.8 表示前後重疊、煙柱連續（§4.3）。</summary>
        public float maxGapToSize;
        public int ignitionTick = -1;

        // ---- 轉向段狀態（驗收 V9 讀取） ----
        /// <summary>轉向段進行中（側推／反推工作）。</summary>
        public bool TurnActive => turnActive;
        /// <summary>轉向推進器已拋棄（主推力開始全開）。</summary>
        public bool Jettisoned => jettisoned;
        /// <summary>這一 tick 的機頭角速度（度/tick，正 = 順時針）。</summary>
        public float TurnRate => rotRate;
        /// <summary>這一 tick 的 RCS 火焰強度（0 = 不噴）。</summary>
        public float TurnJetAim => turnJetStrength;
        /// <summary>主推力全開的漸入比例。</summary>
        public float MainThrustBlend => mainBlend;
        /// <summary>拋棄後的轉向推進器年齡（−1 = 不在畫面上）。</summary>
        public int PodAge => podAge;
        public Vector3 PodPos => podPos;

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
        private float NozzleOffset => -(separated && !props.separatedTexPath.NullOrEmpty() ? props.separatedOffsetY : props.offsetY);

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
            rotRate = Mathf.DeltaAngle(prevRot, curRot);
            rotAccel = rotRate - lastRotRate;
            lastRotRate = rotRate;
            mainBlend = jettisonT >= 0
                ? Mathf.Clamp01((t - jettisonT) / (float)Mathf.Max(1, profile.mainThrustRampTicks))
                : 0f;
            curThrust = ThrustAt(profile, props.burnDurationTicks, t, evaluate, mainBlend);
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
                UpdateTurn(canvas, visible, t);
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
            => ThrustAt(profile, burnDurationTicks, t, evaluate, 0f);

        /// <summary>
        /// 同 <see cref="ThrustAt(LaunchFxProfileDef, int, int, float)"/>，再加上轉向推進器拋棄後的主推力全開（§3.6）：
        /// <paramref name="mainBlend"/> 由 0 漸增到 1，推力拉到 <see cref="LaunchFxProfileDef.mainThrust"/>（不會拉低既有推力）。
        /// 放在燃燒時間裁切之前，所以 burnDurationTicks 到期的彈體不會被主推力救回來。
        /// </summary>
        public static float ThrustAt(LaunchFxProfileDef profile, int burnDurationTicks, int t, float evaluate, float mainBlend)
        {
            if (t < 1) { return 0f; }
            float thrust = Mathf.Clamp01(t / (float)Mathf.Max(1, profile.rampTicks));
            if (profile.boosterTicks > 0 && t > profile.boosterTicks)
            {
                thrust = Mathf.Min(thrust, Mathf.Lerp(1f, profile.sustainThrust, Mathf.Clamp01((t - profile.boosterTicks) / 10f)));
            }
            if (profile.thrustCurve != null) { thrust *= Mathf.Max(0f, profile.thrustCurve.Evaluate(evaluate)); }
            if (mainBlend > 0f && profile.mainThrust > 0f)
            {
                thrust = Mathf.Lerp(thrust, Mathf.Max(thrust, profile.mainThrust), Mathf.Clamp01(mainBlend));
            }
            if (burnDurationTicks == 0) { return 0f; }
            if (burnDurationTicks > 0 && t > burnDurationTicks)
            {
                thrust *= 1f - Mathf.Clamp01((t - burnDurationTicks) / 8f);
            }
            return thrust;
        }

        // ====================================================================
        //  轉向：RCS 側推／反推、轉向推進器拋棄、主推力全開（§3.6）
        // ====================================================================

        /// <summary>角加速度是否與角速度反向（＝正在煞停；反推噴嘴的時機）。純函式，單元測試用。</summary>
        public static bool IsBraking(float rate, float accel)
        {
            if (Mathf.Abs(rate) < 1e-3f || Mathf.Abs(accel) < 1e-3f) { return false; }
            return Mathf.Sign(accel) != Mathf.Sign(rate);
        }

        /// <summary>
        /// 每 tick 推進轉向段：以實際機頭角速度（<see cref="IMissileFlight.LaunchRotation"/> 的逐 tick 差）判斷側推、
        /// 反推與轉向完成；完成或逾時就向前拋棄轉向推進器並打開主推力（§3.6）。
        /// </summary>
        private void UpdateTurn(FxCanvas canvas, bool visible, int t)
        {
            if (!profile.turnJets) { return; }
            TickPod(canvas, visible);
            if (bloomAge >= 0 && ++bloomAge > profile.mainBloomTicks) { bloomAge = -1; }
            jetPhase = -1;
            if (!ignited || jettisoned) { turnJetStrength = 0f; return; }

            float rate = Mathf.Abs(rotRate);
            if (!turnActive)
            {
                bool forced = profile.turnStartTick > 0 && t >= profile.turnStartTick;
                if (!forced && rate < profile.turnJetRate) { return; }
                turnActive = true;
                brakeAt = -1;
                lastPulseIndex = -1;
                pulsesFired = 0;
            }
            // 計時從「機頭真的開始轉」算起：turnStartTick 只是讓狀態先就緒，
            // 升空段還沒交接時角速度是 0，太早開始計時會讓上限把轉向砍一半。
            if (turnStartT < 0 && rate >= 0.15f) { turnStartT = t; }
            if (rate >= profile.turnJetRate) { hasTurned = true; }
            // 煞停：角速度已經不高（轉向尾聲）而角加速度開始反向——反推噴嘴從這一刻起算，
            // 之後固定等 turnBrakeTicks 再拋棄（欠阻尼轉向的煞停是一段一段的，不能累計）。
            bool braking = hasTurned && IsBraking(rotRate, rotAccel);
            bool brakeStarted = false;
            if (braking && rate < profile.turnJetRate && brakeAt < 0) { brakeAt = t; brakeStarted = true; }

            // 側推 vs 反推：轉得快就沿轉向推；快停下來、角速度反向遞減時換另一側反推（§3.6）。
            if (rate >= profile.turnJetRate)
            {
                turnJetStrength = 1f;
                turnCouple = rotRate > 0f ? 1 : -1;
                turnDir = turnCouple;
                // 點放：噴幾 tick、停幾 tick，RCS 才像在「修正」而不是一直亮著。
                if (profile.turnJetPulseTicks > 0 && turnStartT >= 0)
                {
                    int period = Mathf.Max(1, profile.turnJetPulseTicks + Mathf.Max(0, profile.turnJetPulseGapTicks));
                    int phase = (t - turnStartT) % period;
                    int index = (t - turnStartT) / period;
                    bool spent = profile.turnJetPulses > 0 && pulsesFired >= profile.turnJetPulses && index != lastPulseIndex;
                    if (phase >= profile.turnJetPulseTicks || spent) { turnJetStrength = 0f; }
                    else
                    {
                        if (index != lastPulseIndex) { lastPulseIndex = index; pulseJustStarted = true; pulsesFired++; }
                        jetPhase = phase;
                        // 每一下是「砰」一聲：第一 tick 最強，之後衰減（不是一條一直亮著的線）。
                        if (profile.turnJetBurstPuffs > 0) { turnJetStrength = PulseEnvelope(phase); }
                    }
                }
            }
            else if (braking && rate > 0.02f)
            {
                turnJetStrength = 0.55f * Mathf.Clamp(profile.turnJetBrakeScale, 0.1f, 1f);
                turnCouple = rotRate > 0f ? -1 : 1;
            }
            else
            {
                turnJetStrength = 0f;
            }

            // 結束：點放次數用完（參考影片的六顆小助推）；或轉向真的煞停且至少噴過 3 下（小角度轉向不會白噴 6 下）；或時間上限。
            bool pulsesDone = profile.turnJetPulses > 0 && pulsesFired >= profile.turnJetPulses;
            // §11：點放用完不再立刻拋棄（慢轉向時六下噴完機頭還在轉）——等反推煞停，或角速度已經幾乎歸零。
            if (profile.turnJetBurstPuffs > 0)
            {
                pulsesDone = pulsesDone && hasTurned && rate < 0.15f && turnJetStrength <= 0f;
            }
            bool stopped = hasTurned && turnStartT >= 0 && t - turnStartT >= profile.turnMinTicks
                           && brakeAt >= 0 && t - brakeAt >= profile.turnBrakeTicks
                           && (profile.turnJetPulses <= 0 || pulsesFired >= 3);
            bool timedOut = profile.turnMaxTicks > 0 && turnStartT >= 0 && t - turnStartT >= profile.turnMaxTicks;
            if (pulsesDone || stopped || timedOut)
            {
                Jettison(canvas, visible, t);
                return;
            }
            if (visible && turnJetStrength > 0f)
            {
                if (profile.turnJetBurstPuffs > 0)
                {
                    // §11：每一下開頭噴一大團氣（衝出去、被阻力停住、膨脹），點放中每 tick 再補一顆中等的。
                    if (pulseJustStarted) { TurnJetBurst(canvas, profile.turnJetBurstPuffs, 1f); }
                    else if (brakeStarted) { TurnJetBurst(canvas, Mathf.Max(1, profile.turnJetBurstPuffs * 2 / 3), 0.7f); }
                    TurnJetBurst(canvas, 1, 0.65f);
                }
                else
                {
                    for (int i = 0; i < Mathf.Max(1, profile.turnJetPuffs); i++) { TurnJetSmoke(canvas); }
                    // 每一次點放的第一 tick 多補一團，讓「六次助推」每一次都看得出是一口氣。
                    if (pulseJustStarted)
                    {
                        for (int i = 0; i < Mathf.Max(0, profile.turnJetPulsePuffs); i++) { TurnJetSmoke(canvas); }
                    }
                }
            }
            pulseJustStarted = false;
        }

        /// <summary>點放中第 <paramref name="phase"/> tick 的火焰強度（純函式）：1 → 0.75 → 0.5 …，最低 0.4。</summary>
        public static float PulseEnvelope(int phase) => Mathf.Max(0.4f, 1f - 0.25f * Mathf.Max(0, phase));

        /// <summary>
        /// 轉向完成：彈頭前方的轉向推進器點火並向「前」脫離（§3.6 第 5 幕），同時主推力全開（第 6 幕）。
        /// 推進器純視覺、不存檔：繼承彈體速度再加一點向前的推力，之後受阻力減速、自轉、淡出。
        /// </summary>
        private void Jettison(FxCanvas canvas, bool visible, int t)
        {
            jettisoned = true;
            turnActive = false;
            turnJetStrength = 0f;
            jettisonT = t;
            Vector3 f = Forward(curRot);
            Vector3 center = curNozzle + f * NozzleOffset;
            podPos = center + f * profile.turnPodOffset;
            podVel = (curNozzle - prevNozzle) + f * profile.turnPodEjectSpeed * Scale;
            // §11：往最後一次轉向的外側飄開，才看得出「掉下去的是另一塊」，而不是黏在機頭前面。
            if (profile.turnPodDrift > 0f)
            {
                Vector3 side = new Vector3(f.z, 0f, -f.x);
                // 順時針轉（往右彎）時弧的外側是左邊（−side）。
                float outward = turnDir != 0 ? -turnDir : (Rand.Bool ? 1f : -1f);
                podVel += side * outward * profile.turnPodDrift * Scale;
            }
            podRot = curRot;
            podSpin = profile.turnPodSpin * Rand.Range(0.6f, 1.4f) * (Rand.Bool ? 1f : -1f);
            podAge = 0;
            if (profile.mainBloomSize > 0f) { bloomAge = 0; }
            if (!visible) { return; }
            if (profile.mainBloomSize <= 0f)
            {
                // 舊版：pod 位置一閃＋小煙。
                canvas.Emit(DMSE_DefOf.DMSE_InterceptFlash, podPos, profile.ignitionFlash * 0.35f * Scale, 0f,
                    new Color(1f, 0.92f, 0.78f, 1f));
                int n0 = LaunchFxBudget.BurstCount(8, canvas.Smoke.Count);
                Burst(canvas, podPos, n0, 0.12f * Scale, new FloatRange(0.7f, 1.2f) * Scale, 1.8f, 200,
                    DMSE_DefOf.DMSE_LaunchSmokeLight, new Color(1f, 0.9f, 0.72f, 0.6f), new Color(1f, 1f, 1f, 0.5f), f, 0.6f);
                return;
            }
            Vector3 seam = center + f * profile.turnJetNoseCells;
            canvas.Emit(DMSE_DefOf.DMSE_InterceptFlash, seam, profile.ignitionFlash * 0.35f * Scale, 0f,
                new Color(1f, 0.92f, 0.78f, 1f));
            // 接縫一圈白氣（「噗」），一半往前。
            int n = LaunchFxBudget.BurstCount(profile.jettisonRingPuffs, canvas.Smoke.Count);
            Burst(canvas, seam, n, 0.2f * Scale, new FloatRange(0.7f, 1.2f) * Scale, 2.2f, 220,
                DMSE_DefOf.DMSE_LaunchSmokeLight, new Color(1f, 0.92f, 0.72f, 0.7f), new Color(1f, 1f, 1f, 0.55f), f, 0.5f);
            // 主發動機點火的一大團熱煙（幕 6「白橘色一大團」），偏向尾後。
            int m = LaunchFxBudget.BurstCount(profile.mainBloomPuffs, canvas.Smoke.Count);
            Burst(canvas, curNozzle, m, 0.1f * Scale, new FloatRange(1.2f, 1.8f) * Scale, 2.4f, 260,
                DMSE_DefOf.DMSE_LaunchSmokeLight, new Color(1f, 0.82f, 0.55f, 0.75f), new Color(1f, 1f, 1f, 0.5f), -f, 0.7f);
        }

        /// <summary>拋棄後的轉向推進器：阻力減速、自轉，壽命到就消失（純視覺，§3.6）。</summary>
        private void TickPod(FxCanvas canvas, bool visible)
        {
            if (podAge < 0) { return; }
            podAge++;
            if (podAge > profile.turnPodLifeTicks) { podAge = -1; return; }
            podVel *= 0.975f;
            podPos += podVel;
            podRot += podSpin;
            // 殘焰期間拖一條細煙，pod 的去向才看得見（§11）。
            if (visible && profile.turnPodDrift > 0f && podAge < profile.turnPodBurnTicks && podAge % 2 == 0
                && LaunchFxBudget.Pressure(canvas.Smoke.Count) < 2)
            {
                Vector3 back = -Forward(podRot);
                canvas.Smoke.Spawn(new SmokePuffSpec
                {
                    def = DMSE_DefOf.DMSE_LaunchSmokeLight,
                    pos = podPos + back * (profile.turnPodLength * 0.5f * Scale),
                    velocity = back * 0.04f * Scale,
                    size = Rand.Range(0.3f, 0.45f) * Scale,
                    growth = 2f,
                    lifeTicks = Mathf.Max(30, Mathf.RoundToInt(140 * LaunchFxBudget.LifeFactor * Rand.Range(0.8f, 1.2f))),
                    drag = 0.1f,
                    buoyancy = 0.001f,
                    windFactor = 0.002f,
                    hotColor = new Color(1f, 0.85f, 0.6f, 0.55f),
                    color = new Color(1f, 1f, 1f, 0.42f),
                    hotTicks = 4,
                    rotation = Rand.Range(0f, 360f),
                    spin = Rand.Range(-1f, 1f)
                });
            }
        }

        /// <summary>
        /// 一下點放的氣團（§11）：機頭、機尾各 <paramref name="count"/> 顆，沿排氣方向 ±25° 衝出、被阻力停住後膨脹，
        /// 在彈體側面留下一團白氣。<paramref name="power"/> 縮放速度與大小（反推 0.7、點放中補氣 0.65）。
        /// </summary>
        private void TurnJetBurst(FxCanvas canvas, int count, float power)
        {
            if (LaunchFxBudget.Pressure(canvas.Smoke.Count) >= 2) { return; }
            count = Mathf.Max(1, LaunchFxBudget.BurstCount(count, canvas.Smoke.Count));
            Vector3 f = Forward(curRot);
            Vector3 side = new Vector3(f.z, 0f, -f.x);
            Vector3 center = curNozzle + f * NozzleOffset;
            Vector3 missileVel = curNozzle - prevNozzle;
            for (int i = 0; i < count; i++)
            {
                EmitBurstPuff(canvas, center + f * profile.turnJetNoseCells, side * -turnCouple, missileVel, power);
                EmitBurstPuff(canvas, center - f * profile.turnJetTailCells, side * turnCouple, missileVel, power);
            }
        }

        private void EmitBurstPuff(FxCanvas canvas, Vector3 mouth, Vector3 dir, Vector3 missileVel, float power)
        {
            Vector3 d = MissileMapEntryUtility.HeadingVector(MissileMapEntryUtility.HeadingOf(dir) + Rand.Range(-25f, 25f));
            canvas.Smoke.Spawn(new SmokePuffSpec
            {
                def = DMSE_DefOf.DMSE_LaunchSmokeLight,
                pos = mouth + d * Rand.Range(0.1f, 0.3f) * Scale,
                velocity = d * profile.turnJetBurstSpeed * Rand.Range(0.6f, 1.2f) * power * Scale + missileVel * 0.3f,
                size = profile.turnJetBurstSize.RandomInRange * power * Scale,
                growth = 2.4f,
                lifeTicks = Mathf.Max(30, Mathf.RoundToInt(200 * LaunchFxBudget.LifeFactor * Rand.Range(0.8f, 1.2f))),
                drag = 0.14f,
                buoyancy = 0.001f,
                windFactor = 0.002f,
                hotColor = new Color(1f, 0.9f, 0.72f, 0.65f),
                color = new Color(1f, 1f, 1f, 0.5f),
                hotTicks = 4,
                rotation = Rand.Range(0f, 360f),
                spin = Rand.Range(-1.5f, 1.5f)
            });
            puffsEmitted++;
        }

        /// <summary>RCS 噴嘴的排氣煙：前後各一顆小白煙，速度沿噴嘴方向（不影響推力計算）。</summary>
        private void TurnJetSmoke(FxCanvas canvas)
        {
            if (LaunchFxBudget.Pressure(canvas.Smoke.Count) >= 2) { return; }
            Vector3 f = Forward(curRot);
            Vector3 side = new Vector3(f.z, 0f, -f.x);
            Vector3 center = curNozzle + f * NozzleOffset;
            Vector3 missileVel = curNozzle - prevNozzle;
            // 力矩方向決定排氣方向：機頭向力矩的反側、機尾向同側（§3.6）。
            // 噴嘴位置與 DrawTurnJets 用同一組量測值（貼圖鼻尖／助推器底），煙才不會從彈體裡面冒出來。
            EmitJetSmoke(canvas, center + f * profile.turnJetNoseCells, side * -turnCouple, missileVel);
            EmitJetSmoke(canvas, center - f * profile.turnJetTailCells, side * turnCouple, missileVel);
        }

        private void EmitJetSmoke(FxCanvas canvas, Vector3 mouth, Vector3 dir, Vector3 missileVel)
        {
            canvas.Smoke.Spawn(new SmokePuffSpec
            {
                def = DMSE_DefOf.DMSE_LaunchSmokeLight,
                pos = mouth + dir * Rand.Range(0.05f, 0.2f) * Scale,
                velocity = dir * 0.05f * Scale + missileVel * 0.15f,
                size = Rand.Range(0.22f, 0.34f) * Scale,
                growth = 1.8f,
                lifeTicks = Mathf.Max(30, Mathf.RoundToInt(80 * LaunchFxBudget.LifeFactor * Rand.Range(0.8f, 1.2f))),
                drag = 0.1f,
                buoyancy = 0.001f,
                windFactor = 0.002f,
                hotColor = new Color(1f, 0.92f, 0.8f, 0.5f),
                color = new Color(1f, 1f, 1f, 0.4f),
                hotTicks = 3,
                rotation = Rand.Range(0f, 360f),
                spin = Rand.Range(-1f, 1f)
            });
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
            // 大小用「至少 smokeThrustFloor」的推力：續航段推力低，但煙跡不該細成一串點（§11）；
            // 主推力全開後再放大 mainSmokeScale，拋棄前後煙跡粗細看得出差別。
            float sizeThrust = Mathf.Max(curThrust, profile.smokeThrustFloor);
            float size0 = profile.smokeSize * (0.6f + 0.4f * sizeThrust) * Scale * (1f + (profile.mainSmokeScale - 1f) * mainBlend);
            Color trailColor = profile.mainSmokeWhiten > 0f && mainBlend > 0f
                ? Color.Lerp(profile.smokeColor, new Color(1f, 1f, 1f, profile.smokeColor.a), profile.mainSmokeWhiten * mainBlend)
                : profile.smokeColor;
            float trailShade = profile.smokeShade * (1f - profile.mainSmokeWhiten * mainBlend);
            // 主推力全開：煙團往後噴得更快（尾跡拉得更開、更有力）。
            float exhaust = profile.exhaustSpeed * (1f + (profile.mainExhaustScale - 1f) * mainBlend);
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
                    velocity = (-f * exhaust * Rand.Range(0.6f, 1f) * curThrust
                                + side * Rand.Range(-0.15f, 0.15f) * exhaust
                                + missileVel * profile.inherit) * Scale,
                    size = size0 * Rand.Range(0.8f, 1.2f),
                    growth = profile.smokeGrowth,
                    lifeTicks = Mathf.Max(30, Mathf.RoundToInt(life * Rand.Range(0.85f, 1.15f))),
                    drag = 0.08f,
                    buoyancy = profile.buoyancy,
                    windFactor = 0.002f,
                    hotColor = profile.smokeHotColor,
                    color = ShadeTint(trailColor, trailShade, Rand.Range(-1f, 1f)),
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

            if (thrust < 0.02f && podAge < 0 && turnJetStrength <= 0f && bloomAge < 0) { return; }
            Vector3 pos = Vector3.Lerp(prevNozzle, curNozzle, alpha);
            float rot = Mathf.LerpAngle(prevRot, curRot, alpha);
            Vector3 f = Forward(rot);
            float main = Mathf.Lerp(0f, mainBlend, alpha);

            // 轉向 RCS（§3.6）：機頭與機尾各一盞，方向由力矩決定；煞停時換另一側（反推）。
            if (turnJetStrength > 0f) { DrawTurnJets(batch, pos, f, scale); }
            DrawPod(batch, scale, alpha);
            DrawBloom(batch, pos, scale, alpha);

            if (thrust < 0.02f) { return; }
            float time = Time.realtimeSinceStartup;
            float flicker = 1f + 0.1f * (0.6f * Mathf.Sin(time * 37f + seed) + 0.4f * Mathf.Sin(time * 23.7f + seed * 1.3f));
            float speed = (curNozzle - prevNozzle).magnitude;
            float length = profile.coreLength * thrust * flicker * (1f + 0.3f * Mathf.Min(speed / 3f, 1f)) * scale
                           * (1f + (profile.mainPlumeScale - 1f) * main);
            float width = profile.plumeWidth * (0.6f + 0.4f * thrust) * scale * (1f + profile.mainPlumeWiden * main);
            Color flame = Color.Lerp(profile.flameColor, profile.mainFlameColor, main * 0.7f);

            Material outer = ((Time.frameCount / 3 + seed) & 1) == 0 ? LaunchFxMaterials.OuterA : LaunchFxMaterials.OuterB;
            Quad(batch, outer, pos - f * (0.9f * length), rot, width, 1.8f * length, 2.2f, flame);
            Quad(batch, LaunchFxMaterials.Core, pos - f * (0.5f * length), rot, 0.35f * width, length, 2.4f, profile.coreColor);
            Color halo = flame;
            halo.a = 0.6f;
            float d = 2.5f * width * thrust * (1f + profile.mainHaloScale * main);
            Quad(batch, LaunchFxMaterials.Halo, pos, 0f, d, d, 2.3f, halo);
        }

        /// <summary>
        /// RCS 側推／反推的火焰（§3.6）：前後兩個噴嘴、一左一右；<see cref="turnCouple"/> = +1 順時針，
        /// 機頭排氣往力矩反側、機尾往同側。煞停時強度較小（<see cref="LaunchFxProfileDef.turnJetBrakeScale"/>）。
        /// </summary>
        private void DrawTurnJets(DrawBatch batch, Vector3 nozzle, Vector3 f, float scale)
        {
            float strength = turnJetStrength;
            if (strength < 0.02f || turnCouple == 0) { return; }
            Vector3 side = new Vector3(f.z, 0f, -f.x);
            Vector3 center = nozzle + f * NozzleOffset;
            float length = Mathf.Max(0.2f, profile.turnJetLength * strength * scale);
            float width = Mathf.Max(0.12f, profile.turnJetWidth * strength * scale);
            // 噴嘴對齊實際貼圖：鼻尖與助推器底，而不是貼圖內的近似位置（skyFaller 512² 量出來的 1.85／1.81 格）。
            // 位置不乘 effectScale：貼圖大小不受特效縮放影響，乘了會讓噴嘴浮在貼圖外。
            Jet(batch, center + f * profile.turnJetNoseCells, side * -turnCouple, length, width);
            Jet(batch, center - f * profile.turnJetTailCells, side * turnCouple, length, width);
        }

        private void Jet(DrawBatch batch, Vector3 mouth, Vector3 dir, float length, float width)
        {
            float rotation = MissileMapEntryUtility.HeadingOf(dir);
            Color c = profile.turnJetColor;
            Quad(batch, LaunchFxMaterials.OuterA, mouth + dir * (0.5f * length), rotation, width * 1.25f, length, 2.15f, c);
            Quad(batch, LaunchFxMaterials.Core, mouth + dir * (0.42f * length), rotation, 0.38f * width, 0.8f * length, 2.18f, profile.coreColor);
            Color halo = profile.turnJetColor;
            halo.a = 0.62f;
            // 點放第一 tick 噴嘴亮一下（§11），讓每一下都是「砰」。
            float d = (jetPhase == 0 ? 2.6f : 1.7f) * width;
            Quad(batch, LaunchFxMaterials.Halo, mouth + dir * (0.1f * length), 0f, d, d, 2.12f, halo);
        }

        /// <summary>
        /// 主推力點火光球（§11 幕 6）：拋棄的同一 tick 在噴口亮起，前 4 tick 長到最大，之後到 <see cref="LaunchFxProfileDef.mainBloomTicks"/> 淡掉。
        /// </summary>
        private void DrawBloom(DrawBatch batch, Vector3 nozzle, float scale, float alpha)
        {
            if (bloomAge < 0 || profile.mainBloomSize <= 0f) { return; }
            float a = bloomAge + alpha;
            float grow = Mathf.Clamp01(a / 4f);
            float fade = 1f - Mathf.Clamp01((a - 4f) / Mathf.Max(1f, profile.mainBloomTicks - 4f));
            float k = grow * fade;
            if (k <= 0.01f) { return; }
            float d = profile.mainBloomSize * scale * (0.6f + 0.4f * grow);
            Color outer = profile.mainFlameColor;
            outer.a = 0.75f * k;
            Quad(batch, LaunchFxMaterials.Halo, nozzle, 0f, d, d, 2.25f, outer);
            Color core = profile.coreColor;
            core.a = 0.9f * k;
            Quad(batch, LaunchFxMaterials.Halo, nozzle, 0f, 0.45f * d, 0.45f * d, 2.35f, core);
        }

        /// <summary>
        /// 拋棄後的轉向推進器（§3.6 第 5–6 幕）：翻滾著往前飛、淡出；剛脫離的前 24 tick 還帶著自己的小尾焰。
        /// </summary>
        private void DrawPod(DrawBatch batch, float scale, float alpha)
        {
            if (podAge < 0) { return; }
            float life = Mathf.Clamp01(1f - (podAge + alpha) / Mathf.Max(1, profile.turnPodLifeTicks));
            Color c = new Color(1f, 1f, 1f, Mathf.Clamp01(life * 4f));
            Quad(batch, LaunchFxMaterials.TurnPodFor(profile.turnPodTexPath), podPos, podRot,
                profile.turnPodWidth * scale, profile.turnPodLength * scale, 2.1f, c);

            float burn = 1f - Mathf.Clamp01((podAge + alpha) / Mathf.Max(1, profile.turnPodBurnTicks));
            if (burn <= 0.02f) { return; }
            Vector3 f = Forward(podRot);
            float length = 1.1f * burn * scale;
            Vector3 tail = podPos - f * (profile.turnPodLength * 0.5f * scale);
            Quad(batch, LaunchFxMaterials.OuterA, tail - f * (0.5f * length), podRot,
                0.34f * scale * burn, length, 2.16f, profile.flameColor);
            Quad(batch, LaunchFxMaterials.Core, tail - f * (0.4f * length), podRot,
                0.12f * scale * burn, 0.7f * length, 2.19f, profile.coreColor);
        }


        private static void Quad(DrawBatch batch, Material mat, Vector3 center, float rotation, float width, float length, float altitudeOffset, Color color)
        {
            center.y = AltitudeLayer.MetaOverlays.AltitudeFor(altitudeOffset);
            Matrix4x4 m = Matrix4x4.TRS(center, Quaternion.AngleAxis(rotation, Vector3.up), new Vector3(width, 1f, length));
            batch.DrawMesh(MeshPool.plane10, m, mat, 0, color);
        }
    }
}
