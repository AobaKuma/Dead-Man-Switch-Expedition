using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Fortified;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈世界航跡的共用基底：玩家發射的 <see cref="ScorerProjectile_WorldObject"/>
    /// 與來襲的 <see cref="WorldObject_IncomingMissile"/>。
    ///
    /// 終端更新（逃逸區）：發射時鎖定 <see cref="targetParent"/>；目標換了 tile 時，
    /// 具導引能力的導彈可從當下位置改飛新位置，次數上限由彈體決定
    /// （<see cref="MissileConfig.TerminalUpdates"/>），且改飛後的總航程不得超過射程。
    /// 次數用完或燃料不足時沿最後位置飛行，抵達時目標不在就落空。
    /// </summary>
    public abstract class MissileWorldObject : WorldObject, IBVRTrack, IMissileTrack, IDecoyTrack
    {
        public MissileConfig config;
        public PlanetTile destinationTile = PlanetTile.Invalid;

        /// <summary>發射時鎖定的目標；null 表示打擊固定 tile。</summary>
        public MapParent targetParent;

        protected PlanetTile initialTile = PlanetTile.Invalid;
        protected float traveledPct;
        protected bool arrived;

        // 目前航段；終端更新後從當下位置重新起算
        private bool hasLegStart;
        private Vector3 legStart;
        private float legLength = -1f;   // 本航段消耗的射程（地表格，含跨圖層成本）；-1 = 依起訖 tile 計算
        private float rangeSpent;        // 先前航段已消耗的射程
        private int terminalUpdatesUsed;

        private int ticksUntilTerminalCheck;

        /// <summary>抵達時若目標存在但尚未生成地圖（例：敵方據點），是否生成其地圖。</summary>
        protected abstract bool AllowGenerateTargetMap { get; }

        /// <summary>抵達。<paramref name="map"/> 為 null 表示落空；實作需自行 <see cref="WorldObject.Destroy"/>。</summary>
        protected abstract void OnArrived(Map map);

        public int RouteRevision => terminalUpdatesUsed + manoeuvres;

        // ---- 分導（docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §5.5）----
        /// <summary>所屬分導群組（釋放它的母彈 ID）；-1 = 不屬於任何群組。</summary>
        public int mirvGroupId = -1;
        public MirvRole mirvRole = MirvRole.None;
        /// <summary>子彈沿用母彈的世界速度倍率（RV／誘餌必須與母彈同速，否則 ETA 一眼識破）；≤0 = 依自己的設定。</summary>
        private float inheritedSpeedFactor = -1f;
        /// <summary>子彈沿用母彈的射程上限（終端更新的射程檢查用）；-1 = 依自己的設定。</summary>
        private int inheritedRange = -1;
        /// <summary>誘餌沿用母彈的終端更新次數（沒有導引也要跟著目標改飛，否則立刻露餡）；-1 = 依自己的設定。</summary>
        private int inheritedTerminalUpdates = -1;

        /// <summary>終端更新總次數。</summary>
        public int TerminalUpdatesTotal => inheritedTerminalUpdates >= 0 ? inheritedTerminalUpdates : (config != null ? config.TerminalUpdates : 0);

        /// <summary>實際使用的世界速度倍率。</summary>
        public float EffectiveSpeedFactor => inheritedSpeedFactor > 0f ? inheritedSpeedFactor : (config?.WorldSpeedFactor ?? 1f);
        /// <summary>實際使用的射程上限（0 = 不限）。</summary>
        public int EffectiveRange => inheritedRange >= 0 ? inheritedRange : (config != null ? config.Range : 0);
        private float BaseAngularSpeed => MissileWorldMotion.BaseAngularSpeed * EffectiveSpeedFactor;

        // ---- 飛彈行為（Fortified.MissileBehavior）----
        private int manoeuvres;
        private int childDepth;
        private MissileBehaviorSet behaviors;
        private const int MaxChildDepth = 3;
        /// <summary>有速度剖面時，預測位置與 ETA 的積分步長（ticks）。</summary>
        private const int SpeedStepTicks = 30;

        public MissileBehaviorSet Behaviors
        {
            get
            {
                if (behaviors != null) { return behaviors; }
                if (config == null) { return MissileBehaviorSet.Empty; }
                return behaviors = MissileBehaviorSet.Create(config, this);
            }
        }

        public IMissileLoadout Loadout => config;
        public MapParent TargetParent => targetParent;
        public bool VariableSpeed => Behaviors.HasSpeedProfile;
        public float RouteProgress => RouteProgressAt(CurrentProgress, CurrentLegLength);

        private float RouteProgressAt(float legProgress, float leg)
        {
            float total = rangeSpent + leg;
            return total <= 0f ? 1f : Mathf.Clamp01((rangeSpent + leg * legProgress) / total);
        }

        public void NotifyManoeuvre() => manoeuvres++;

        /// <summary>本航段進度在 <paramref name="ticks"/> 後的值（速度隨航線進度變化時逐段積分）。</summary>
        private float AdvanceProgress(float progress, int ticks)
        {
            float angle = Mathf.Max(0.0000001f, GenMath.SphericalDistance(Start.normalized, End.normalized));
            float leg = CurrentLegLength;
            float baseSpeed = BaseAngularSpeed;
            while (ticks > 0 && progress < 1f)
            {
                int step = Mathf.Min(ticks, SpeedStepTicks);
                progress += baseSpeed * Behaviors.SpeedFactorAt(RouteProgressAt(progress, leg)) / angle * step;
                ticks -= step;
            }
            return Mathf.Clamp01(progress);
        }

        private int VariableTicksToArrival()
        {
            float angle = Mathf.Max(0.0000001f, GenMath.SphericalDistance(Start.normalized, End.normalized));
            float leg = CurrentLegLength;
            float baseSpeed = BaseAngularSpeed;
            float progress = CurrentProgress;
            int ticks = 0;
            while (progress < 1f)
            {
                float rate = baseSpeed * Behaviors.SpeedFactorAt(RouteProgressAt(progress, leg)) / angle;
                float next = progress + rate * SpeedStepTicks;
                if (next >= 1f) { return ticks + Mathf.CeilToInt((1f - progress) / rate); }
                progress = next;
                ticks += SpeedStepTicks;
            }
            return ticks;
        }

        /// <summary>
        /// 從目前位置分出一枚子彈：同目的地、同陣營，接續母彈已消耗的射程，並有自己的攔截與抵達流程。
        /// 超過世代上限回傳 null，避免彈體設定互相遞迴。<paramref name="inheritsRole"/> = 子彈接手母彈的戰果回報（母彈隨後銷毀時用）。
        /// </summary>
        public MissileWorldObject SpawnChild(MissileConfig childConfig, bool inheritsRole = false)
            => SpawnChild(childConfig, MirvRole.None, -1, inheritsRole);

        /// <summary>
        /// 分導釋放：子彈屬於 <paramref name="groupId"/> 群組、扮演 <paramref name="role"/>。
        /// 子彈一律沿用母彈的世界速度與射程上限，終端更新次數也從母彈已用的次數接續。
        /// </summary>
        public MissileWorldObject SpawnChild(MissileConfig childConfig, MirvRole role, int groupId, bool inheritsRole = false,
            MissileBodyDef mimic = null, ThingDef mimicIncomingDef = null)
        {
            if (childConfig == null || !childConfig.Valid || childDepth >= MaxChildDepth) { return null; }
            MissileWorldObject child = (MissileWorldObject)WorldObjectMaker.MakeWorldObject(def);
            child.Tile = Tile;
            child.SetFaction(Faction);
            child.destinationTile = destinationTile;
            child.targetParent = targetParent;
            child.config = childConfig;
            child.childDepth = childDepth + 1;
            float progress = CurrentProgress;
            float leg = CurrentLegLength;
            child.hasLegStart = true;
            child.legStart = Vector3.Slerp(Start, End, progress);
            child.legLength = leg * (1f - progress);
            child.rangeSpent = rangeSpent + leg * progress;
            child.inheritedSpeedFactor = EffectiveSpeedFactor;
            child.inheritedRange = EffectiveRange;
            child.terminalUpdatesUsed = terminalUpdatesUsed;
            child.mirvRole = role;
            child.mirvGroupId = groupId;
            if (childConfig.body?.decoy != null)
            {
                // 誘餌模仿的對象：呼叫端指定（同組彈頭或繼續飛的母彈），否則用誘餌設定的後備外觀。
                child.mimicBody = mimic ?? childConfig.body.decoy.mimicBody;
                child.mimicIncoming = mimicIncomingDef ?? child.mimicBody?.incomingSkyfaller;
                child.inheritedTerminalUpdates = TerminalUpdatesTotal;
            }
            // 狀態沿用：已被偵測的母彈分出的子彈不必重新觸發第一次偵測信件。
            child.State.detectedByFaction = State.detectedByFaction;
            child.State.playerNotified = State.playerNotified;
            child.State.firstDetectedTick = State.firstDetectedTick;
            // 母彈的導引已被干擾機打掉時，繼承同一套導引的子彈也一樣。
            child.State.jamRolled = State.jamRolled;
            child.State.jamDenied = State.jamDenied;
            CopyToChild(child, inheritsRole);
            Find.WorldObjects.Add(child);
            return child;
        }

        /// <summary>子類複製自己的欄位到子彈。</summary>
        protected virtual void CopyToChild(MissileWorldObject child, bool inheritsRole) { }

        /// <summary>目的地 tile；虛擬航跡（空投）會覆寫成自己的目標 tile。</summary>
        public virtual PlanetTile Destination => destinationTile;

        /// <summary>
        /// 玩家是否看得到這條航跡（P4）：自己的彈一定看得到；敵方航跡要被我方雷達偵測到才顯示，
        /// 未偵測的敵方航跡在世界地圖上隱藏、不可選取。
        /// </summary>
        public virtual bool VisibleToPlayer
        {
            get
            {
                Faction player = Faction.OfPlayer;
                if (player == null) { return true; }
                if (Faction == player || Attacker == player) { return true; }
                return State.detectedByFaction == player;
            }
        }

        public override bool SelectableNow => VisibleToPlayer && base.SelectableNow;

        public override void Draw()
        {
            if (VisibleToPlayer) { base.Draw(); }
        }

        public override Texture2D ExpandingIcon => VisibleToPlayer ? DisplayBody?.ExpandingIcon ?? base.ExpandingIcon : null;

        // ---- 誘餌（計畫 §5.6、§7）----
        /// <summary>誘餌模仿的彈體（標籤、圖示）；不是誘餌為 null。</summary>
        public MissileBodyDef mimicBody;
        /// <summary>誘餌模仿的落地彈：雷達特徵（<see cref="TargetProps"/>）與進入地圖時的外觀都取自它。</summary>
        public ThingDef mimicIncoming;

        public bool IsDecoy => config?.body?.decoy != null;
        public int DecoyFidelity => config?.body?.decoy?.fidelity ?? 0;

        /// <summary>玩家的防空已識破這枚誘餌（P4）。</summary>
        public bool DecoyKnownToPlayer => IsDecoy && State.DecoyIdentifiedBy(Faction.OfPlayer);

        /// <summary>
        /// 介面上顯示的彈體：誘餌對攻擊方以外的人顯示它模仿的彈體（P4 識別後才露出原形）。
        /// </summary>
        public MissileBodyDef DisplayBody
        {
            get
            {
                MissileBodyDef body = config?.body;
                if (IsDecoy && mimicBody != null && Attacker != Faction.OfPlayer && !DecoyKnownToPlayer) { return mimicBody; }
                return body;
            }
        }

        /// <summary>落地彈的傷害歸因（發射建築）；只有玩家發射的有。</summary>
        protected virtual Thing LaunchingThing => null;

        public int TerminalUpdatesRemaining
        {
            get
            {
                int total = TerminalUpdatesTotal;
                return Mathf.Max(0, total - terminalUpdatesUsed);
            }
        }

        protected Vector3 Start
        {
            get
            {
                if (hasLegStart) { return legStart; }
                return MissileWorldUtility.TileCenter(initialTile.Valid ? initialTile : Tile);
            }
        }

        protected Vector3 End => MissileWorldUtility.TileCenter(destinationTile);

        /// <summary>
        /// 抵達時的進入方位（docs/Missile_Skyfaller轉Projectile_計畫.md §5）：世界航跡「最後一段」在目標點、指向該段起點的方位，
        /// 落地導彈從這個方向的地圖邊緣飛入。有終端更新時用改飛後的那一段（<see cref="Start"/> 已是改飛點）。
        /// 跨圖層或幾乎同點時沒有水平方位，回傳 null（照原本墜落）。
        /// </summary>
        public float? ArrivalHeading
        {
            get
            {
                if (!destinationTile.Valid) { return null; }
                PlanetTile from = initialTile.Valid ? initialTile : Tile;
                Vector3 start = Start;
                Vector3 end = End;
                if (!from.Valid || !MissileMapEntryUtility.HasHorizontalHeading(from.Layer, destinationTile.Layer, start, end)) { return null; }
                return MissileMapEntryUtility.EntryHeading(destinationTile.Layer, start, end);
            }
        }

        /// <summary>畫在目前飛行高度上；判定一律用貼地的 <see cref="PositionAt"/>。</summary>
        public override Vector3 DrawPos => AirTrackDisplay.Raised(PositionAt(0), AirTrackDisplay.CurrentLift(this));

        public override bool ExpandingIconFlipHorizontal
            => GenWorldUI.WorldToUIPosition(Start).x > GenWorldUI.WorldToUIPosition(End).x;

        public override float ExpandingIconRotation
        {
            get
            {
                if (!def.rotateGraphicWhenTraveling) { return base.ExpandingIconRotation; }
                Vector2 a = GenWorldUI.WorldToUIPosition(Start);
                Vector2 b = GenWorldUI.WorldToUIPosition(End);
                float num = Mathf.Atan2(b.y - a.y, b.x - a.x) * 57.29578f;
                if (num > 180f) { num -= 180f; }
                return num + 90f;
            }
        }

        private int lastMotionTick = -1;
        private BVRTrackState bvrState = new BVRTrackState();
        public WorldObject Self => this;
        public Faction Attacker => Faction;
        public PlanetTile Origin => initialTile;
        public MissileConfig Config => config;
        public BVRTrackState State => bvrState ??= new BVRTrackState();
        public virtual ThingDef IncomingDef => mimicIncoming ?? config?.body?.incomingSkyfaller;
        private BVRTargetProps signatureProps;
        /// <summary>
        /// 落點 skyfaller 的 BVR 屬性；裝了誘餌酬載時雷達截面（distanceLevel）依 <see cref="MissileConfig.SignatureFactor"/> 放大，
        /// 飛行行為也可修改（<see cref="MissileBehavior.ModifySignature"/>）。
        /// </summary>
        public BVRTargetProps TargetProps
        {
            get
            {
                BVRTargetProps baseProps = IncomingDef?.GetModExtension<BVRTargetProps>() ?? BVRTargetProps.Default;
                float factor = config?.SignatureFactor ?? 1f;
                if (Mathf.Approximately(factor, 1f) && Behaviors.Count == 0) { return baseProps; }
                AirTrackSignature signature = baseProps.Signature;
                signature.distanceLevel *= factor;
                Behaviors.ModifySignature(ref signature);
                if (signature.SameAs(baseProps.Signature)) { return baseProps; }
                if (signatureProps == null || !signatureProps.Signature.SameAs(signature))
                {
                    signatureProps = baseProps.WithSignature(signature);
                }
                return signatureProps;
            }
        }
        public virtual float AngularSpeed => VariableSpeed
            ? BaseAngularSpeed * Behaviors.SpeedFactorAt(RouteProgress)
            : BaseAngularSpeed;
        private float TraveledPctStepPerTick => AngularSpeed / Mathf.Max(0.0000001f,
            GenMath.SphericalDistance(Start.normalized, End.normalized));
        private int TicksSinceMotion => lastMotionTick < 0 ? 0 : Mathf.Max(0, Find.TickManager.TicksGame - lastMotionTick);
        private float CurrentProgress => VariableSpeed
            ? AdvanceProgress(traveledPct, TicksSinceMotion)
            : Mathf.Clamp01(traveledPct + TraveledPctStepPerTick * TicksSinceMotion);
        public virtual int TicksToArrival => VariableSpeed
            ? VariableTicksToArrival()
            : Mathf.Max(0, Mathf.CeilToInt((1f - CurrentProgress) / TraveledPctStepPerTick));
        private float ProgressAhead(int ticksAhead) => VariableSpeed
            ? AdvanceProgress(CurrentProgress, Mathf.Max(0, ticksAhead))
            : Mathf.Clamp01(CurrentProgress + Mathf.Max(0, ticksAhead) * TraveledPctStepPerTick);
        public virtual Vector3 PositionAt(int ticksAhead) => Vector3.Slerp(Start, End, ProgressAhead(ticksAhead));

        public float AltitudeAt(int ticksAhead)
        {
            if (ticksAhead <= 0 || Behaviors.Count == 0) { return TargetProps.altitude; }
            float altitude = (IncomingDef?.GetModExtension<BVRTargetProps>() ?? BVRTargetProps.Default).altitude;
            Behaviors.ModifyAltitudeAt(RouteProgressAt(ProgressAhead(ticksAhead), CurrentLegLength), ref altitude);
            return altitude;
        }
        /// <summary>已識破的敵方誘餌畫成灰色；其餘已偵測的敵方航跡畫紅色。</summary>
        public override Color ExpandingIconColor => DecoyKnownToPlayer ? new Color(0.55f, 0.55f, 0.55f, 0.7f)
            : State.detectedByFaction == Faction.OfPlayer ? Color.red : Faction?.Color ?? Color.white;
        public void Notify_Intercepted(Faction by)
        {
            if (Destroyed) return;
            if (by == Faction.OfPlayer || Attacker == Faction.OfPlayer)
                Messages.Message("DMSE.BVR.World.Intercepted".Translate(Label, by?.Name ?? "?"),
                    new GlobalTargetInfo(destinationTile), by == Faction.OfPlayer ? MessageTypeDefOf.PositiveEvent
                        : MessageTypeDefOf.NegativeEvent, historical: false);
            WorldComponent_BVRNetwork.Current?.AddFlash(DrawPos);
            Behaviors.OnIntercepted(by);
            WorldComponent_MirvGroups.RecordWorldOutcome(this, StrikeOutcome.Intercepted);
            OnIntercepted(by);
            Destroy();
        }

        /// <summary>被世界層防空擊落前呼叫（子類記錄戰果用）。</summary>
        protected virtual void OnIntercepted(Faction by) { }

        /// <summary>
        /// 被地面干擾機打掉導引後在地圖外落空：不生成目標地圖、不進 BVR、清掉在飛的攔截彈。
        /// 沒有落到任何地方，所以不加爆炸閃光（docs/電子戰建築_實作計畫.md §3.2）。
        /// </summary>
        public void Notify_JammedOut(Faction by)
        {
            if (Destroyed) return;
            if (by == Faction.OfPlayer || Attacker == Faction.OfPlayer)
                Messages.Message("DMSE.EW.Jammer.JammedOut".Translate(Label, by?.Name ?? "?"),
                    new GlobalTargetInfo(destinationTile), by == Faction.OfPlayer ? MessageTypeDefOf.PositiveEvent
                        : MessageTypeDefOf.NegativeEvent, historical: false);
            foreach (WorldObject_Interceptor interceptor in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>().ToList())
            {
                if (interceptor.target == this && !interceptor.Destroyed) { interceptor.Destroy(); }
            }
            State.interceptor = null;
            Behaviors.OnJammedOut(by);
            WorldComponent_MirvGroups.RecordWorldOutcome(this, StrikeOutcome.Missed);
            OnJammedOut(by);
            Destroy();
        }

        /// <summary>被干擾機在世界層移除前呼叫（子類記錄戰果用）。</summary>
        protected virtual void OnJammedOut(Faction by) { }

        /// <summary>
        /// 抵達時的落點：導引被干擾機打掉就走慣性、不盤旋；否則照導引頭。兩條落地路徑共用，行為才會一致。
        /// </summary>
        protected IntVec3 ArrivalCellFor(Map map, out bool loiter)
        {
            aimFromGroup = false;
            if (State.jamDenied)
            {
                loiter = false;
                return GuidanceType.InertialTarget(map, Faction);
            }
            // 分導彈頭共用一個落點（計畫 §6.3）：第一枚跑導引頭並登記 claim，同組其他彈頭沿用，
            // 不會被導引頭的分散鎖定拆到不同目標。各枚之後仍各自套命中散布。
            MirvGroup group = MirvWarheadGroup;
            if (group != null && group.HasAim(map))
            {
                aimFromGroup = true;
                loiter = group.aimLoiter;
                return group.aimCell;
            }
            IntVec3 cell = GuidanceType.ArrivalCell(config, map, Faction, out loiter, this, ArrivalHeading);
            group?.SetAim(map, cell, loiter);
            return cell;
        }

        /// <summary>上一次 <see cref="ArrivalCellFor"/> 沿用了同組彈頭的落點（子類用來省略重複訊息）。</summary>
        protected bool aimFromGroup;

        /// <summary>這枚是分導群組裡的彈頭時回傳群組；否則 null。</summary>
        public MirvGroup MirvWarheadGroup => mirvRole == MirvRole.Warhead ? WorldComponent_MirvGroups.Get(mirvGroupId) : null;

        /// <summary>打擊任務 id 與波次（玩家波次打擊才有；-1 = 無）。分導群組建立時記下。</summary>
        public virtual int MissionId => -1;
        public virtual int MissionWave => -1;

        private float CurrentLegLength
        {
            get
            {
                if (legLength >= 0f) { return legLength; }
                return MissileWorldUtility.RangeDistance(initialTile.Valid ? initialTile : Tile, destinationTile);
            }
        }

        public override void PostAdd()
        {
            base.PostAdd();
            initialTile = Tile;
            lastMotionTick = Find.TickManager.TicksGame;
            Behaviors.PostLaunch();
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (arrived)
            {
                if (!Destroyed) { TickWhileArrived(); }
                return;
            }

            int now = Find.TickManager.TicksGame;
            int elapsed = lastMotionTick < 0 ? delta : Mathf.Max(0, now - lastMotionTick);
            // Commit elapsed movement before a terminal update starts a new leg.
            traveledPct = VariableSpeed
                ? AdvanceProgress(traveledPct, elapsed)
                : Mathf.Clamp01(traveledPct + TraveledPctStepPerTick * elapsed);
            lastMotionTick = now;
            Behaviors.FlightTick(elapsed);
            if (Destroyed) { return; }
            ticksUntilTerminalCheck -= elapsed;
            if (ticksUntilTerminalCheck <= 0)
            {
                // 反艦導引頭追蹤移動目標，檢查得更勤（20 ticks），一般導引頭維持 60 ticks。
                ticksUntilTerminalCheck = config != null && config.TracksMobileTargets
                    ? MissileWorldUtility.MobileTerminalCheckIntervalTicks
                    : MissileWorldUtility.TerminalCheckIntervalTicks;
                TryTerminalUpdate();
            }

            if (traveledPct >= 1f)
            {
                traveledPct = 1f;
                arrived = true;
                // 受襲通報（NPC 防空警戒）：抵達目標據點即通報，不論之後是命中地圖還是壓制抽象節點。
                MapParent site = WorldComponent_FactionAirDefenseAlert.TargetSite(this);
                WorldComponent_FactionAirDefenseAlert.Current?.ReportHit(this, site);
                AirDefenseHooks.WorldEvent(AirCombatEventKind.TrackArrived, this, site?.Faction, site);
                if (IsDecoy)
                {
                    ResolveDecoyArrival();
                    if (!Destroyed) { Destroy(); }
                    return;
                }
                if (Behaviors.PreArrive())
                {
                    if (!Destroyed) { Destroy(); }
                    return;
                }
                if (TryStrikeAbstractNode())
                {
                    // 打掉抽象防空節點：對分導群組算抵達並命中。
                    WorldComponent_MirvGroups.RecordWorldOutcome(this, StrikeOutcome.Arrived);
                    if (mirvRole == MirvRole.Warhead) { WorldComponent_MirvGroups.RecordMapOutcome(mirvGroupId, StrikeOutcome.Hit); }
                }
                else
                {
                    Map map = MissileWorldUtility.ResolveArrivalMap(destinationTile, targetParent, AllowGenerateTargetMap);
                    WorldComponent_MirvGroups.RecordWorldOutcome(this, map == null ? StrikeOutcome.Missed : StrikeOutcome.Arrived);
                    OnArrived(map);
                }
            }
        }

        /// <summary>
        /// 誘餌抵達（計畫 §7.2）：絕不生成目標地圖。地圖不存在就無聲消失；有地圖時依 <see cref="DecoyProps.reentrySurvivalChance"/>
        /// 在大氣層燒毀，或以模仿對象的落地彈（惰性）進入地圖層 BVR，落在同組彈頭的落點附近。
        /// </summary>
        private void ResolveDecoyArrival()
        {
            DecoyProps decoy = config.body.decoy;
            MapParent parent = targetParent != null && !targetParent.Destroyed ? targetParent : Find.WorldObjects.MapParentAt(destinationTile);
            Map map = parent != null && parent.HasMap ? parent.Map : null;
            if (map == null || !Rand.Chance(decoy.reentrySurvivalChance))
            {
                if (Prefs.DevMode) { Log.Message($"[DMSE MIRV] 誘餌 #{ID} " + (map == null ? "抵達時目標沒有地圖，消失" : "在大氣層燒毀")); }
                return;
            }
            ThingDef def = mimicIncoming ?? IncomingDef;
            if (def == null) { return; }
            MirvGroup group = WorldComponent_MirvGroups.Get(mirvGroupId);
            IntVec3 cell = group != null && group.HasAim(map) ? group.aimCell : GuidanceType.InertialTarget(map, Faction);
            Thing thing = MissileProjectileMaker.SpawnIncoming(def, cell, map, ArrivalHeading);
            if (!(thing is IIncomingMissile incoming)) { return; }
            incoming.Config = config.Clone();
            incoming.WorldPhaseDone = true;
            incoming.Attacker = Faction;
            incoming.MirvGroupId = -1; // 誘餌的地圖層結果不算彈頭戰果
            if (thing is Projectile_MissileIncoming p)
            {
                p.SetLauncher(LaunchingThing); // 原版護盾以 launcher 判斷敵我
                if (!State.decoyIdentifiedFactions.NullOrEmpty())
                {
                    p.decoyIdentifiedFactions = new System.Collections.Generic.List<int>(State.decoyIdentifiedFactions);
                }
                p.ApplyLandingScatter();
                cell = p.Position;
            }
            MissileBVRUtility.TryRegister(thing, cell);
            if (Prefs.DevMode) { Log.Message($"[DMSE MIRV] 誘餌 #{ID} 以 {def.defName} 進入地圖層，落點 {cell}"); }
        }

        /// <summary>
        /// 已抵達但還沒銷毀時每次 tick 呼叫（例：分導彈頭等同組的電視選點）。子類負責最後 <see cref="WorldObject.Destroy"/>。
        /// </summary>
        protected virtual void TickWhileArrived() { }

        /// <summary>目標換了 tile 時嘗試改飛；不符合條件就維持原航線（之後落空）。</summary>
        private void TryTerminalUpdate()
        {
            if (targetParent == null || targetParent.Destroyed) { return; }

            PlanetTile targetTile = targetParent.Tile;
            if (!targetTile.Valid || targetTile == destinationTile) { return; }
            if (TerminalUpdatesRemaining <= 0 || State.jamDenied) { return; }

            Vector3 here = Vector3.Slerp(Start, End, traveledPct);
            float spent = rangeSpent + CurrentLegLength * traveledPct;
            float newLeg = MissileWorldUtility.SurfaceTilesBetween(here, MissileWorldUtility.TileCenter(targetTile));
            if (!MissileWorldUtility.SameLayer(destinationTile, targetTile))
            {
                newLeg += MissileWorldUtility.CrossLayerRangeCost;
            }
            int range = EffectiveRange;
            if (range > 0 && spent + newLeg > range) { return; }

            rangeSpent = spent;
            legStart = here;
            hasLegStart = true;
            legLength = newLeg;
            destinationTile = targetTile;
            traveledPct = 0f;
            terminalUpdatesUsed++;
            Behaviors.PostTerminalUpdate();

            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE Missile] 終端更新 → {targetTile}（剩餘 {TerminalUpdatesRemaining} 次，已用射程 {spent:0.#}/{(range > 0 ? range.ToString() : "∞")}）");
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if ((config != null && config.HasTerminalGuidance) || inheritedTerminalUpdates > 0)
            {
                if (!text.NullOrEmpty()) { text += "\n"; }
                text += "DMSE.Missile.TerminalUpdatesRemaining".Translate(TerminalUpdatesRemaining, TerminalUpdatesTotal);
            }
            if (!text.NullOrEmpty()) text += "\n";
            text += "DMSE.BVR.World.MissileType".Translate(DisplayBody?.LabelCap ?? Label) + "\n";
            string destination = targetParent?.Label ?? Find.WorldObjects.MapParentAt(destinationTile)?.Label;
            text += "DMSE.BVR.World.TrackInspect".Translate(destination ?? destinationTile.ToString(),
                TicksToArrival.ToStringTicksToPeriod(), State.StatusKey.Translate(), State.interceptAttempts);
            text += "\n" + "DMSE.BVR.World.TrackAltitude".Translate(MissileWorldMotion.AltitudeLabel(AltitudeAt(0)));
            // 分導資訊只給攻擊方看：防守方不該從面板得知哪條是彈頭、何時分導。
            if (Attacker == Faction.OfPlayer || Prefs.DevMode)
            {
                if (mirvRole != MirvRole.None)
                {
                    text += "\n" + ("DMSE.Mirv.Role." + mirvRole).Translate(mirvGroupId);
                }
                string release = Behaviors.Get<MissileBehavior_ReleaseChildren>()?.InspectLine();
                if (!release.NullOrEmpty()) { text += "\n" + release; }
            }
            return text;
        }

        /// <summary>
        /// P4：反輻射彈命中「尚未生成地圖的抽象防空節點」時，直接削弱該節點（打掉庫存、壓制雷達一天），
        /// 不生成目標地圖。回傳 true 表示已在此結算，呼叫端不再走一般抵達流程。
        /// </summary>
        protected virtual bool TryStrikeAbstractNode()
        {
            if (config?.TargetingGuidance?.HomesOnEmissions != true) { return false; }
            MapParent parent = targetParent ?? Find.WorldObjects.MapParentAt(destinationTile);
            if (parent == null || parent.Destroyed) { return false; }
            int now = Find.TickManager.TicksGame;

            WorldObjectComp_AirDefense comp = parent.GetComponent<WorldObjectComp_AirDefense>();
            if (comp != null && comp.Available)
            {
                comp.Degrade(now);
                ReportAbstractStrike(parent);
                return true;
            }
            IBVRDefense synthetic = WorldComponent_SettlementDefense.Current?.DefenseFor(parent);
            if (synthetic != null && synthetic.Available)
            {
                synthetic.Degrade(now);
                ReportAbstractStrike(parent);
                return true;
            }
            return false;
        }

        private void ReportAbstractStrike(MapParent parent)
        {
            if (Attacker == Faction.OfPlayer || Faction == Faction.OfPlayer)
            {
                Messages.Message("DMSE.BVR.World.AbstractStrike".Translate(Label, parent.Label),
                    new GlobalTargetInfo(parent), MessageTypeDefOf.PositiveEvent, historical: false);
            }
            WorldComponent_BVRNetwork.Current?.AddFlash(PositionAt(0));
            Destroy();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref destinationTile, "destinationTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref initialTile, "initialTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref traveledPct, "traveledPct", 0f);
            Scribe_Values.Look(ref arrived, "arrived", false);
            Scribe_Values.Look(ref lastMotionTick, "lastMotionTick", -1);
            Scribe_Deep.Look(ref bvrState, "bvrState");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                bvrState ??= new BVRTrackState();
                if (lastMotionTick < 0) lastMotionTick = Find.TickManager.TicksGame;
            }
            Scribe_Deep.Look(ref config, "config");
            Scribe_References.Look(ref targetParent, "targetParent");
            Scribe_Values.Look(ref hasLegStart, "hasLegStart", false);
            Scribe_Values.Look(ref legStart, "legStart");
            Scribe_Values.Look(ref legLength, "legLength", -1f);
            Scribe_Values.Look(ref rangeSpent, "rangeSpent", 0f);
            Scribe_Values.Look(ref terminalUpdatesUsed, "terminalUpdatesUsed", 0);
            Scribe_Values.Look(ref manoeuvres, "manoeuvres", 0);
            Scribe_Values.Look(ref childDepth, "childDepth", 0);
            Scribe_Values.Look(ref mirvGroupId, "mirvGroupId", -1);
            Scribe_Values.Look(ref mirvRole, "mirvRole", MirvRole.None);
            Scribe_Values.Look(ref inheritedSpeedFactor, "inheritedSpeedFactor", -1f);
            Scribe_Values.Look(ref inheritedRange, "inheritedRange", -1);
            Scribe_Values.Look(ref inheritedTerminalUpdates, "inheritedTerminalUpdates", -1);
            Scribe_Defs.Look(ref mimicBody, "mimicBody");
            Scribe_Defs.Look(ref mimicIncoming, "mimicIncoming");
            if (Scribe.EnterNode("behaviors"))
            {
                try { Behaviors.ExposeData(); }
                finally { Scribe.ExitNode(); }
            }
        }
    }
}

namespace DMSE
{
    /// <summary>分導群組成員的角色（docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §5.5）。</summary>
    public enum MirvRole : byte
    {
        None,
        /// <summary>真彈頭（重返載具，或只放誘餌時繼續飛的母彈）。</summary>
        Warhead,
        /// <summary>誘餌：外觀與彈頭相同、無殺傷。</summary>
        Decoy
    }
}
