using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public class CompPropertiesScorer : CompProperties
    {
        public CompPropertiesScorer() => this.compClass = typeof(CompMissileLauncher_Scorer);
        public ThingDef skyfaller;
        public ThingDef skyfallerIncoming;
        public WorldObjectDef worldObjectDef;
        public int launchCooldownTicks = 300; // 一次發射後的冷卻（預設 5 秒）。
        public int launchForwardCells = 2;    // 離場 skyfaller 生成於建築前方（沿朝向）的格數。
    }

    /// <summary>
    /// 通用計分彈發射器：<see cref="CompMissileLauncher_WorldTargeting"/> 家族中以
    /// <see cref="CompRefuelable"/> 抽象燃料作為彈藥來源的成員（相對於
    /// <see cref="CompMissileLauncher_Rail"/>／<see cref="CompMissileLauncher_Interceptor"/>
    /// 以實體 Thing 存放彈藥）。選靶／發射流程由中介類統一提供。
    /// </summary>
    public class CompMissileLauncher_Scorer : CompMissileLauncher_WorldTargeting
    {
        public CompPropertiesScorer Props => (CompPropertiesScorer)this.props;

        private CompRefuelable refuelable;

        private CompRefuelable Refuelable
        {
            get
            {
                if (refuelable == null) { refuelable = parent.GetComp<CompRefuelable>(); }
                return refuelable;
            }
        }

        protected override int LaunchCooldownTicks => Props.launchCooldownTicks;

        protected override string CooldownScribeKey => "launchCooldownUntil";

        public override bool HasAmmo => Refuelable == null || Refuelable.IsFull;

        protected override string LoadedStatusText
        {
            get
            {
                if (Refuelable != null && !Refuelable.IsFull)
                {
                    return "MissingPartWithLabel".Translate(Refuelable.Props.FuelLabel);
                }
                return null;
            }
        }

        protected override ThingDef LaunchSkyfallerDef => Props.skyfaller;

        protected override ThingDef IncomingSkyfallerDef => Props.skyfallerIncoming;

        protected override WorldObjectDef TravelWorldObjectDef => Props.worldObjectDef;

        protected override int LaunchForwardCells => Props.launchForwardCells;

        protected override string GizmoLabel => "ScorerLaunch".Translate();

        protected override string GizmoDesc => "ScorerLaunchDesc".Translate();

        protected override string NoAmmoDisableReason
            => "MissingPartWithLabel".Translate(Refuelable?.Props?.FuelLabel ?? string.Empty);

        /// <summary>發射設定來自同一建築上的 CompMissileConfig（可為 null，落點按預設結算）。</summary>
        protected override MissileConfig BuildFiringConfig()
            => parent.GetComp<CompMissileConfig>()?.config?.Clone();

        /// <summary>消耗全部燃料（一發 = 滿燃料）。</summary>
        protected override void ConsumeAmmo()
        {
            CompRefuelable r = Refuelable;
            if (r != null)
            {
                r.ConsumeFuel(r.Fuel);
            }
        }
    }
}
