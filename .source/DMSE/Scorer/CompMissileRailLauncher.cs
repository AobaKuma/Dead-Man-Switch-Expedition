using System.Collections.Generic;
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
            compClass = typeof(CompMissileRailLauncher);
        }
    }

    /// <summary>
    /// 發射軌類發射裝置（巡飛彈用）。彈藥以實體 Thing 存於 parent 的容器
    /// （<see cref="Building_MissileRack"/>），由 <see cref="CompMissileRackRenderer"/> 依建築朝向渲染；
    /// 發射時取出該 Thing，以 Scorer 方式（世界選靶→飛行→落點）打擊地圖外目標。
    ///
    /// 與 <see cref="CompScorer"/> 的差異：彈藥來源是容器中的實體導彈，而非 CompRefuelable 的抽象燃料。
    /// </summary>
    public class CompMissileRailLauncher : ThingComp
    {
        private int cooldownUntil;

        public CompProperties_MissileRailLauncher Props => (CompProperties_MissileRailLauncher)props;

        private ThingOwner HeldContainer => (parent as IThingHolder)?.GetDirectlyHeldThings();

        private Thing LoadedMissile
        {
            get
            {
                ThingOwner owner = HeldContainer;
                return owner != null && owner.Count > 0 ? owner[0] : null;
            }
        }

        /// <summary>建立發射設定：優先採用導彈物品自身的 CompMissileConfig，否則由 Props.missileBody 生成。</summary>
        private MissileConfig BuildConfig(Thing missile)
        {
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

        /// <summary>已裝填導彈的射程（世界 tile）；0 = 不限。</summary>
        private int LoadedRange
        {
            get
            {
                MissileConfig c = BuildConfig(LoadedMissile);
                return c != null ? c.Range : 0;
            }
        }

        private int DistanceToTile(GlobalTargetInfo t)
        {
            return (int)Find.WorldGrid.ApproxDistanceInTiles(parent.Map.Tile, t.Tile);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            Command_Action command = new Command_Action
            {
                defaultLabel = "ScorerLaunch".Translate(),
                defaultDesc = "ScorerLaunchDesc".Translate(),
                icon = CompLaunchable.LaunchCommandTex,
                action = () =>
                {
                    CameraJumper.TryJump(CameraJumper.GetWorldTarget(parent), CameraJumper.MovementMode.Pan);
                    Find.WorldTargeter.BeginTargeting(t =>
                    {
                        if (t.Tile.Tile.PrimaryBiome.isWaterBiome ||
                            (Find.World.worldObjects.WorldObjectAt<WorldObject>(t.Tile) is WorldObject wo && !(wo is MapParent)))
                        {
                            return false;
                        }
                        int range = LoadedRange;
                        if (range > 0 && DistanceToTile(t) > range)
                        {
                            Messages.Message("DMSE.Missile.OutOfRange".Translate(range), MessageTypeDefOf.RejectInput, false);
                            return false;
                        }
                        Launch(t);
                        return true;
                    },
                    canTargetTiles: true,
                    mouseAttachment: Props.worldObjectDef?.ExpandingIconTexture,
                    closeWorldTabWhenFinished: true,
                    onUpdate: null,
                    extraLabelGetter: t =>
                    {
                        int range = LoadedRange;
                        if (range <= 0) { return string.Empty; }
                        return "DMSE.Missile.RangeLabel".Translate(DistanceToTile(t), range);
                    },
                    canSelectTarget: null,
                    originForClosest: null,
                    showCancelButton: true);
                }
            };

            if (LoadedMissile == null)
            {
                command.Disable("DMSE.Missile.NoMissileLoaded".Translate());
            }
            int cooldownLeft = cooldownUntil - Find.TickManager.TicksGame;
            if (cooldownLeft > 0)
            {
                command.Disable("DMSE.Missile.LaunchCooldown".Translate(cooldownLeft.ToStringTicksToPeriod()));
            }

            yield return command;
        }

        private void Launch(GlobalTargetInfo t)
        {
            Thing missile = LoadedMissile;
            if (missile == null || Props.skyfaller == null || Props.worldObjectDef == null)
            {
                return;
            }

            MissileConfig config = BuildConfig(missile);

            ScorerProjectile faller = (ScorerProjectile)SkyfallerMaker.SpawnSkyfaller(
                Props.skyfaller,
                parent.Position + (parent.Rotation.AsIntVec3 * Props.launchForwardCells),
                parent.Map);
            faller.Rotation = parent.Rotation;
            faller.angle = faller.Rotation.AsAngle;

            ScorerProjectile_WorldObject wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(Props.worldObjectDef);
            wo.skyfallerIncoming = Props.skyfallerIncoming;
            wo.SetFaction(Faction.OfPlayer);
            wo.Tile = parent.Map.Tile;
            wo.destinationTile = t.Tile;
            wo.config = config;
            faller.worldObject = wo;

            // 消耗實體彈藥。
            HeldContainer?.Remove(missile);
            missile.Destroy(DestroyMode.Vanish);

            cooldownUntil = Find.TickManager.TicksGame + Props.launchCooldownTicks;
        }

        public override string CompInspectStringExtra()
        {
            int cooldownLeft = cooldownUntil - Find.TickManager.TicksGame;
            if (cooldownLeft > 0)
            {
                return "DMSE.Missile.LaunchCooldown".Translate(cooldownLeft.ToStringTicksToPeriod());
            }
            return null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, "railLaunchCooldownUntil", 0);
        }
    }
}
