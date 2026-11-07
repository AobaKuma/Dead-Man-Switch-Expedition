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
    public abstract class MissileWorldObject : WorldObject, IBVRTrack, IMissileTrack
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
            float baseSpeed = MissileWorldMotion.AngularSpeed(config);
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
            float baseSpeed = MissileWorldMotion.AngularSpeed(config);
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

        public override Texture2D ExpandingIcon => VisibleToPlayer ? config?.body?.ExpandingIcon ?? base.ExpandingIcon : null;

        public int TerminalUpdatesRemaining
        {
            get
            {
                int total = config != null ? config.TerminalUpdates : 0;
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
        public virtual ThingDef IncomingDef => config?.body?.incomingSkyfaller;
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
            ? MissileWorldMotion.AngularSpeed(config) * Behaviors.SpeedFactorAt(RouteProgress)
            : MissileWorldMotion.AngularSpeed(config);
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
        public override Color ExpandingIconColor => State.detectedByFaction == Faction.OfPlayer
            ? Color.red : Faction?.Color ?? Color.white;
        public void Notify_Intercepted(Faction by)
        {
            if (Destroyed) return;
            if (by == Faction.OfPlayer || Attacker == Faction.OfPlayer)
                Messages.Message("DMSE.BVR.World.Intercepted".Translate(Label, by?.Name ?? "?"),
                    new GlobalTargetInfo(destinationTile), by == Faction.OfPlayer ? MessageTypeDefOf.PositiveEvent
                        : MessageTypeDefOf.NegativeEvent, historical: false);
            WorldComponent_BVRNetwork.Current?.AddFlash(DrawPos);
            Behaviors.OnIntercepted(by);
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
            if (State.jamDenied)
            {
                loiter = false;
                return GuidanceType.InertialTarget(map, Faction);
            }
            return GuidanceType.ArrivalCell(config, map, Faction, out loiter, this, ArrivalHeading);
        }

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
            if (arrived) { return; }

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
                if (Behaviors.PreArrive())
                {
                    if (!Destroyed) { Destroy(); }
                    return;
                }
                if (!TryStrikeAbstractNode())
                {
                    OnArrived(MissileWorldUtility.ResolveArrivalMap(destinationTile, targetParent, AllowGenerateTargetMap));
                }
            }
        }

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
            int range = config != null ? config.Range : 0;
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
            if (config != null && config.HasTerminalGuidance)
            {
                if (!text.NullOrEmpty()) { text += "\n"; }
                text += "DMSE.Missile.TerminalUpdatesRemaining".Translate(TerminalUpdatesRemaining, config.TerminalUpdates);
            }
            if (!text.NullOrEmpty()) text += "\n";
            text += "DMSE.BVR.World.MissileType".Translate(config?.body?.LabelCap ?? Label) + "\n";
            string destination = targetParent?.Label ?? Find.WorldObjects.MapParentAt(destinationTile)?.Label;
            text += "DMSE.BVR.World.TrackInspect".Translate(destination ?? destinationTile.ToString(),
                TicksToArrival.ToStringTicksToPeriod(), State.StatusKey.Translate(), State.interceptAttempts);
            text += "\n" + "DMSE.BVR.World.TrackAltitude".Translate(MissileWorldMotion.AltitudeLabel(AltitudeAt(0)));
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
            if (Scribe.EnterNode("behaviors"))
            {
                try { Behaviors.ExposeData(); }
                finally { Scribe.ExitNode(); }
            }
        }
    }
}
