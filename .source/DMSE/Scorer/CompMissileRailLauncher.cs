using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public class CompProperties_MissileRailLauncher : CompProperties
    {
        /// <summary>離場 skyfaller（thingClass 應為 DMSE.ScorerProjectile）。</summary>
        public ThingDef skyfaller;

        /// <summary>落點 incoming skyfaller（thingClass 應為 DMSE.MissileIncoming）。</summary>
        public ThingDef skyfallerIncoming;

        /// <summary>世界旅行物件（worldObjectClass 應為 DMSE.ScorerProjectile_WorldObject）。</summary>
        public WorldObjectDef worldObjectDef;

        /// <summary>當裝填的導彈物品無自身 CompMissileConfig 時，用此彈體建立發射設定。</summary>
        public MissileBodyDef missileBody;

        /// <summary>一次發射後的冷卻（tick）。</summary>
        public int launchCooldownTicks = 600;

        /// <summary>離場 skyfaller 生成於建築前方（沿朝向）的格數。</summary>
        public int launchForwardCells = 2;

        public CompProperties_MissileRailLauncher()
        {
            compClass = typeof(CompMissileLauncher_Rail);
        }
    }

    /// <summary>
    /// 巡飛彈發射軌：<see cref="CompMissileLauncher_WorldTargeting"/> 家族中彈藥以實體 Thing
    /// 存於 parent 容器（<see cref="Building_MissileRack"/>）的成員，由
    /// <see cref="CompMissileRackRenderer"/> 依建築朝向渲染；發射時取出該 Thing。
    ///
    /// 與 <see cref="CompMissileLauncher_Scorer"/> 的差異：彈藥來源是容器中的實體導彈，而非
    /// <see cref="CompRefuelable"/> 的抽象燃料。選靶／發射流程由中介類統一提供。
    /// </summary>
    public class CompMissileLauncher_Rail : CompMissileLauncher_WorldTargeting
    {
        public CompProperties_MissileRailLauncher Props => (CompProperties_MissileRailLauncher)props;

        protected override int LaunchCooldownTicks => Props.launchCooldownTicks;

        protected override string CooldownScribeKey => "railLaunchCooldownUntil";

        private ThingOwner HeldContainer => (parent as IThingHolder)?.GetDirectlyHeldThings();

        private Thing LoadedMissile
        {
            get
            {
                ThingOwner owner = HeldContainer;
                return owner != null && owner.Count > 0 ? owner[0] : null;
            }
        }

        public override bool HasAmmo => LoadedMissile != null;

        protected override string LoadedStatusText => null;

        protected override ThingDef LaunchSkyfallerDef => Props.skyfaller;

        protected override ThingDef IncomingSkyfallerDef => Props.skyfallerIncoming;

        protected override WorldObjectDef TravelWorldObjectDef => Props.worldObjectDef;

        protected override int LaunchForwardCells => Props.launchForwardCells;

        protected override string GizmoLabel => "ScorerLaunch".Translate();

        protected override string GizmoDesc => "ScorerLaunchDesc".Translate();

        protected override string NoAmmoDisableReason => "DMSE.MissileLauncher.Rail.NoMissileLoaded".Translate();

        /// <summary>建立發射設定：優先採用導彈物品自身的 CompMissileConfig，否則由 Props.missileBody 生成。</summary>
        protected override MissileConfig BuildFiringConfig()
        {
            Thing missile = LoadedMissile;
            CompMissileConfig cfg = missile?.TryGetComp<CompMissileConfig>();
            if (cfg != null && cfg.config != null && cfg.config.Valid)
            {
                return cfg.config.Clone();
            }
            if (Props.missileBody != null)
            {
                return new MissileConfig(Props.missileBody);
            }
            return null;
        }

        /// <summary>消耗實體彈藥。</summary>
        protected override void ConsumeAmmo()
        {
            Thing missile = LoadedMissile;
            if (missile == null)
            {
                return;
            }
            HeldContainer?.Remove(missile);
            missile.Destroy(DestroyMode.Vanish);
        }
    }
}
