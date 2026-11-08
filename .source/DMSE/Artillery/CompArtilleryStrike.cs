using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    // =========================================================================
    //  CompProperties_ArtilleryStrike
    // =========================================================================

    public class CompProperties_ArtilleryStrike : CompProperties
    {
        /// <summary>
        /// 離場投射物 ThingDef（thingClass 為 DMSE.Projectile_ArtilleryDeparture）。
        /// 發射後從建築位置沿弧線飛向地圖邊緣，抵達時建立世界地圖航跡物件。
        /// </summary>
        public ThingDef departureProjectileDef;

        /// <summary>世界地圖航跡物件 def（worldObjectClass 為 DMSE.WorldObject_ArtilleryStrike）。</summary>
        public WorldObjectDef worldObjectDef;

        /// <summary>
        /// 落地 Projectile ThingDef（後備；優先讀取 CompChangeableProjectile 已裝填的砲彈）。
        /// </summary>
        public ThingDef projectileDef;

        /// <summary>
        /// 每次開火後的裝填冷卻（tick）。預設 3600（60 秒）。
        /// 對應原版砲塔的 <see cref="BuildingProperties.turretBurstCooldownTime"/>。
        /// </summary>
        public int cooldownTicks = 3600;

        /// <summary>
        /// 每次開火前的瞄準時間（tick）。預設 240（4 秒，同原版迫擊砲 warmupTime=4）。
        /// 冷卻結束後開始計時；結束後才真正發射。
        /// </summary>
        public int warmupTicks = 240;

        /// <summary>以目標格為圓心的散佈半徑（格數）。0 = 完全精準。</summary>
        public float scatterRadius = 2f;

        /// <summary>齊射發數（每次開火生成的 WorldObject 數量）。</summary>
        public int salvoCount = 1;

        /// <summary>齊射各發間隔（tick）。</summary>
        public int salvoIntervalTicks = 60;

        /// <summary>是否需要同陣營的火控雷達佔用一個空閒通道方能開火。</summary>
        public bool requiresFireControlChannel = false;

        /// <summary>開火後佔用火力通道的時間（tick）。</summary>
        public int fireControlHoldTicks = 900;

        /// <summary>
        /// 雷達導引的散佈半徑（格）；&lt; 0 = 不支援雷達導引（預設）。
        /// 支援時，開火瞬間若同陣營有空閒砲擊引導通道（supportsArtilleryGuidance 的火控雷達），
        /// 就佔用一條：這一發改用此散佈半徑，抵達時自動指向目標地圖上的敵方目標（無則地圖中心）。
        /// 沒有通道時照常以 <see cref="scatterRadius"/> 無導引射擊（除非 <see cref="requiresFireControlChannel"/>）。
        /// </summary>
        public float guidedScatterRadius = -1f;

        /// <summary>世界航跡速度：每跨一格地表等效距離需要的 tick（導彈基準為 BVRWorldTuningDef.ticksPerTileAtSpeed1 = 150）。</summary>
        public float worldTicksPerTile = 60f;

        /// <summary>砲身與瞄準方位夾角在此度數內才開火（有 CompTurretFacingRenderer 時）；瞄準時間到了還沒轉到就繼續等。</summary>
        public float aimToleranceDeg = 2f;

        /// <summary>最大射程（世界地表等效格）；0 = 不限。</summary>
        public float maxRangeTiles = 0f;

        /// <summary>CompRefuelable 是砲彈存量而非砲管耐久：用盡時顯示「無彈」而不是「砲管耗損」。</summary>
        public bool refuelableIsAmmo = false;

        public bool SupportsRadarGuidance => guidedScatterRadius >= 0f;

        public CompProperties_ArtilleryStrike()
        {
            compClass = typeof(CompArtilleryStrike);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }
            if (departureProjectileDef == null)
            {
                yield return "departureProjectileDef is null.";
            }
            if (worldObjectDef == null)
            {
                yield return "worldObjectDef is null.";
            }
        }
    }

    // =========================================================================
    //  CompArtilleryStrike
    // =========================================================================

    /// <summary>
    /// 超視距火砲建築的核心元件，繼承 <see cref="CompBVRDevice"/> 以共用
    /// 電力 / 損壞 / 開關的可用性判定。
    ///
    /// 持續射擊工作流程：
    ///   1. 玩家點擊「開火」Gizmo → 切換至世界地圖，選擇目標 Tile。
    ///   2. 進入持續射擊狀態（targetTile 設為選定 tile）。
    ///   3. 每個 tick 推進射擊狀態機：
    ///      a. 若在裝填冷卻中 → 等待；
    ///      b. 冷卻結束後開始瞄準計時（warmupTicks）；
    ///      c. 瞄準完畢 → 發射 Projectile_ArtilleryDeparture；
    ///      d. 設置冷卻計時 → 回到 (a) 循環。
    ///   4. 玩家點擊「停止」Gizmo 或裝備無法運作（無彈、無砲管）→ 結束持續射擊。
    /// </summary>
    public class CompArtilleryStrike : CompBVRDevice
    {
        public CompProperties_ArtilleryStrike Props =>
            (CompProperties_ArtilleryStrike)props;

        /// <summary>效力射每一發的收束比例（相對於 scatterRadius）：每發再減 25%。</summary>
        public const float EffectScatterStep = 0.25f;

        /// <summary>效力射散佈下限（格）：收束到這裡就不再縮小。</summary>
        public const float EffectScatterFloor = 1f;

        // ---- 冷卻 ----
        private int cooldownUntil;

        // ---- 射擊模式 ----
        /// <summary>false = 校射（散佈於目標地圖中心）；true = 效力射（散佈於該地圖最後一次校射的落點）。</summary>
        private bool fireForEffect;

        /// <summary>本次持續射擊已打出的效力射發數（每發收束一次；停止／換目標／切模式重置）。</summary>
        private int effectShotsFired;

        /// <summary>「等砲手補彈」訊息的下次可顯示 tick（避免每 tick 洗頻；不存檔）。</summary>
        private int nextNoShellMessageTick;

        // ---- 持續射擊狀態 ----
        /// <summary>目標世界 tile（含圖層）；Invalid = 閒置（未在持續射擊）。</summary>
        private PlanetTile targetTile = PlanetTile.Invalid;

        /// <summary>
        /// 瞄準結束的 tick；-1 = 尚未開始瞄準（冷卻中或剛重置）。
        /// 冷卻結束後才開始計時；不隨存檔保留，載入後自動重新瞄準。
        /// </summary>
        private int warmupEndTick = -1;

        /// <summary>
        /// 瞄準中的砲管方位角（0=North/+Z，順時針）；NaN = 不轉向（目標與本地同一 tile）。
        /// 瞄準開始時計算，與 <see cref="warmupEndTick"/> 一樣不存檔。
        /// </summary>
        private float aimAngle = float.NaN;

        /// <summary>是否在持續射擊狀態。</summary>
        public bool IsSustainedFiring => targetTile.Valid;

        // =====================================================================
        //  射擊模式（校射／效力射）
        // =====================================================================

        /// <summary>是否為效力射模式（false = 校射）。</summary>
        public bool FireForEffect => fireForEffect;

        /// <summary>
        /// 效力射這一發的散佈半徑：從 <see cref="CompProperties_ArtilleryStrike.scatterRadius"/> 起算，
        /// 每發再減 <see cref="EffectScatterStep"/>（25%），最低 <see cref="EffectScatterFloor"/> 格。
        /// Def 的散佈 ≤ 0 時維持 0（精準武器不會被下限拉大）。
        /// </summary>
        public float CurrentEffectScatterRadius =>
            Props.scatterRadius <= 0f
                ? 0f
                : Mathf.Max(EffectScatterFloor, Props.scatterRadius * (1f - EffectScatterStep * effectShotsFired));

        /// <summary>
        /// 切換射擊模式：收束進度歸零。切到效力射但目標地圖還沒校射過時提示一次
        /// （狀態機會停在待命，不會誤打地圖中心）。
        /// </summary>
        public void SetFireForEffect(bool value)
        {
            if (fireForEffect == value) { return; }
            fireForEffect    = value;
            effectShotsFired = 0;
            if (value && IsSustainedFiring && !TryGetRegisteredImpact(targetTile, out _))
            {
                Messages.Message("DMSE.Artillery.NoRegistration".Translate(),
                    parent, MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        /// <summary>目標 tile 所在地圖上，本陣營最後一次校射的落點；沒有校射過或地圖不存在時 false。</summary>
        private bool TryGetRegisteredImpact(PlanetTile tile, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            Map map = Current.Game.FindMap(tile);
            MapComponent_ArtilleryStrikes comp = map?.GetComponent<MapComponent_ArtilleryStrikes>();
            return comp != null && comp.TryGetRegisteredImpact(parent.Faction, out cell, out _);
        }

        // ---- 火力通道 ----
        private CompFireControlRadar reservedRadar;
        private Thing reservedRadarThing; // 序列化中繼
        private int channelReleaseTick = -1;

        // =====================================================================
        //  存檔
        // =====================================================================

        public override void PostExposeData()
        {
            base.PostExposeData();

            Scribe_Values.Look(ref cooldownUntil,      "artilleryCooldownUntil", 0);
            Scribe_Values.Look(ref targetTile,          "artilleryTargetTile",    PlanetTile.Invalid);
            if (Scribe.mode == LoadSaveMode.LoadingVars && !targetTile.Valid)
            {
                // 舊存檔只存 tileId（圖層遺失），只能視為地表
                int legacyTileId = -1;
                Scribe_Values.Look(ref legacyTileId, "artilleryTargetTileId", -1);
                if (legacyTileId >= 0) { targetTile = new PlanetTile(legacyTileId); }
            }
            Scribe_Values.Look(ref channelReleaseTick,  "channelReleaseTick",     -1);
            Scribe_Values.Look(ref fireForEffect,       "artilleryFireForEffect", false);
            Scribe_Values.Look(ref effectShotsFired,    "artilleryEffectShots",   0);
            // warmupEndTick 不序列化：載入後自動從冷卻結束時重新開始瞄準

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                reservedRadarThing = reservedRadar?.parent;
            }
            Scribe_References.Look(ref reservedRadarThing, "reservedRadarThing");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                reservedRadar      = reservedRadarThing?.TryGetComp<CompFireControlRadar>();
                reservedRadarThing = null;
                warmupEndTick      = -1; // 載入後從冷卻結束再重新瞄準
            }
        }

        // =====================================================================
        //  Tick
        // =====================================================================

        public override void CompTick()
        {
            base.CompTick();

            // 火力通道釋放
            if (reservedRadar != null && Find.TickManager.TicksGame >= channelReleaseTick)
            {
                reservedRadar.ReleaseArtilleryChannel();
                reservedRadar      = null;
                channelReleaseTick = -1;
            }

            TickSustainedFire();
        }

        /// <summary>
        /// 持續射擊狀態機。每 tick 驅動：等待冷卻 → 瞄準 → 開火 → 等待冷卻 → …
        /// </summary>
        private void TickSustainedFire()
        {
            if (!IsSustainedFiring) { return; }

            int now = Find.TickManager.TicksGame;

            // 裝置失效（無電、損壞、開關關閉）→ 暫停，保留目標等恢復後繼續
            if (!Active)
            {
                warmupEndTick = -1;
                return;
            }

            // 裝填冷卻中 → 重置瞄準計時，砲管維持在上一發的方位，等待冷卻結束
            if (cooldownUntil > now)
            {
                warmupEndTick = -1;
                SetTurretRotation(aimAngle);
                return;
            }

            // 確認彈藥
            ThingDef projDef = ResolveProjectileDef();
            if (projDef == null)
            {
                // 可換彈藥的砲（原版迫擊砲）：砲手補彈要離開砲位再回來，任務暫停等他，不要取消
                if (ChangeableProjectile != null)
                {
                    warmupEndTick = -1;
                    if (now >= nextNoShellMessageTick)
                    {
                        Messages.Message("DMSE.Artillery.WaitingForShell".Translate(),
                            parent, MessageTypeDefOf.NeutralEvent, historical: false);
                        nextNoShellMessageTick = now + 600;
                    }
                    return;
                }
                Messages.Message("DMSE.Artillery.NoAmmo".Translate(),
                    parent, MessageTypeDefOf.NegativeEvent, historical: false);
                StopSustainedFire();
                return;
            }

            // 確認砲管耐久（CompRefuelable，fuelIsMortarBarrel=true）或砲彈存量（refuelableIsAmmo）
            CompRefuelable barrel = parent.GetComp<CompRefuelable>();
            if (barrel != null && !barrel.HasFuel)
            {
                // 有砲手的裝置：砲手會去拿強化砲管換上，等他就位就好
                if (parent.GetComp<CompMannable>() != null)
                {
                    warmupEndTick = -1;
                    return;
                }
                Messages.Message((Props.refuelableIsAmmo ? "DMSE.Artillery.NoAmmo" : "DMSE.Artillery.NoBarrel").Translate(),
                    parent, MessageTypeDefOf.NegativeEvent, historical: false);
                StopSustainedFire();
                return;
            }

            // 火力通道確認（若需要）
            if (Props.requiresFireControlChannel && FindAvailableRadar() == null)
            {
                // 無可用通道時暫停瞄準，等通道釋放後自動恢復
                warmupEndTick = -1;
                return;
            }

            // 效力射需要目標地圖上已有校射落點（選靶時已擋；這裡是「射擊中切到效力射」的保險）
            if (fireForEffect && !TryGetRegisteredImpact(targetTile, out _))
            {
                warmupEndTick = -1;
                return;
            }

            // 開始瞄準（冷卻剛結束時，warmupEndTick 為 -1）
            if (warmupEndTick < 0)
            {
                warmupEndTick = now + Props.warmupTicks;

                // 砲管轉向目標世界方位（瞄準開始時，讓玩家有 4 秒看到砲管旋轉）
                PlanetTile srcTile = parent.Map?.Tile ?? PlanetTile.Invalid;
                aimAngle = srcTile.Valid && srcTile != targetTile
                    ? ArtilleryStrikeUtility.WorldDirectionAngle(srcTile, targetTile)
                    : float.NaN;
                SetTurretRotation(aimAngle);
                return;
            }

            // 瞄準進行中：原版 TurretTop 沒有本地目標時會閒置擺動，每 tick 拉回瞄準方位。
            // 砲身轉速有限（CompTurretFacingRenderer），瞄準時間到了還沒轉到方位就繼續等，砲彈才會沿砲口方向出膛。
            if (now < warmupEndTick || !BarrelOnBearing)
            {
                SetTurretRotation(aimAngle);
                return;
            }

            // 瞄準完畢 → 開火
            warmupEndTick = -1;
            FireSingleShot(targetTile);
        }

        // =====================================================================
        //  停止持續射擊
        // =====================================================================

        public void StopSustainedFire()
        {
            targetTile       = PlanetTile.Invalid;
            warmupEndTick    = -1;
            effectShotsFired = 0;
        }

        // =====================================================================
        //  檢視字串
        // =====================================================================

        /// <summary>
        /// 現在開火的話，這一發是否有雷達導引（支援導引且同陣營有空閒砲擊引導通道）。
        /// 效力射優先：效力射不使用導引，這裡一律視為不可用。
        /// </summary>
        public bool RadarGuidanceAvailable =>
            !fireForEffect && Props.SupportsRadarGuidance && FindAvailableRadar() != null;

        public override string CompInspectStringExtra()
        {
            if (fireForEffect)
            {
                string line = "DMSE.Artillery.ModeEffect".Translate();
                if (IsSustainedFiring && TryGetRegisteredImpact(targetTile, out IntVec3 cell))
                {
                    line += "\n" + "DMSE.Artillery.EffectPoint".Translate(
                        cell.x, cell.z, CurrentEffectScatterRadius.ToString("0.#"));
                }
                return line;
            }
            if (!Props.SupportsRadarGuidance) { return null; }
            string guidance = RadarGuidanceAvailable
                ? "DMSE.Artillery.GuidanceOnline".Translate(Props.guidedScatterRadius.ToString("0.#"))
                : "DMSE.Artillery.GuidanceOffline".Translate(Props.scatterRadius.ToString("0.#"));
            // 射擊中才補模式（閒置時維持原本的導引字串，既有驗收的精確比對不受影響）
            return IsSustainedFiring ? guidance + "\n" + "DMSE.Artillery.ModeRanging".Translate() : guidance;
        }

        // =====================================================================
        //  Gizmo
        // =====================================================================

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer) { yield break; }
            if (IsSustainedFiring)
            {
                // ---- 停止按鈕（含狀態說明）----
                yield return new Command_Action
                {
                    defaultLabel = "DMSE.Artillery.Stop".Translate(),
                    defaultDesc  = BuildStopDesc(),
                    icon         = TexCommand.ClearPrioritizedWork,
                    action       = StopSustainedFire,
                };
            }
            else
            {
                // ---- 開火按鈕（選世界目標）----
                Command_Action cmd = new Command_Action
                {
                    defaultLabel = "DMSE.Artillery.Fire".Translate(),
                    defaultDesc  = "DMSE.Artillery.FireDesc".Translate(),
                    icon         = CompLaunchable.LaunchCommandTex,
                    action       = BeginWorldTargeting,
                };

                if (!CanFireGizmo(out string reason))
                {
                    cmd.Disable(reason);
                }

                yield return cmd;
            }

            // ---- 射擊模式切換（校射／效力射）----
            yield return new Command_Toggle
            {
                defaultLabel = fireForEffect
                    ? "DMSE.Artillery.ModeEffect".Translate()
                    : "DMSE.Artillery.ModeRanging".Translate(),
                defaultDesc  = "DMSE.Artillery.ModeDesc".Translate(),
                icon         = ContentFinder<Texture2D>.Get("UI/Commands/FireAtWill"),
                isActive     = () => fireForEffect,
                toggleAction = () => SetFireForEffect(!fireForEffect),
                alsoClickIfOtherInGroupClicked = false,
            };
        }

        private string BuildStopDesc()
        {
            int now = Find.TickManager.TicksGame;
            string stateStr;

            if (!Active)
            {
                stateStr = "DMSE.BVR.DeviceInactive".Translate();
            }
            else if (cooldownUntil > now)
            {
                stateStr = "DMSE.Artillery.StateCooldown".Translate(
                    (cooldownUntil - now).ToStringTicksToPeriod());
            }
            else if (warmupEndTick > now)
            {
                int pct = Mathf.RoundToInt(
                    (1f - (float)(warmupEndTick - now) / Mathf.Max(1, Props.warmupTicks)) * 100f);
                stateStr = "DMSE.Artillery.StateAiming".Translate(pct);
            }
            else
            {
                stateStr = "DMSE.Artillery.StateReady".Translate();
            }

            return "DMSE.Artillery.StopDesc".Translate(stateStr);
        }

        // =====================================================================
        //  開火前判定（Gizmo 用）
        //  注意：不檢查冷卻，允許在冷卻中選定目標並排程持續射擊。
        // =====================================================================

        private const string NotConfiguredReason = "Artillery not configured (missing def reference in XML).";

        private bool CanFireGizmo(out string reason)
        {
            if (!Active)
            {
                // 需要砲手的裝置（原版迫擊砲）：先告訴玩家是沒人操作，而不是籠統的「裝置未運作」
                CompMannable mannable = parent.GetComp<CompMannable>();
                reason = mannable != null && !mannable.MannedNow
                    ? "DMSE.Artillery.NoCrew".Translate()
                    : "DMSE.BVR.DeviceInactive".Translate();
                return false;
            }
            // 設定錯誤只會出現在開發階段（載入時已由 ConfigErrors 回報），不翻譯
            if (Props.departureProjectileDef == null || Props.worldObjectDef == null)
            {
                reason = NotConfiguredReason;
                return false;
            }
            if (ResolveProjectileDef() == null)
            {
                reason = ChangeableProjectile != null
                    ? "DMSE.Artillery.NoLoadedShell".Translate().ToString()
                    : NotConfiguredReason;
                return false;
            }
            // 沒砲彈時不給選靶：否則選完下一 tick 就因無彈停火，本地圖武器隨即接手轉向本地敵人，看起來像沒打出去
            CompRefuelable barrel = parent.GetComp<CompRefuelable>();
            if (barrel != null && !barrel.HasFuel)
            {
                reason = !barrel.Props.outOfFuelMessage.NullOrEmpty()
                    ? barrel.Props.outOfFuelMessage
                    : (Props.refuelableIsAmmo ? "DMSE.Artillery.NoAmmo" : "DMSE.Artillery.NoBarrel").Translate().ToString();
                return false;
            }
            reason = null;
            return true;
        }

        // =====================================================================
        //  砲彈 def 解析（優先 CompChangeableProjectile）
        // =====================================================================

        /// <summary>
        /// 原版可換彈藥 Comp（迫擊砲）：先找建築本身，再找原版砲塔（<see cref="Building_TurretGun"/>）的 gun。
        /// 原版迫擊砲把 <see cref="CompChangeableProjectile"/> 掛在 gun 上，砲手載入哪種砲彈就記在哪裡；
        /// 世界砲擊也用同一份資料決定這發要打哪種彈。
        /// </summary>
        private CompChangeableProjectile ChangeableProjectile
        {
            get
            {
                CompChangeableProjectile comp = parent.GetComp<CompChangeableProjectile>();
                if (comp != null) { return comp; }
                return (parent as Building_TurretGun)?.gun?.TryGetComp<CompChangeableProjectile>();
            }
        }

        private ThingDef ResolveProjectileDef()
        {
            // 可換彈藥的砲：只用砲手載入的那種砲彈；未裝填時回 null（等砲手補彈）
            CompChangeableProjectile loaded = ChangeableProjectile;
            if (loaded != null) { return loaded.Projectile; }
            return Props.projectileDef;
        }

        // =====================================================================
        //  火力通道管理
        // =====================================================================

        private CompFireControlRadar FindAvailableRadar()
        {
            Map map = parent.MapHeld;
            MapComponent_BVRCombat mgr = map?.GetComponent<MapComponent_BVRCombat>();
            if (mgr == null) { return null; }

            CompFireControlRadar best = null;
            foreach (CompFireControlRadar fc in mgr.fireControlRadars)
            {
                if (fc.parent.Faction != parent.Faction) { continue; }
                if (!fc.Active || !fc.Props.supportsArtilleryGuidance) { continue; }
                if (fc.FreeArtilleryChannels <= 0) { continue; }
                if (best == null || fc.FreeArtilleryChannels > best.FreeArtilleryChannels)
                {
                    best = fc;
                }
            }
            return best;
        }

        // =====================================================================
        //  世界地圖目標選取
        // =====================================================================

        private void BeginWorldTargeting()
        {
            if (!CanFireGizmo(out _)) { return; }

            CameraJumper.TryJump(CameraJumper.GetWorldTarget(parent));

            PlanetTile origin = parent.Map?.Tile ?? PlanetTile.Invalid;
            Find.WorldTargeter.BeginTargeting(
                action:                    ChooseWorldTarget,
                canTargetTiles:            true,
                closeWorldTabWhenFinished: true,
                onUpdate: () =>
                {
                    if (Props.maxRangeTiles > 0f && origin.Valid)
                    {
                        GenDraw.DrawWorldRadiusRing(origin, Mathf.FloorToInt(Props.maxRangeTiles));
                    }
                },
                extraLabelGetter: t =>
                {
                    if (!t.IsValid || !t.Tile.Valid) { return null; }
                    // 沒有載入中的地圖 = 打不了（抵達時不生成地圖），游標先告訴玩家原因
                    if (Current.Game.FindMap(t.Tile) == null) { return "DMSE.Artillery.MapNotSpawned".Translate(); }
                    // 效力射要有校射落點，否則先告訴玩家去校射
                    if (fireForEffect && !TryGetRegisteredImpact(t.Tile, out _)) { return "DMSE.Artillery.NoRegistration".Translate(); }
                    if (Props.maxRangeTiles <= 0f || !origin.Valid) { return null; }
                    return "DMSE.MissileLauncher.RangeLabel".Translate(
                        MissileWorldUtility.RangeDistance(origin, t.Tile).ToString("0.#"), Props.maxRangeTiles.ToString("0.#"));
                }
            );
        }

        /// <summary>世界目標是否可射擊（同一圖層、不是本地圖、射程內、目標地圖已生成、效力射需先校射）；不可時給出原因。供選靶與測試共用。</summary>
        public bool CanTargetTile(PlanetTile tile, out string reason)
        {
            reason = null;
            if (!tile.Valid)
            {
                reason = "MessageTransportPodsDestinationIsInvalid".Translate();
                return false;
            }
            PlanetTile origin = parent.Map?.Tile ?? PlanetTile.Invalid;
            if (!origin.Valid) { return true; }

            // 砲彈無導引，不能跨圖層（地表 ↔ 軌道）
            if (!MissileWorldUtility.SameLayer(origin, tile))
            {
                reason = "DMSE.Artillery.CrossLayer".Translate();
                return false;
            }
            // 本地圖：砲彈會從自己地圖邊緣落回地圖中心，等同砲擊自家
            if (tile == origin)
            {
                reason = "DMSE.Artillery.TargetIsHome".Translate();
                return false;
            }
            if (Props.maxRangeTiles > 0f && MissileWorldUtility.RangeDistance(origin, tile) > Props.maxRangeTiles)
            {
                reason = "DMSE.Artillery.OutOfRange".Translate(Props.maxRangeTiles.ToString("0.#"));
                return false;
            }
            // 目標地圖必須已存在（spawned）：砲彈不會為了一個 tile 生成整張地圖，
            // 抵達時會用同一條規則再確認（WorldObject_ArtilleryStrike.Arrived，allowGenerateMap: false）。
            if (Current.Game.FindMap(tile) == null)
            {
                reason = "DMSE.Artillery.MapNotSpawned".Translate();
                return false;
            }
            // 效力射需要目標地圖上已有校射落點（同陣營）；沒有就先打一發校射觀測落點
            if (fireForEffect && !TryGetRegisteredImpact(tile, out _))
            {
                reason = "DMSE.Artillery.NoRegistration".Translate();
                return false;
            }
            return true;
        }

        private bool ChooseWorldTarget(GlobalTargetInfo target)
        {
            string reason = null;
            if (!target.IsValid || !CanTargetTile(target.Tile, out reason))
            {
                Messages.Message(
                    reason ?? "MessageTransportPodsDestinationIsInvalid".Translate(),
                    MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }

            StartSustainedFire(target.Tile);
            return true;
        }

        /// <summary>設定持續射擊目標；狀態機在下一個 tick 開始瞄準流程。不檢查目標（呼叫者先用 <see cref="CanTargetTile"/>）。</summary>
        public void StartSustainedFire(PlanetTile tile)
        {
            targetTile       = tile;
            warmupEndTick    = -1;
            effectShotsFired = 0;   // 新的射擊任務：效力射散佈從 Def 的 scatterRadius 重新起算
        }

        // =====================================================================
        //  邊緣格計算（球面切線框架，不依賴螢幕空間）
        // =====================================================================

        private IntVec3 CalcExitEdgeCell(PlanetTile destTile)
        {
            Map map            = parent.Map;
            PlanetTile srcTile = map.Tile;

            if (srcTile == destTile)
            {
                return new IntVec3(
                    Mathf.Clamp(parent.Position.x, 1, map.Size.x - 2), 0, map.Size.z - 1);
            }

            // 沿砲身方位（= 世界方位）直線到地圖邊緣，離場航線才會與砲口朝向一致
            float angle = ArtilleryStrikeUtility.WorldDirectionAngle(srcTile, destTile);
            return ArtilleryStrikeUtility.EdgeCellAlongBearing(map, parent.TrueCenter(), angle);
        }

        // =====================================================================
        //  砲管旋轉（設定 Building_TurretGun.top 的 TurretTop.CurRotation）
        // =====================================================================

        private static readonly AccessTools.FieldRef<Building_TurretGun, TurretTop> TurretTopRef =
            AccessTools.FieldRefAccess<Building_TurretGun, TurretTop>("top");

        /// <summary>
        /// 將砲管轉向指定方位角（0=North/+Z，90=East/+X，與 <see cref="TurretTop.CurRotation"/> 相同）；NaN 時不動作。
        /// 原版砲塔：砲管朝向存放在 <see cref="Building_TurretGun"/> 的 protected 欄位 top（<see cref="TurretTop"/>），直接設定。
        /// 一般建築：交給 <see cref="CompTurretFacingRenderer"/> 平滑轉向（非正方形砲塔頭貼圖只能這樣畫）。
        /// </summary>
        private void SetTurretRotation(float angleDeg)
        {
            if (float.IsNaN(angleDeg)) { return; }
            if (parent is Building_TurretGun turret && TurretTopRef(turret) is TurretTop top)
            {
                top.CurRotation = angleDeg;
            }
            CompTurretFacingRenderer head = parent.GetComp<CompTurretFacingRenderer>();
            if (head != null)
            {
                head.ForceFaceTarget(parent.DrawPos + Quaternion.Euler(0f, angleDeg, 0f) * Vector3.forward * 10f);
            }
        }

        /// <summary>砲身是否已指向瞄準方位（沒有方位、或沒有 CompTurretFacingRenderer 時視為已指向）。</summary>
        private bool BarrelOnBearing
        {
            get
            {
                if (float.IsNaN(aimAngle)) { return true; }
                CompTurretFacingRenderer head = parent.GetComp<CompTurretFacingRenderer>();
                return head == null || Mathf.Abs(Mathf.DeltaAngle(head.CurRotation, aimAngle)) <= Props.aimToleranceDeg;
            }
        }

        /// <summary>目前瞄準方位角（NaN = 未在瞄準）；測試用。</summary>
        public float AimAngle => aimAngle;

        private static IntVec3 GetMapCenterCell(PlanetTile tile)
        {
            Map existing = Current.Game.FindMap(tile);
            if (existing != null)
            {
                return new IntVec3(existing.Size.x / 2, 0, existing.Size.z / 2);
            }
            return new IntVec3(125, 0, 125);
        }

        // =====================================================================
        //  單次開火（由狀態機呼叫，不再重複確認冷卻）
        // =====================================================================

        private void FireSingleShot(PlanetTile destTile)
        {
            ThingDef projDef = ResolveProjectileDef();
            if (projDef == null) { StopSustainedFire(); return; }

            // 消耗彈藥與砲管（可換彈藥的砲：扣掉砲手載入的那一發，之後由砲手補彈）
            ChangeableProjectile?.Notify_ProjectileLaunched();
            parent.GetComp<CompRefuelable>()?.ConsumeFuel(1f);

            // 一門砲同時只佔一條通道：上一發的通道若還沒到期，先釋放再重新找
            if (reservedRadar != null)
            {
                reservedRadar.ReleaseArtilleryChannel();
                reservedRadar      = null;
                channelReleaseTick = -1;
            }

            // 雷達導引：開火瞬間有空閒通道才算數（requiresFireControlChannel 時狀態機已確認有通道）
            // 效力射優先：效力射不找導引、不佔通道，一律打已登錄的校射落點。
            CompFireControlRadar radar = Props.requiresFireControlChannel || (Props.SupportsRadarGuidance && !fireForEffect)
                ? FindAvailableRadar() : null;
            bool guided = radar != null && Props.SupportsRadarGuidance && !fireForEffect;

            // 瞄準格與散佈：校射＝目標地圖中心（現行行為）；效力射＝已登錄落點，且每一發再收束
            IntVec3 aimCell = GetMapCenterCell(destTile);
            float scatter   = guided ? Props.guidedScatterRadius : Props.scatterRadius;
            if (fireForEffect && !guided)
            {
                if (TryGetRegisteredImpact(destTile, out IntVec3 registered)) { aimCell = registered; }
                scatter = CurrentEffectScatterRadius;
            }

            // 生成離場投射物，朝對應方位邊緣格飛去
            IntVec3 edgeCell = CalcExitEdgeCell(destTile);

            Projectile_ArtilleryDeparture departure =
                (Projectile_ArtilleryDeparture)GenSpawn.Spawn(
                    Props.departureProjectileDef, parent.Position, parent.Map);

            departure.destTile                    = destTile;
            departure.artilleryTargetCell         = aimCell;
            departure.artilleryProjectileDef      = projDef;
            departure.artilleryAttackerFaction    = parent.Faction;
            departure.artilleryScatterRadius      = scatter;
            departure.artilleryGuided             = guided;
            departure.artilleryFireForEffect      = fireForEffect;
            departure.artillerySalvoCount         = Props.salvoCount;
            departure.artillerySalvoIntervalTicks = Props.salvoIntervalTicks;
            departure.artilleryWorldObjectDef     = Props.worldObjectDef;
            departure.artilleryWorldTicksPerTile  = Props.worldTicksPerTile;

            departure.Launch(
                launcher:            parent,
                origin:              parent.TrueCenter(),
                usedTarget:          new LocalTargetInfo(edgeCell),
                intendedTarget:      new LocalTargetInfo(edgeCell),
                hitFlags:            ProjectileHitFlags.None,
                preventFriendlyFire: true,
                equipment:           null
            );

            // 裝填冷卻（下一發要等此 tick 數後才能再瞄準）
            cooldownUntil = Find.TickManager.TicksGame + Props.cooldownTicks;

            // 火力通道佔用
            if (radar != null)
            {
                radar.OccupyArtilleryChannel();
                reservedRadar      = radar;
                channelReleaseTick = Find.TickManager.TicksGame + Props.fireControlHoldTicks;
            }

            LastShotGuided = guided;

            // 效力射：這一發已經打出，下一次散佈再收束 25%
            if (fireForEffect) { effectShotsFired++; }
        }

        /// <summary>最近一發是否有雷達導引（不存檔）；測試與檢視用。</summary>
        public bool LastShotGuided { get; private set; }

        /// <summary>目前為砲擊引導佔用的火控雷達（無則 null）；測試用。</summary>
        public CompFireControlRadar ReservedRadar => reservedRadar;

        // =====================================================================
        //  建築拆除：釋放火力通道
        // =====================================================================

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (reservedRadar != null)
            {
                reservedRadar.ReleaseArtilleryChannel();
                reservedRadar      = null;
                channelReleaseTick = -1;
            }
        }
    }
}
