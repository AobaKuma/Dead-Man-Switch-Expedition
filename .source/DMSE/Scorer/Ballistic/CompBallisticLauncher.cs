using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public class CompProperties_BallisticLauncher : CompProperties
    {
        /// <summary>離場 skyfaller（thingClass 應為 DMSE.ScorerProjectile）。</summary>
        public ThingDef skyfaller;

        /// <summary>落點 incoming skyfaller（thingClass 應為 DMSE.MissileIncoming）。</summary>
        public ThingDef skyfallerIncoming;

        /// <summary>世界旅行物件（worldObjectClass 應為 DMSE.ScorerProjectile_WorldObject）。</summary>
        public WorldObjectDef worldObjectDef;

        /// <summary>發射後冷卻（tick）。彈道導彈裝填時間較長，預設 2400（40 秒）。</summary>
        public int launchCooldownTicks = 2400;

        /// <summary>離場 skyfaller 生成於建築中心的格數偏移（垂直發射通常為 0）。</summary>
        public int launchForwardCells = 0;

        public CompProperties_BallisticLauncher()
        {
            compClass = typeof(CompMissileLauncher_Ballistic);
        }
    }

    /// <summary>
    /// 彈道導彈發射井：<see cref="CompMissileLauncher_WorldTargeting"/> 家族中以裝配旗標
    /// （而非物品型態彈藥）判斷裝填狀態的成員。
    ///
    /// 工作流程：
    ///   1. 玩家於 <see cref="ITab_MissileAssembly"/> 設定 pending 設定（選擇彈頭/導引/酬載）。
    ///   2. <see cref="WorkGiver_AssembleBallisticSilo"/> 指派小人搬運資源並執行
    ///      <see cref="JobDriver_AssembleBallisticSilo"/>。
    ///   3. 裝配完成後 <see cref="MarkLoaded"/> 被呼叫，<see cref="IsLoaded"/> 變為 true。
    ///   4. 玩家透過 Gizmo（中介類提供）在世界地圖選靶並發射；發射後重置為未裝填。
    ///
    /// 與 <see cref="CompMissileLauncher_Rail"/> 的差異：
    ///   - 彈藥來源為同一建築上的 <see cref="CompMissileConfig"/>（無物品型態）。
    ///   - 以 <c>isAssembled</c> 旗標而非容器物品計數判斷「已裝填」。
    /// </summary>
    public class CompMissileLauncher_Ballistic : CompMissileLauncher_WorldTargeting
    {
        private bool isAssembled;

        public CompProperties_BallisticLauncher Props => (CompProperties_BallisticLauncher)props;

        /// <summary>是否已完成裝配、可以發射。</summary>
        public bool IsLoaded => isAssembled;

        /// <summary>裝配作業完成後由 <see cref="JobDriver_AssembleBallisticSilo"/> 呼叫。</summary>
        public void MarkLoaded() { isAssembled = true; }

        protected override int LaunchCooldownTicks => Props.launchCooldownTicks;

        protected override string CooldownScribeKey => "ballisticCooldownUntil";

        public override bool HasAmmo => isAssembled;

        private CompMissileConfig MissileCfg => parent?.TryGetComp<CompMissileConfig>();

        protected override ThingDef LaunchSkyfallerDef => Props.skyfaller;

        protected override ThingDef IncomingSkyfallerDef => Props.skyfallerIncoming;

        protected override WorldObjectDef TravelWorldObjectDef => Props.worldObjectDef;

        protected override int LaunchForwardCells => Props.launchForwardCells;

        /// <summary>發射後重置流程依賴 config 存在，config 為 null 時中止發射。</summary>
        protected override bool RequiresConfig => true;

        protected override string GizmoLabel => "DMSE.MissileLauncher.Ballistic.Fire".Translate();

        protected override string GizmoDesc => "DMSE.MissileLauncher.Ballistic.FireDesc".Translate();

        protected override string NoAmmoDisableReason => "DMSE.MissileLauncher.Ballistic.NotLoaded".Translate();

        protected override MissileConfig BuildFiringConfig() => MissileCfg?.config?.Clone();

        /// <summary>
        /// 發射後重置為未裝填，並清除 config（保留 body），使 config ≠ pending
        /// → NeedsAssembly = true → 殖民者下次必須重新搬運資源並執行製造工作。
        /// pending 保留：玩家不必重新選擇彈頭/導引/酬載，只需等待裝填。
        /// </summary>
        protected override void ConsumeAmmo()
        {
            isAssembled = false;

            CompMissileConfig cfg = MissileCfg;
            if (cfg != null)
            {
                // 保留 body 但清空部件 → config ≠ pending（pending 仍有選好的部件）
                cfg.config = new MissileConfig { body = cfg.pending?.body ?? cfg.config?.body };
                // 清除已搬運資源（彈體已發射消耗）
                cfg.delivered.Clear();
            }
        }

        protected override string LoadedStatusText
        {
            get
            {
                if (isAssembled)
                {
                    MissileConfig c = BuildFiringConfig();
                    if (c?.body != null)
                    {
                        return "DMSE.MissileLauncher.Ballistic.LoadedWith".Translate(c.body.LabelCap);
                    }
                    return "DMSE.MissileLauncher.Ballistic.Loaded".Translate();
                }
                CompMissileConfig cfg = MissileCfg;
                if (cfg != null && cfg.NeedsAssembly)
                {
                    return "DMSE.MissileLauncher.Ballistic.AwaitingAssembly".Translate();
                }
                return "DMSE.MissileLauncher.Ballistic.Unloaded".Translate();
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref isAssembled, "ballisticIsAssembled", false);
        }
    }
}
