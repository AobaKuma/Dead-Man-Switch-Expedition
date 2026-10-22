using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 所有導彈發射平台的共用父類別：防空攔截彈旋轉發射架
    /// （<see cref="CompMissileLauncher_Interceptor"/>）、彈道飛彈發射井
    /// （<see cref="CompMissileLauncher_Ballistic"/>）、巡飛彈發射軌
    /// （<see cref="CompMissileLauncher_Rail"/>）與通用計分彈發射器
    /// （<see cref="CompMissileLauncher_Scorer"/>）皆繼承自本類別。
    ///
    /// 統一內容：
    ///   - 發射冷卻計時（<see cref="cooldownUntil"/> / <see cref="StartCooldown"/> / <see cref="OnCooldown"/>）。
    ///   - <see cref="CompInspectStringExtra"/>：冷卻中顯示剩餘時間，否則顯示各平台的裝填狀態
    ///     （由子類別以 <see cref="LoadedStatusText"/> 提供內容，本類別只負責組裝與冷卻優先顯示）。
    ///   - <see cref="Active"/>：繼承 <see cref="CompBVRDevice"/> 的斷電/開關/損壞/暈眩/未操作判定，
    ///     並疊加 <see cref="HasAmmo"/>，使所有發射平台共享一致的可運作性條件。
    /// </summary>
    public abstract class CompMissileLauncher : CompBVRDevice
    {
        /// <summary>目前冷卻結束的世界 tick；小於等於目前 tick 表示冷卻已結束。</summary>
        protected int cooldownUntil;

        /// <summary>目前冷卻結束的世界 tick（供跨平台比較，例如挑選最先冷卻完成的發射架）。</summary>
        public int CooldownUntil => cooldownUntil;

        /// <summary>本次發射後的冷卻時長（tick），由各平台自其 Props 對應欄位提供。</summary>
        protected abstract int LaunchCooldownTicks { get; }

        /// <summary>每次發射後的冷卻時長（供波次打擊的時間軸估算）。</summary>
        public int CooldownTicksAfterLaunch => LaunchCooldownTicks;

        /// <summary>Scribe 存檔用的欄位名稱；各平台沿用各自舊有名稱以維持存檔相容。</summary>
        protected virtual string CooldownScribeKey => "missileLauncherCooldownUntil";

        /// <summary>冷卻中顯示文字所用的翻譯鍵；各平台可覆寫以沿用各自原有措辭。</summary>
        protected virtual string CooldownTranslationKey => "DMSE.MissileLauncher.LaunchCooldown";

        /// <summary>平台是否已裝填彈藥／完成裝配，可供發射（不含冷卻判定）。</summary>
        public abstract bool HasAmmo { get; }

        /// <summary>
        /// 非冷卻狀態下顯示的裝填狀態文字（例如已裝填內容、等待裝配、無彈藥…）；
        /// 若平台無額外資訊可顯示則回傳 null。
        /// </summary>
        protected abstract string LoadedStatusText { get; }

        /// <summary>平台目前是否可運作：除 <see cref="CompBVRDevice.Active"/> 的一般條件外，亦須已裝填彈藥。</summary>
        public override bool Active => base.Active && HasAmmo;

        /// <summary>指定時刻是否仍在冷卻中。</summary>
        public bool OnCooldown(int now) => now < cooldownUntil;

        /// <summary>指定時刻距冷卻結束的剩餘 tick；已結束則回傳 0。</summary>
        public int CooldownTicksRemaining(int now) => cooldownUntil > now ? cooldownUntil - now : 0;

        /// <summary>發射成功後呼叫，依 <see cref="LaunchCooldownTicks"/> 重新起算冷卻。</summary>
        protected void StartCooldown(int now) => cooldownUntil = now + LaunchCooldownTicks;

        /// <summary>
        /// 平台當前詳情：冷卻中優先顯示剩餘時間；冷卻結束則交由子類別的
        /// <see cref="LoadedStatusText"/> 顯示裝填/裝配狀態。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            int now = Find.TickManager.TicksGame;
            int remaining = CooldownTicksRemaining(now);
            if (remaining > 0)
            {
                return CooldownTranslationKey.Translate(remaining.ToStringTicksToPeriod());
            }
            return LoadedStatusText;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, CooldownScribeKey, 0);
        }
    }

    /// <summary>
    /// 世界選靶發射平台的共用中介類：巡飛彈發射軌（<see cref="CompMissileLauncher_Rail"/>）、
    /// 彈道飛彈發射井（<see cref="CompMissileLauncher_Ballistic"/>）與通用計分彈發射器
    /// （<see cref="CompMissileLauncher_Scorer"/>）皆繼承本類。
    ///
    /// 統一內容：
    ///   - 發射 Gizmo（世界選靶、射程檢查與標示、無彈藥／全數保留／裝置不可用／冷卻中停用）。
    ///   - <see cref="TryLaunchRound"/>：發射指定彈藥——生成離場 skyfaller 與世界旅行物件、掛上發射設定、
    ///     消耗該彈藥（<see cref="ConsumeAmmo"/>）並起算冷卻。單發 Gizmo 與波次打擊共用。
    ///   - <see cref="CanTargetTile"/>：預設排除水域與已有非 MapParent 世界物件的 tile，
    ///     三平台選靶規則一致。
    ///   - 向 <see cref="MapComponent_StrikePlanner"/> 註冊，供波次打擊列舉在架彈藥。
    ///
    /// 「彈藥」（round）：Rail 為容器中可進攻的導彈 Thing；彈道井與 Scorer 為平台本身（已裝填時）。
    /// 被打擊任務保留的彈藥（<see cref="StrikeReservation"/>）不會被單發 Gizmo 取用。
    ///
    /// 子類只需提供 def 來源、彈藥列舉、發射設定建構與彈藥消耗方式。
    /// </summary>
    public abstract class CompMissileLauncher_WorldTargeting : CompMissileLauncher
    {
        /// <summary>
        /// 平台預設的離場 def（thingClass 為 <see cref="ScorerProjectile"/>）：只在彈體沒有
        /// <see cref="MissileBodyDef.launchProjectile"/> 時使用（見 <see cref="LaunchDefFor"/>）。
        /// </summary>
        protected abstract ThingDef LaunchSkyfallerDef { get; }

        /// <summary>平台預設的落點 def（thingClass 應為 <see cref="Projectile_MissileIncoming"/>）：彈體沒有 incomingSkyfaller 時使用。</summary>
        protected abstract ThingDef IncomingSkyfallerDef { get; }

        /// <summary>世界旅行物件 def（worldObjectClass 應為 <see cref="ScorerProjectile_WorldObject"/>）。</summary>
        protected abstract WorldObjectDef TravelWorldObjectDef { get; }

        /// <summary>離場 skyfaller 生成於建築中心沿朝向的格數偏移（垂直發射為 0）。</summary>
        protected virtual int LaunchForwardCells => 0;

        /// <summary>升空到世界航跡出現的時間（平台預設離場 def）。時間軸估算用；知道彈藥時用 <see cref="DepartureTicksFor"/>。</summary>
        public int DepartureTicks => MissileProjectileMaker.DepartureTicksOf(LaunchSkyfallerDef);

        /// <summary>以此設定發射時實際使用的離場 def：彈體的發射 Projectile 優先，沒有時退回平台的 skyfaller。</summary>
        public ThingDef LaunchDefFor(MissileConfig config) => config?.body?.launchProjectile ?? LaunchSkyfallerDef;

        /// <summary>離場彈的生成格（建築中心沿朝向偏移 <see cref="LaunchForwardCells"/>）。</summary>
        private IntVec3 LaunchCell => parent.Position + (parent.Rotation.AsIntVec3 * LaunchForwardCells);

        /// <summary>
        /// 以此設定朝 <paramref name="target"/> 發射時，升空到世界航跡出現的 tick 數：
        /// 升空後沿出發方位飛出地圖邊緣（§8.3），用與實際飛行相同的模擬計算。
        /// </summary>
        public int DepartureTicksFor(MissileConfig config, PlanetTile target)
        {
            if (parent.Map == null) { return MissileProjectileMaker.DepartureTicksOf(LaunchDefFor(config)); }
            return MissileProjectileMaker.DepartureTicksOf(LaunchDefFor(config), LaunchCell, parent.Map.Size, parent.Rotation,
                MissileProjectileMaker.DepartureHeading(parent.Map.Tile, target));
        }

        /// <summary>
        /// 可供世界打擊的在架彈藥（含被打擊任務保留者）：Rail 為容器中可進攻的導彈；
        /// 彈道井／Scorer 已裝填時為平台本身。
        /// </summary>
        public abstract IEnumerable<Thing> StrikeRounds { get; }

        /// <summary>單發 Gizmo 使用的彈藥：第一枚未被保留的彈藥；沒有時為 null。</summary>
        public Thing NextRound
        {
            get
            {
                MapComponent_StrikePlanner planner = StrikeReservation.PlannerFor(parent);
                foreach (Thing round in StrikeRounds)
                {
                    if (planner == null || !planner.IsReserved(round)) { return round; }
                }
                return null;
            }
        }

        /// <summary>彈藥是否仍在本平台上（可發射）。以參照比較：Thing.Equals 比的是 ID 與 def。</summary>
        public bool OwnsRound(Thing round)
        {
            if (round == null) { return false; }
            foreach (Thing r in StrikeRounds)
            {
                if (ReferenceEquals(r, round)) { return true; }
            }
            return false;
        }

        /// <summary>同一波次內同平台各枚的發射間隔（tick）；0 表示單發平台。</summary>
        public virtual int SalvoIntervalTicks => 0;

        /// <summary>建構指定彈藥的發射設定（應回傳副本）；彈藥不在本平台或無法建構時回傳 null。</summary>
        public abstract MissileConfig BuildFiringConfig(Thing round);

        /// <summary>發射設定為 null 時是否中止發射（彈道井的重置流程依賴 config 存在）。</summary>
        protected virtual bool RequiresConfig => false;

        /// <summary>發射成功後消耗指定彈藥（取出實體彈／清空燃料／重置裝配旗標）。</summary>
        protected abstract void ConsumeAmmo(Thing round);

        /// <summary>發射 Gizmo 的標籤與說明。</summary>
        protected abstract string GizmoLabel { get; }
        protected abstract string GizmoDesc { get; }

        /// <summary>無彈藥時 Gizmo 的停用理由。</summary>
        protected abstract string NoAmmoDisableReason { get; }

        /// <summary>下一枚單發彈藥的射程（世界 tile）；0 = 不限。</summary>
        private int LoadedRange
        {
            get
            {
                MissileConfig c = BuildFiringConfig(NextRound);
                return c != null ? c.Range : 0;
            }
        }

        private int DistanceToTile(GlobalTargetInfo t)
        {
            return Mathf.RoundToInt(MissileWorldUtility.RangeDistance(parent.Map.Tile, t.Tile));
        }

        /// <summary>選靶處的目標：點到的 MapParent，或該 tile 上的 MapParent；沒有則為 null（抵達時落空）。</summary>
        private static MapParent TargetParentAt(GlobalTargetInfo t)
        {
            return t.WorldObject as MapParent ?? Find.WorldObjects.MapParentAt(t.Tile);
        }

        /// <summary>選靶游標上的落點方式說明：「{導引}：抵達後手動選擇落點」或「{導引}：自動鎖定」。</summary>
        public static string GuidanceTargetingText(MissileConfig config)
        {
            if (config == null) { return null; }
            return (config.PlayerChoosesTarget
                    ? "DMSE.Guidance.ManualTarget"
                    : "DMSE.Guidance.AutoTarget").Translate(config.TargetingLabel);
        }

        /// <summary>選靶驗證：預設排除水域與已有非 MapParent 世界物件的 tile。</summary>
        protected virtual bool CanTargetTile(GlobalTargetInfo t)
        {
            // 反艦：移動中的據點（艦艇）可能停在海上，移動目標不受水域限制。
            if (t.WorldObject is MapParent mobileTarget
                && WorldComponent_BVRNetwork.Current != null
                && WorldComponent_BVRNetwork.Current.IsMobileTarget(mobileTarget))
            {
                return true;
            }
            if (t.Tile.Tile.PrimaryBiome.isWaterBiome)
            {
                return false;
            }
            if (Find.World.worldObjects.WorldObjectAt<WorldObject>(t.Tile) is WorldObject wo && !(wo is MapParent))
            {
                return false;
            }
            return true;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            Command_Action command = new Command_Action
            {
                defaultLabel = GizmoLabel,
                defaultDesc = GizmoDesc,
                icon = CompLaunchable.LaunchCommandTex,
                action = () =>
                {
                    CameraJumper.TryJump(CameraJumper.GetWorldTarget(parent), CameraJumper.MovementMode.Pan);
                    Find.WorldTargeter.BeginTargeting(t =>
                    {
                        if (!CanTargetTile(t))
                        {
                            return false;
                        }
                        // 選靶期間狀態可能改變（彈被卸除、被保留、斷電…），由 TryLaunchRound 再驗一次；
                        // 跨圖層需導引能力，射程以地表等效格計（跨圖層另加成本）。
                        if (TryLaunchRound(t, NextRound, false, out string reason) == null)
                        {
                            if (!reason.NullOrEmpty()) { Messages.Message(reason, MessageTypeDefOf.RejectInput, false); }
                            return false;
                        }
                        return true;
                    },
                    canTargetTiles: true,
                    mouseAttachment: TravelWorldObjectDef?.ExpandingIconTexture,
                    closeWorldTabWhenFinished: true,
                    onUpdate: null,
                    extraLabelGetter: t =>
                    {
                        string label = string.Empty;
                        int range = LoadedRange;
                        if (range > 0)
                        {
                            label = "DMSE.MissileLauncher.RangeLabel".Translate(DistanceToTile(t), range);
                        }
                        if (t.IsValid && TargetParentAt(t) == null)
                        {
                            if (!label.NullOrEmpty()) { label += "\n"; }
                            label += "DMSE.MissileLauncher.NoTargetHere".Translate();
                        }
                        // 落點選擇方式：電視制導抵達後手選，其餘依導引頭自動鎖定
                        string seeker = GuidanceTargetingText(BuildFiringConfig(NextRound));
                        if (!seeker.NullOrEmpty())
                        {
                            if (!label.NullOrEmpty()) { label += "\n"; }
                            label += seeker;
                        }
                        return label;
                    },
                    canSelectTarget: null,
                    originForClosest: null,
                    showCancelButton: true);
                }
            };

            if (!HasAmmo)
            {
                command.Disable(NoAmmoDisableReason);
            }
            else if (NextRound == null)
            {
                // 在架彈藥全數被打擊任務保留。
                command.Disable("DMSE.Strike.AllRoundsReserved".Translate());
            }
            else if (!Active)
            {
                // 有彈但斷電／關閉／損壞／暈眩／未操作 → 不可發射。
                command.Disable("DMSE.BVR.DeviceInactive".Translate());
            }
            int cooldownLeft = CooldownTicksRemaining(Find.TickManager.TicksGame);
            if (cooldownLeft > 0)
            {
                command.Disable("DMSE.MissileLauncher.LaunchCooldown".Translate(cooldownLeft.ToStringTicksToPeriod()));
            }

            yield return command;
        }

        /// <summary>
        /// 發射指定彈藥（docs/BVR_波次打擊規劃.md §7-2）。單發 Gizmo 傳入 <see cref="NextRound"/>；
        /// 波次打擊傳入保留中的彈藥，本方法不檢查保留。
        /// <paramref name="salvoContinuation"/> = 同一波次同平台的後續各枚：略過冷卻檢查；
        /// 每枚發射後都重新起算冷卻，所以冷卻從本平台最後一枚起算。
        /// 成功回傳世界航跡物件（離場 skyfaller 離開地圖時才加入世界）；失敗回傳 null 並給出原因。
        /// </summary>
        public ScorerProjectile_WorldObject TryLaunchRound(GlobalTargetInfo target, Thing round, bool salvoContinuation, out string failReason)
        {
            failReason = null;
            if (TravelWorldObjectDef == null || parent.Map == null)
            {
                failReason = "DMSE.BVR.DeviceInactive".Translate();
                return null;
            }
            if (!OwnsRound(round))
            {
                failReason = NoAmmoDisableReason;
                return null;
            }
            if (!Active)
            {
                failReason = "DMSE.BVR.DeviceInactive".Translate();
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (!salvoContinuation && OnCooldown(now))
            {
                failReason = "DMSE.MissileLauncher.LaunchCooldown".Translate(CooldownTicksRemaining(now).ToStringTicksToPeriod());
                return null;
            }

            MissileConfig config = BuildFiringConfig(round);
            if (RequiresConfig && config == null)
            {
                failReason = NoAmmoDisableReason;
                return null;
            }
            ThingDef launchDef = LaunchDefFor(config);
            if (launchDef == null)
            {
                failReason = "DMSE.BVR.DeviceInactive".Translate();
                return null;
            }
            // 移動中的目標可能已出射程；跨圖層需導引能力
            if (!MissileWorldUtility.CanEngage(config, parent.Map.Tile, target.Tile, 0f, out failReason))
            {
                return null;
            }

            ScorerProjectile_WorldObject wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(TravelWorldObjectDef);
            // 落點視覺／BVR 目標特性以彈體為準（同一平台可換彈體）；平台指定的 skyfallerIncoming 僅作後備。
            wo.skyfallerIncoming = config?.body?.incomingSkyfaller ?? IncomingSkyfallerDef;
            wo.SetFaction(Faction.OfPlayer);
            wo.Tile = parent.Map.Tile;
            wo.destinationTile = target.Tile;
            wo.targetParent = TargetParentAt(target);
            wo.config = config;
            wo.launcherThing = parent; // 落地爆炸歸因於本發射建築（被攔截時不歸因）
            wo.launchSpawnTick = LaunchDeparture(wo, launchDef, target.Tile);

            ConsumeAmmo(round);
            StartCooldown(now);
            return wo;
        }

        /// <summary>
        /// 從本平台升空一枚離場彈，飛出地圖邊緣時把 <paramref name="worldObject"/> 加入世界；回傳升空彈實際生成 tick。
        /// 不檢查彈藥、冷卻或可運作性（NPC 導彈陣地從彈架取彈後經此發射，見 <see cref="MapComponent_MissileBase"/>）。
        /// </summary>
        public int LaunchDeparture(WorldObject worldObject, ThingDef launchDef, PlanetTile targetTile)
        {
            // 升空外觀與尾焰隨彈體（launchProjectile）；彈體沒有設定時退回平台的離場 skyfaller。
            // 升空後沿世界航跡的出發方位飛出地圖邊緣（跨圖層沒有水平方位時照原本升空後消失）。
            // 經發射排程：同一張地圖的升空彈錯開生成，後焰在實際生成時觸發（MapComponent_MissileLaunchQueue）。
            float? heading = MissileProjectileMaker.DepartureHeading(parent.Map.Tile, targetTile);
            return MapComponent_MissileLaunchQueue.Launch(parent.Map, new PendingMissileLaunch
            {
                def = launchDef,
                cell = LaunchCell,
                rotation = parent.Rotation,
                launcher = parent,
                worldObject = worldObject,
                hasHeading = heading != null,
                heading = heading ?? 0f,
            });
        }

        /// <summary>在基底的冷卻／裝填狀態之後，加上被打擊任務保留的彈藥數。</summary>
        public override string CompInspectStringExtra()
        {
            string text = base.CompInspectStringExtra();
            MapComponent_StrikePlanner planner = StrikeReservation.PlannerFor(parent);
            int reserved = planner == null ? 0 : StrikeRounds.Count(planner.IsReserved);
            if (reserved > 0)
            {
                string line = "DMSE.Strike.ReservedInspect".Translate(reserved);
                text = text.NullOrEmpty() ? line : text + "\n" + line;
            }
            return text;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map?.GetComponent<MapComponent_StrikePlanner>()?.launchers.Add(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            map?.GetComponent<MapComponent_StrikePlanner>()?.launchers.Remove(this);
        }
    }
}
