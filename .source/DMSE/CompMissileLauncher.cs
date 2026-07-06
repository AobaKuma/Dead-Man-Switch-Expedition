using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
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
    ///   - 發射 Gizmo（世界選靶、射程檢查與標示、無彈藥／裝置不可用／冷卻中停用）。
    ///   - <see cref="Launch"/>：生成離場 skyfaller 與世界旅行物件、掛上發射設定、
    ///     消耗彈藥（<see cref="ConsumeAmmo"/>）並起算冷卻。
    ///   - <see cref="CanTargetTile"/>：預設排除水域與已有非 MapParent 世界物件的 tile，
    ///     三平台選靶規則一致。
    ///
    /// 子類只需提供 def 來源、發射設定建構與彈藥消耗方式。
    /// </summary>
    public abstract class CompMissileLauncher_WorldTargeting : CompMissileLauncher
    {
        /// <summary>離場 skyfaller def（thingClass 應為 <see cref="ScorerProjectile"/>）。</summary>
        protected abstract ThingDef LaunchSkyfallerDef { get; }

        /// <summary>落點 incoming skyfaller def（thingClass 應為 <see cref="MissileIncoming"/>）。</summary>
        protected abstract ThingDef IncomingSkyfallerDef { get; }

        /// <summary>世界旅行物件 def（worldObjectClass 應為 <see cref="ScorerProjectile_WorldObject"/>）。</summary>
        protected abstract WorldObjectDef TravelWorldObjectDef { get; }

        /// <summary>離場 skyfaller 生成於建築中心沿朝向的格數偏移（垂直發射為 0）。</summary>
        protected virtual int LaunchForwardCells => 0;

        /// <summary>建構本次發射的設定（應回傳副本）；無法建構時回傳 null。</summary>
        protected abstract MissileConfig BuildFiringConfig();

        /// <summary>發射設定為 null 時是否中止發射（彈道井的重置流程依賴 config 存在）。</summary>
        protected virtual bool RequiresConfig => false;

        /// <summary>發射成功後消耗彈藥（取出實體彈／清空燃料／重置裝配旗標）。</summary>
        protected abstract void ConsumeAmmo();

        /// <summary>發射 Gizmo 的標籤與說明。</summary>
        protected abstract string GizmoLabel { get; }
        protected abstract string GizmoDesc { get; }

        /// <summary>無彈藥時 Gizmo 的停用理由。</summary>
        protected abstract string NoAmmoDisableReason { get; }

        /// <summary>已裝填彈藥的射程（世界 tile）；0 = 不限。</summary>
        private int LoadedRange
        {
            get
            {
                MissileConfig c = BuildFiringConfig();
                return c != null ? c.Range : 0;
            }
        }

        private int DistanceToTile(GlobalTargetInfo t)
        {
            return (int)Find.WorldGrid.ApproxDistanceInTiles(parent.Map.Tile, t.Tile);
        }

        /// <summary>選靶驗證：預設排除水域與已有非 MapParent 世界物件的 tile。</summary>
        protected virtual bool CanTargetTile(GlobalTargetInfo t)
        {
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
                        int range = LoadedRange;
                        if (range > 0 && DistanceToTile(t) > range)
                        {
                            Messages.Message(
                                "DMSE.MissileLauncher.OutOfRange".Translate(range),
                                MessageTypeDefOf.RejectInput,
                                false);
                            return false;
                        }
                        Launch(t);
                        return true;
                    },
                    canTargetTiles: true,
                    mouseAttachment: TravelWorldObjectDef?.ExpandingIconTexture,
                    closeWorldTabWhenFinished: true,
                    onUpdate: null,
                    extraLabelGetter: t =>
                    {
                        int range = LoadedRange;
                        if (range <= 0) { return string.Empty; }
                        return "DMSE.MissileLauncher.RangeLabel".Translate(DistanceToTile(t), range);
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

        /// <summary>共用發射流程；選靶後由 Gizmo 呼叫。</summary>
        protected void Launch(GlobalTargetInfo t)
        {
            // 選靶期間狀態可能改變（彈被卸除、斷電…），發射前再驗一次。
            if (LaunchSkyfallerDef == null || TravelWorldObjectDef == null || parent.Map == null || !Active)
            {
                return;
            }

            MissileConfig config = BuildFiringConfig();
            if (RequiresConfig && config == null)
            {
                return;
            }

            ScorerProjectile faller = (ScorerProjectile)SkyfallerMaker.SpawnSkyfaller(
                LaunchSkyfallerDef,
                parent.Position + (parent.Rotation.AsIntVec3 * LaunchForwardCells),
                parent.Map);
            faller.Rotation = parent.Rotation;
            faller.angle = faller.Rotation.AsAngle;

            ScorerProjectile_WorldObject wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(TravelWorldObjectDef);
            wo.skyfallerIncoming = IncomingSkyfallerDef;
            wo.SetFaction(Faction.OfPlayer);
            wo.Tile = parent.Map.Tile;
            wo.destinationTile = t.Tile;
            wo.config = config;
            faller.worldObject = wo;

            ConsumeAmmo();
            StartCooldown(Find.TickManager.TicksGame);
        }
    }
}
