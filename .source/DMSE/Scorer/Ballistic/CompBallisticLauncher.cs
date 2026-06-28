using System.Collections.Generic;
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
            compClass = typeof(CompBallisticLauncher);
        }
    }

    /// <summary>
    /// 彈道導彈發射架核心元件。
    ///
    /// 工作流程：
    ///   1. 玩家於 <see cref="ITab_MissileAssembly"/> 設定 pending 設定（選擇彈頭/導引/酬載）。
    ///   2. <see cref="WorkGiver_AssembleBallisticSilo"/> 指派小人搬運資源並執行
    ///      <see cref="JobDriver_AssembleBallisticSilo"/>。
    ///   3. 裝配完成後 <see cref="MarkLoaded"/> 被呼叫，<see cref="IsLoaded"/> 變為 true。
    ///   4. 玩家透過 Gizmo 在世界地圖選靶並發射；發射後重置為未裝填。
    ///
    /// 與 <see cref="CompMissileRailLauncher"/> 的差異：
    ///   - 彈藥來源為同一建築上的 <see cref="CompMissileConfig"/>（無物品型態）。
    ///   - 以 <c>isAssembled</c> 旗標而非容器物品計數判斷「已裝填」。
    /// </summary>
    public class CompBallisticLauncher : ThingComp
    {
        private bool isAssembled;
        private int cooldownUntil;

        public CompProperties_BallisticLauncher Props => (CompProperties_BallisticLauncher)props;

        /// <summary>是否已完成裝配、可以發射。</summary>
        public bool IsLoaded => isAssembled;

        /// <summary>裝配作業完成後由 <see cref="JobDriver_AssembleBallisticSilo"/> 呼叫。</summary>
        public void MarkLoaded() { isAssembled = true; }

        private CompMissileConfig MissileCfg => parent?.TryGetComp<CompMissileConfig>();

        private MissileConfig GetFiringConfig() => MissileCfg?.config?.Clone();

        private int LoadedRange
        {
            get
            {
                MissileConfig c = GetFiringConfig();
                return c != null ? c.Range : 0;
            }
        }

        private int DistanceToTile(GlobalTargetInfo t)
            => (int)Find.WorldGrid.ApproxDistanceInTiles(parent.Map.Tile, t.Tile);

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            Command_Action command = new Command_Action
            {
                defaultLabel = "DMSE.Ballistic.Fire".Translate(),
                defaultDesc = "DMSE.Ballistic.FireDesc".Translate(),
                icon = CompLaunchable.LaunchCommandTex,
                action = () =>
                {
                    CameraJumper.TryJump(CameraJumper.GetWorldTarget(parent), CameraJumper.MovementMode.Pan);
                    Find.WorldTargeter.BeginTargeting(t =>
                    {
                        if (t.Tile.Tile.PrimaryBiome.isWaterBiome) { return false; }
                        int range = LoadedRange;
                        if (range > 0 && DistanceToTile(t) > range)
                        {
                            Messages.Message(
                                "DMSE.Missile.OutOfRange".Translate(range),
                                MessageTypeDefOf.RejectInput,
                                false);
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

            if (!isAssembled)
            {
                command.Disable("DMSE.Ballistic.NotLoaded".Translate());
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
            if (Props.skyfaller == null || Props.worldObjectDef == null) { return; }

            MissileConfig config = GetFiringConfig();
            if (config == null) { return; }

            // 計算生成位置（垂直發射架通常在中心格正上方）。
            IntVec3 spawnPos = parent.Position;
            if (Props.launchForwardCells > 0)
            {
                spawnPos += parent.Rotation.AsIntVec3 * Props.launchForwardCells;
            }

            ScorerProjectile faller = (ScorerProjectile)SkyfallerMaker.SpawnSkyfaller(
                Props.skyfaller,
                spawnPos,
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

            // 重置為未裝填，並清除 config（保留 body），使 config ≠ pending
            // → NeedsAssembly = true → 殖民者下次必須重新搬運資源並執行製造工作。
            // pending 保留：玩家不必重新選擇彈頭/導引/酬載，只需等待裝填。
            isAssembled = false;
            cooldownUntil = Find.TickManager.TicksGame + Props.launchCooldownTicks;

            CompMissileConfig cfg = MissileCfg;
            if (cfg != null)
            {
                // 保留 body 但清空部件 → config ≠ pending（pending 仍有選好的部件）
                cfg.config = new MissileConfig { body = cfg.pending?.body ?? cfg.config.body };
                // 清除已搬運資源（彈體已發射消耗）
                cfg.delivered.Clear();
            }
        }

        public override string CompInspectStringExtra()
        {
            if (isAssembled)
            {
                MissileConfig c = GetFiringConfig();
                if (c?.body != null)
                {
                    return "DMSE.Ballistic.LoadedWith".Translate(c.body.LabelCap);
                }
                return "DMSE.Ballistic.Loaded".Translate();
            }
            int cooldownLeft = cooldownUntil - Find.TickManager.TicksGame;
            if (cooldownLeft > 0)
            {
                return "DMSE.Missile.LaunchCooldown".Translate(cooldownLeft.ToStringTicksToPeriod());
            }
            CompMissileConfig cfg = MissileCfg;
            if (cfg != null && cfg.NeedsAssembly)
            {
                return "DMSE.Ballistic.AwaitingAssembly".Translate();
            }
            return "DMSE.Ballistic.Unloaded".Translate();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref isAssembled, "ballisticIsAssembled", false);
            Scribe_Values.Look(ref cooldownUntil, "ballisticCooldownUntil", 0);
        }
    }
}
