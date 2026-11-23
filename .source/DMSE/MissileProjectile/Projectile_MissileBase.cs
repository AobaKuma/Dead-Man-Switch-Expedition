using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 導彈 Projectile 的共用基底（docs/Missile_Skyfaller轉Projectile_計畫.md §3.2、§8.2）。
    ///
    /// 運動不走原版 Projectile 的直線＋拋物線，而是重現 <see cref="Skyfaller"/> 的時間軸動畫：
    ///   - 參數來自 <see cref="MissileFlightExtension"/>（與原 &lt;skyfaller&gt; 同名同語意）。
    ///   - 時間軸計數沿用 Projectile 自己的 <c>ticksToImpact</c> 欄位，語意與 Skyfaller 相同
    ///     （reversed：0 起遞增到 <see cref="LeaveMapAfterTicks"/>；正向：遞減到 0）。
    ///   - <see cref="Launch"/> 後把 <c>landed</c> 設為 true：原版 <c>TickInterval</c> 在扣完 lifetime 後遇 landed 即返回，
    ///     因此出界銷毀、自由攔截（護盾／牆／Pawn）與 ImpactSomething 都不會發生，邊界與命中完全由本類別決定。
    ///     此行為依賴 1.7 <c>Projectile.TickInterval</c> 的實作，遊戲更新後需重新確認。
    ///
    /// 地面位置與畫面位置分開：
    ///   - <see cref="ExactPosition"/>：地面（目前固定在生成格，與原 Skyfaller 的 Position／影子一致）。
    ///   - <see cref="DrawPos"/>：畫面上的彈體位置，等同原 <c>Skyfaller.DrawPos</c>（原版 Projectile 的 DrawPos 是地面位置，這裡刻意改掉）。
    ///
    /// 升空段的姿態（<see cref="GetLaunchPose"/>）沿用舊 ScorerProjectile／InterceptProjectile 的規則：
    /// z 位置曲線與旋轉曲線只在東／西朝向生效（南北向發射靠 angle 方向的位移表現上升）。
    /// </summary>
    public class Projectile_MissileBase : Projectile, IMissileFlight, IRoofCollapseAlert
    {
        public const float DefaultAngle = -33.7f;
        private const int RoofHitPreDelay = 15;
        private const int LeaveMapAfterTicksDefault = 220;

        // ---- 時間軸（與 Skyfaller 同名同語意；ticksToImpact 用 Projectile 的欄位） ----
        public int ticksToImpactMax = LeaveMapAfterTicksDefault;
        public int ticksToDiscard;
        public int ageTicks;
        public float angle;
        protected bool hasHitRoof;
        protected bool hasImpacted;
        protected bool hasLeftMap;
        private bool anticipationSoundPlayed;

        // ---- 離場巡航（§8.3）：有出發方位的升空彈在交接後轉向並飛出地圖邊緣 ----
        private bool hasDepartureHeading;
        private float departureHeading;
        private IntVec3 launchCell = IntVec3.Invalid;
        private Vector3 launchOffset;
        private MissileCruiseState cruise;
        /// <summary>交接後尾焰進度固定在交接當下（發射特效的 ticksSinceSpawn 上限）。</summary>
        private int effectHoldTicks = int.MaxValue;

        /// <summary>
        /// 設定出發方位（世界航跡在發射地的切線方位，0 = 北、順時針）。設定後升空段會在交接點轉向並飛出地圖；
        /// 不設定（攔截彈、跨圖層發射）則照原本跑完升空動畫。須在 Launch 之後、第一個 tick 之前呼叫。
        /// </summary>
        public void SetDepartureHeading(float heading)
        {
            hasDepartureHeading = true;
            departureHeading = heading;
        }

        /// <summary>把升空彈的軌跡起點偏移到發射架實際繪製的彈藥槽位置。</summary>
        public void SetLaunchOffset(Vector3 offset) => launchOffset = offset;

        private Vector3 AscentCenter => LaunchMouthCenter + MissileMapEntryUtility.HeadingVector(angle) * EmergeLift;

        /// <summary>發射點（井口中心／彈藥槽）：沒有出井段時就是升空起點。</summary>
        private Vector3 LaunchMouthCenter => this.TrueCenter() + launchOffset;

        // ---- 出井（MissileFlightExtension.emergeTicks） ----
        /// <summary>出井段已經過的 tick；到 emergeTicks 後才推進升空時間軸。</summary>
        private int emergeAge;

        /// <summary>正在出井（升空時間軸尚未開始）。</summary>
        public bool Emerging => Flight.reversed && emergeAge < Flight.emergeTicks && !cruise.active;

        /// <summary>彈體長邊的繪製尺寸（格）。</summary>
        private float BodyLength => def.graphicData != null ? Mathf.Max(def.graphicData.drawSize.x, def.graphicData.drawSize.y) : 1f;

        /// <summary>升空起點比發射點沿機身軸線高出多少格：彈尾離井口 emergeClearance（沒有出井段 = 0）。</summary>
        private float EmergeLift
        {
            get
            {
                MissileFlightExtension f = Flight;
                return f.emergeTicks > 0 ? f.emergeMouthOffset + f.emergeTailFraction * BodyLength + f.emergeClearance : 0f;
            }
        }

        /// <summary>出井段的彈體中心：從「鼻尖在井口」沿軸線推到升空起點（<see cref="MissileFlightExtension.EmergeProgress"/>）。</summary>
        private Vector3 EmergeDrawPos
        {
            get
            {
                MissileFlightExtension f = Flight;
                Vector3 dir = MissileMapEntryUtility.HeadingVector(angle);
                Vector3 mouth = LaunchMouthCenter + dir * f.emergeMouthOffset;
                Vector3 start = mouth - dir * (f.emergeNoseFraction * BodyLength);
                Vector3 end = AscentCenter;
                float k = MissileFlightExtension.EmergeProgress((float)emergeAge / Mathf.Max(1, f.emergeTicks));
                return Vector3.Lerp(start, end, k);
            }
        }

        public bool Cruising => cruise.active;
        public float CruiseNoseAngle => cruise.nose;
        public float DepartureHeading => departureHeading;

        private Sustainer floatingSoundPlaying;
        private Sustainer anticipationSoundPlaying;
        private Material cachedShadowMaterial;
        private MissileFlightExtension cachedFlight;

        private static readonly MissileFlightExtension FallbackFlight = new MissileFlightExtension();

        public MissileFlightExtension Flight
        {
            get
            {
                if (cachedFlight == null)
                {
                    cachedFlight = def.GetModExtension<MissileFlightExtension>();
                    if (cachedFlight == null)
                    {
                        Log.ErrorOnce($"[DMSE] {def.defName} 缺少 MissileFlightExtension，改用預設飛行參數。", def.shortHash ^ 0x4D46);
                        cachedFlight = FallbackFlight;
                    }
                }
                return cachedFlight;
            }
        }

        /// <summary>時間軸計數（升空遞增、墜落遞減到 0 即命中）。</summary>
        public int TicksToImpact => ticksToImpact;

        public int LeaveMapAfterTicks => ticksToDiscard <= 0 ? LeaveMapAfterTicksDefault : ticksToDiscard;

        /// <summary>動畫進度 0–1（同 <c>Skyfaller.TimeInAnimation</c>）。</summary>
        public float TimeInAnimation => Flight.reversed
            ? (float)ticksToImpact / LeaveMapAfterTicks
            : 1f - (float)ticksToImpact / ticksToImpactMax;

        // ====================================================================
        //  位置
        // ====================================================================

        /// <summary>地面位置：升空段為生成格中心（原 Skyfaller 的 Position 不隨動畫移動）；巡航段為彈體正下方的格。</summary>
        public override Vector3 ExactPosition => this.TrueCenter();

        /// <summary>畫面上的彈體位置（原 <c>Skyfaller.DrawPos</c>；巡航段為積分出的位置）。</summary>
        public override Vector3 DrawPos
        {
            get
            {
                if (cruise.active) { return cruise.pos; }
                if (Emerging) { return EmergeDrawPos; }
                if (Flight.reversed)
                {
                    return MissileDepartureKinematics.AscentDrawPos(Flight, AscentCenter, ticksToImpact, angle, LeaveMapAfterTicks);
                }
                return MissileArrivalKinematics.FallDrawPos(Flight, this.TrueCenter(), ticksToImpact, ticksToImpactMax, angle);
            }
        }

        public override Quaternion ExactRotation
        {
            get
            {
                GetLaunchPose(out _, out float rot);
                return Quaternion.AngleAxis(rot, Vector3.up);
            }
        }

        /// <summary>依目前時間軸套用曲線後的繪製位置與額外旋轉。</summary>
        protected virtual void GetDrawPositionAndRotation(ref Vector3 drawLoc, out float extraRotation)
        {
            MissileFlightExtension f = Flight;
            float t = TimeInAnimation;
            extraRotation = 0f;
            if (cruise.active)
            {
                // 巡航：位置已含高度，機頭角為絕對方位（換算成相對 Rotation 的額外旋轉）。
                extraRotation = cruise.nose - Rotation.AsAngle;
                return;
            }
            if (Emerging)
            {
                // 出井：沿機身軸線直直推出，不套升空曲線。
                extraRotation = angle - Rotation.AsAngle;
                return;
            }
            if (f.reversed)
            {
                // 升空：沿用舊 ScorerProjectile / InterceptProjectile 的覆寫——曲線只在東/西朝向生效。
                extraRotation = MissileDepartureKinematics.AscentCurveOffset(f, Rotation, t, ref drawLoc);
                extraRotation += angle - Rotation.AsAngle;
                return;
            }

            // 落地：原版 Skyfaller.GetDrawPositionAndRotation（angleCurve 於 Tick 更新，不在繪製時改狀態）。
            // 彈體貼圖一律機頭朝北；原版公式下彈體從 angle 方位飛向落點（行進方向 = angle + 180），所以機頭轉到 angle + 180。
            if (f.rotateGraphicTowardsDirection) { extraRotation = angle + 180f; }
            if (f.rotationCurve != null) { extraRotation += f.rotationCurve.Evaluate(t); }
            if (f.xPositionCurve != null) { drawLoc.x += f.xPositionCurve.Evaluate(t); }
            if (f.zPositionCurve != null) { drawLoc.z += f.zPositionCurve.Evaluate(t); }
        }

        /// <summary>
        /// 目前的彈體繪製位置與朝向角（給尾焰、追蹤與繪製共用）。
        /// 舊 Skyfaller 版只在繪製時快取，畫面外或一幀多 tick 時會過時；這裡每次依時間軸重算。
        /// </summary>
        public void GetLaunchPose(out Vector3 pos, out float rotation)
        {
            pos = DrawPos;
            GetDrawPositionAndRotation(ref pos, out float extra);
            rotation = Rotation.AsAngle + extra;
        }

        // ---- IMissileFlight ----
        public Vector3 LaunchDrawPos { get { GetLaunchPose(out Vector3 p, out _); return p; } }
        public float LaunchRotation { get { GetLaunchPose(out _, out float r); return r; } }
        public Vector3 VisualPos => DrawPos;
        public IntVec3 GroundCell => Position;
        public int AscentTicks => ticksToDiscard;
        public float EffectSpeed(float evaluate) => Flight.speedCurve?.Evaluate(evaluate) ?? 1f;

        /// <summary>
        /// 尾焰進度：升空段同原公式；巡航段固定在交接當下（至少到點火之後），讓火焰與煙在飛越地圖時維持交接時的強度，
        /// 不會像原升空動畫末段那樣熄掉。
        /// </summary>
        public float EffectEvaluate(int ticksSinceSpawn, int ignitionDelayTicks)
        {
            int ticks = Mathf.Min(ticksSinceSpawn, Mathf.Max(effectHoldTicks, ignitionDelayTicks + 1));
            return (float)ticks / (ticksToDiscard + ignitionDelayTicks);
        }

        // ====================================================================
        //  生成 / 發射
        // ====================================================================

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (respawningAfterLoad || BeingTransportedOnGravship) { return; }
            MissileFlightExtension f = Flight;
            ticksToImpact = ticksToImpactMax = f.ticksToImpactRange.RandomInRange;
            ticksToDiscard = f.ticksToDiscardInReverse != IntRange.Zero ? f.ticksToDiscardInReverse.RandomInRange : -1;
            angle = f.angleCurve != null ? f.angleCurve.Evaluate(0f) : DefaultAngle;
        }

        /// <summary>
        /// 原版 Launch 會依起訖點距離重設 ticksToImpact；這裡保留 SpawnSetup 決定的時間軸，
        /// 並設 landed 停用原版直線運動（見類別說明）。
        /// </summary>
        public override void Launch(Thing launcher, Vector3 origin, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget,
            ProjectileHitFlags hitFlags, bool preventFriendlyFire = false, Thing equipment = null, ThingDef targetCoverDef = null)
        {
            int timeline = ticksToImpact;
            base.Launch(launcher, origin, usedTarget, intendedTarget, hitFlags, preventFriendlyFire, equipment, targetCoverDef);
            ticksToImpact = timeline;
            landed = true;
            launchCell = Position;
        }

        // ====================================================================
        //  Tick（順序同 Skyfaller.Tick：comps → 音效 → 時間軸 → ageTicks）
        // ====================================================================

        protected override void Tick()
        {
            base.Tick();
            MissileFlightExtension f = Flight;
            if (f.floatingSound != null && (floatingSoundPlaying == null || floatingSoundPlaying.Ended))
            {
                floatingSoundPlaying = f.floatingSound.TrySpawnSustainer(SoundInfo.InMap(new TargetInfo(this), MaintenanceType.PerTick));
            }
            floatingSoundPlaying?.Maintain();

            if (f.reversed && Emerging)
            {
                // 出井段：升空時間軸不動，只推進出井進度（彈體懸停點＝升空起點，結束時無縫接上）。
                emergeAge++;
                ageTicks++;
                return;
            }
            if (f.reversed)
            {
                // 推進前的姿態：交接時用來求交接速度（與 SimulateDepartureTicks 同順序）。
                Vector3 previousPose = cruise.active || !hasDepartureHeading ? default : LaunchDrawPos;
                ticksToImpact++;
                if (!anticipationSoundPlayed && f.anticipationSound != null && ticksToImpact > f.anticipationSoundTicks)
                {
                    PlayAnticipationSound(f);
                }
                if (cruise.active)
                {
                    MissileDepartureKinematics.CruiseStep(f, ref cruise, ticksToImpact, LeaveMapAfterTicks, Map.Size);
                    if (!MissileDepartureKinematics.InMap(cruise.pos, Map.Size) || cruise.ticks >= f.maxCruiseTicks)
                    {
                        Depart();
                        return;
                    }
                    IntVec3 below = cruise.pos.ToIntVec3();
                    if (below.InBounds(Map) && below != Position) { Position = below; }
                }
                else
                {
                    if (hasDepartureHeading)
                    {
                        Vector3 center = AscentCenter;
                        Vector3 pose = MissileDepartureKinematics.AscentPose(f, center, Rotation, ticksToImpact, angle, LeaveMapAfterTicks, out float nose);
                        if (MissileDepartureKinematics.ShouldHandoff(f, ticksToImpact, LeaveMapAfterTicks, pose, center))
                        {
                            cruise = MissileDepartureKinematics.BeginCruise(f, departureHeading, pose, previousPose, nose, ticksToImpact, LeaveMapAfterTicks);
                            effectHoldTicks = ageTicks + 1;
                            if (!MissileDepartureKinematics.InMap(cruise.pos, Map.Size))
                            {
                                Depart();
                                return;
                            }
                        }
                    }
                    if (!cruise.active && ticksToImpact >= LeaveMapAfterTicks && !hasLeftMap)
                    {
                        Depart();
                        return;
                    }
                }
            }
            else
            {
                ticksToImpact--;
                if (f.angleCurve != null) { angle = f.angleCurve.Evaluate(TimeInAnimation); }
                if (ticksToImpact <= RoofHitPreDelay && !hasHitRoof)
                {
                    hasHitRoof = true;
                    HitRoof();
                }
                if (!anticipationSoundPlayed && f.anticipationSound != null && ticksToImpact < f.anticipationSoundTicks)
                {
                    PlayAnticipationSound(f);
                }
                anticipationSoundPlaying?.Maintain();
                if (ticksToImpact <= 0 && !hasImpacted)
                {
                    hasImpacted = true;
                    MissileImpact();
                    ageTicks++;
                    return;
                }
            }
            ageTicks++;
        }

        private void Depart()
        {
            hasLeftMap = true;
            LeaveMap();
            ageTicks++;
        }

        private void PlayAnticipationSound(MissileFlightExtension f)
        {
            anticipationSoundPlayed = true;
            TargetInfo target = new TargetInfo(Position, Map);
            if (f.anticipationSound.sustain)
            {
                anticipationSoundPlaying = f.anticipationSound.TrySpawnSustainer(target);
            }
            else
            {
                f.anticipationSound.PlayOneShot(target);
            }
        }

        /// <summary>離場動畫結束（reversed）。預設直接消失；離場彈在此建立世界航跡。</summary>
        protected virtual void LeaveMap()
        {
            Destroy();
        }

        /// <summary>落地動畫結束（正向）。預設直接消失；落地彈在此引爆。</summary>
        protected virtual void MissileImpact()
        {
            Destroy();
        }

        /// <summary>
        /// 同 <c>Skyfaller.HitRoof</c>：打穿屋頂並崩塌周圍。厚岩頂不在此崩塌（原版會在落點填滿崩落岩石，把之後的爆炸悶在岩石裡），
        /// 改由 <see cref="MissileImpactUtility.Detonate"/> 依彈頭能否穿透決定開孔或在岩層表面引爆。
        /// </summary>
        protected virtual void HitRoof()
        {
            MissileFlightExtension f = Flight;
            if (!f.hitRoof) { return; }
            Map map = Map;
            CellRect cr = this.OccupiedRect();
            bool ThinRoofed(IntVec3 x) => x.InBounds(map) && x.Roofed(map) && !MissileImpactUtility.UnderThickRoof(x, map);
            if (!cr.Cells.Any(ThinRoofed)) { return; }
            RoofDef roof = cr.Cells.First(ThinRoofed).GetRoof(map);
            if (!roof.soundPunchThrough.NullOrUndefined())
            {
                roof.soundPunchThrough.PlayOneShot(new TargetInfo(Position, map));
            }
            CellRect area = cr.ExpandedBy(f.minimalRoofDestruction ? 0 : 1).ClipInsideMap(map);
            RoofCollapserImmediate.DropRoofInCells(area.Cells.Where(c =>
            {
                if (!ThinRoofed(c)) { return false; }
                if (cr.Contains(c)) { return true; }
                if (c.GetFirstPawn(map) != null) { return false; }
                Building edifice = c.GetEdifice(map);
                return edifice == null || !edifice.def.holdsRoof;
            }), map);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            base.Destroy(mode);
            anticipationSoundPlaying?.End();
            anticipationSoundPlaying = null;
        }

        public RoofCollapseResponse Notify_OnBeforeRoofCollapse() => RoofCollapseResponse.None;

        // ====================================================================
        //  繪製（同 Skyfaller.DrawAt：彈體一次＋落點影子）
        // ====================================================================

        public override Graphic Graphic
        {
            get
            {
                if (Flight.fadeInTicks > 0 || Flight.fadeOutTicks > 0)
                {
                    return def.graphicData.GraphicColoredFor(this);
                }
                return base.Graphic;
            }
        }

        private bool FadingOut => Flight.fadeOutTicks > 0 && ageTicks >= LeaveMapAfterTicks - Flight.fadeOutTicks;

        public override Color DrawColor
        {
            get
            {
                MissileFlightExtension f = Flight;
                if (f.fadeInTicks > 0 && ageTicks < f.fadeInTicks)
                {
                    Color c = base.DrawColor;
                    c.a *= Mathf.Lerp(0f, 1f, Mathf.Min((float)ageTicks / f.fadeInTicks, 1f));
                    return c;
                }
                if (FadingOut)
                {
                    Color c = base.DrawColor;
                    c.a *= Mathf.Lerp(1f, 0f, Mathf.Max(ageTicks - (float)(LeaveMapAfterTicks - f.fadeOutTicks), 0f) / f.fadeOutTicks);
                    return c;
                }
                return base.DrawColor;
            }
            set => base.DrawColor = value;
        }

        private Material ShadowMaterial
        {
            get
            {
                if (cachedShadowMaterial == null && !Flight.shadow.NullOrEmpty())
                {
                    cachedShadowMaterial = MaterialPool.MatFrom(Flight.shadow, ShaderDatabase.Transparent);
                }
                return cachedShadowMaterial;
            }
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            GetDrawPositionAndRotation(ref drawLoc, out float extraRotation);
            if (!WorldComponent_GravshipController.GravshipRenderInProgess)
            {
                if (Emerging) { DrawEmerging(drawLoc); }
                else { Graphic.Draw(drawLoc, flip ? Rotation.Opposite : Rotation, this, extraRotation); }
            }
            Material shadow = ShadowMaterial;
            if (shadow != null)
            {
                // 影子留在發射點（同原 Skyfaller）：巡航段 Position 跟著彈體移動，所以改用記下的發射格；影子約 200 tick 後淡出。
                Vector3 shadowCenter = cruise.active && launchCell.IsValid
                    ? launchCell.ToVector3Shifted().WithY(def.Altitude) : this.TrueCenter();
                Skyfaller.DrawDropSpotShadow(shadowCenter, Rotation, shadow, Flight.shadowSize, ticksToImpact);
            }
        }

        /// <summary>
        /// 出井段：只畫井口以上的部分。沿機身軸線算出井口落在貼圖的哪個比例，用裁掉彈尾那一截的四邊形（UV 同步裁切）來畫，
        /// 所以彈體像是從井裡被推出來，而不是整枚浮在井上。
        /// </summary>
        private void DrawEmerging(Vector3 drawLoc)
        {
            MissileFlightExtension f = Flight;
            Vector2 size = def.graphicData.drawSize;
            Vector3 dir = MissileMapEntryUtility.HeadingVector(angle);
            Vector3 mouth = LaunchMouthCenter + dir * f.emergeMouthOffset;
            Vector3 rel = mouth - drawLoc;
            rel.y = 0f;
            // 井口在貼圖長軸上的位置（−0.5 = 彈尾端、0.5 = 鼻尖端）；它以下（彈尾側）的部分還在井裡。
            float hidden = Mathf.Clamp01(Vector3.Dot(rel, dir) / Mathf.Max(0.01f, size.y) + 0.5f);
            if (hidden >= 0.999f) { return; }
            Material mat = Graphic.MatAt(Rotation, this);
            Matrix4x4 matrix = Matrix4x4.TRS(drawLoc, Quaternion.AngleAxis(angle, Vector3.up), new Vector3(size.x, 1f, size.y));
            Graphics.DrawMesh(ClippedPlane(hidden), matrix, mat, 0);
        }

        private const int ClipSteps = 128;
        private static readonly Dictionary<int, Mesh> clippedPlanes = new Dictionary<int, Mesh>();

        /// <summary>單位平面（同原版 plane：x、z ∈ [−0.5, 0.5]、機頭朝 +z）裁掉下方 <paramref name="hiddenFraction"/>，UV 一起裁。</summary>
        private static Mesh ClippedPlane(float hiddenFraction)
        {
            int key = Mathf.Clamp(Mathf.RoundToInt(hiddenFraction * ClipSteps), 0, ClipSteps - 1);
            if (clippedPlanes.TryGetValue(key, out Mesh mesh) && mesh != null) { return mesh; }
            float v0 = (float)key / ClipSteps;
            float z0 = v0 - 0.5f;
            mesh = new Mesh
            {
                name = "DMSE_ClippedPlane_" + key,
                vertices = new[] { new Vector3(-0.5f, 0f, z0), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, z0) },
                uv = new[] { new Vector2(0f, v0), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, v0) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            clippedPlanes[key] = mesh;
            return mesh;
        }

        // ====================================================================
        //  存檔
        // ====================================================================

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ticksToImpactMax, "ticksToImpactMax", LeaveMapAfterTicksDefault);
            Scribe_Values.Look(ref ticksToDiscard, "ticksToDiscard", 0);
            Scribe_Values.Look(ref ageTicks, "ageTicks", 0);
            Scribe_Values.Look(ref angle, "angle", 0f);
            Scribe_Values.Look(ref hasHitRoof, "hasHitRoof", false);
            Scribe_Values.Look(ref hasImpacted, "hasImpacted", false);
            Scribe_Values.Look(ref hasLeftMap, "hasLeftMap", false);
            Scribe_Values.Look(ref anticipationSoundPlayed, "anticipationSoundPlayed", false);
            Scribe_Values.Look(ref hasDepartureHeading, "hasDepartureHeading", false);
            Scribe_Values.Look(ref departureHeading, "departureHeading", 0f);
            Scribe_Values.Look(ref launchCell, "launchCell", IntVec3.Invalid);
            Scribe_Values.Look(ref launchOffset, "launchOffset", Vector3.zero);
            Scribe_Values.Look(ref effectHoldTicks, "effectHoldTicks", int.MaxValue);
            // 舊存檔沒有這個欄位：已在飛的導彈視為出井完成。
            Scribe_Values.Look(ref emergeAge, "emergeAge", int.MaxValue);
            cruise.Expose();
        }
    }
}
