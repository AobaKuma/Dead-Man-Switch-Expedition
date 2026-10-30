using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 玩家發射的導彈世界航跡。飛行、存檔與終端更新見 <see cref="MissileWorldObject"/>。
    /// 抵達目標地圖後依導引頭決定落點：電視制導（<see cref="MissileConfig.PlayerChoosesTarget"/>）暫停遊戲由玩家選點，
    /// 其餘導引頭與慣性制導由 <see cref="GuidanceType.ArrivalCell"/> 自動鎖定；裝了盤旋飛控（<see cref="MissileConfig.LoiterTicks"/>）
    /// 而導引頭找不到目標時，改為在目標上空盤旋等待（<see cref="MapComponent_BVRCombat.RegisterLoiter"/>）。
    /// </summary>
    public class ScorerProjectile_WorldObject : MissileWorldObject
    {
        public ThingDef skyfallerIncoming;

        /// <summary>
        /// 發射建築：落地爆炸的傷害歸因（docs/Missile_Skyfaller轉Projectile_計畫.md §7）。
        /// 建築被拆除或摧毀後為 null（存檔不保留已摧毀的參照），落地爆炸改為無 instigator。
        /// </summary>
        public Thing launcherThing;

        /// <summary>升空彈實際生成的 tick（發射排程可能比發射當下晚幾個 tick；-1 = 未知，視同發射當下）。</summary>
        public int launchSpawnTick = -1;

        /// <summary>波次打擊的任務 id 與波次（0 起算）；-1 表示不是波次打擊發射的。</summary>
        public int strikeMissionId = -1;
        public int strikeWave = -1;

        // 選中並生成的墜落物定義，用原版的隕石當佔位符
        private const string ProjectileDefName = "MeteoriteIncoming";

        public override ThingDef IncomingDef => skyfallerIncoming ?? base.IncomingDef;

        // 打擊尚未生成地圖的據點時，生成其地圖供玩家選落點
        protected override bool AllowGenerateTargetMap => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref skyfallerIncoming, "skyfallerIncoming");
            if (Scribe.mode == LoadSaveMode.Saving && launcherThing != null && launcherThing.Destroyed) { launcherThing = null; }
            Scribe_References.Look(ref launcherThing, "launcherThing");
            Scribe_Values.Look(ref launchSpawnTick, "launchSpawnTick", -1);
            Scribe_Values.Look(ref strikeMissionId, "strikeMissionId", -1);
            Scribe_Values.Look(ref strikeWave, "strikeWave", -1);

            // 舊存檔只存了 tileId（圖層遺失），無從得知原圖層，只能視為地表
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (!destinationTile.Valid)
                {
                    int legacyDest = -1;
                    Scribe_Values.Look(ref legacyDest, "destinationTileId", -1);
                    if (legacyDest >= 0) { destinationTile = new PlanetTile(legacyDest); }
                }
                if (!initialTile.Valid)
                {
                    int legacyInit = -1;
                    Scribe_Values.Look(ref legacyInit, "initialTileId", -1);
                    if (legacyInit >= 0) { initialTile = new PlanetTile(legacyInit); }
                }
            }
        }

        protected override void OnIntercepted(Faction by)
        {
            WorldComponent_StrikeReports.Report(strikeMissionId, strikeWave, StrikeOutcome.Intercepted);
        }

        protected override void OnArrived(Map map)
        {
            WorldComponent_StrikeReports.Report(strikeMissionId, strikeWave, map == null ? StrikeOutcome.Missed : StrikeOutcome.Arrived);
            if (map == null)
            {
                Messages.Message("DMSE.Missile.Missed".Translate(), new GlobalTargetInfo(destinationTile),
                    MessageTypeDefOf.NeutralEvent, historical: false);
                Destroy();
                return;
            }

            if (map.Parent?.Faction is Faction f &&
                !f.IsPlayer &&
                f.RelationKindWith(Faction.OfPlayer) != FactionRelationKind.Hostile)
            {
                f.TryAffectGoodwillWith(Faction.OfPlayer, f.GoodwillToMakeHostile(Faction.OfPlayer), true);
            }
            Current.Game.GetComponent<GameComponent_MissileEngage>().times.SetOrAdd(map, Find.TickManager.TicksGame + (GenDate.TicksPerDay * 2));

            ThingDef def = skyfallerIncoming ?? ThingDef.Named(ProjectileDefName);

            // 只有電視制導由玩家手動選點；其餘導引頭（含慣性）依導引頭規則自動鎖定，不暫停遊戲。
            if (config == null || !config.PlayerChoosesTarget)
            {
                // 盤旋飛控：導引頭找不到目標時不直接回退慣性，改在目標上空盤旋等待。
                IntVec3 cell = GuidanceType.ArrivalCell(config, map, base.Faction, out bool loiter);
                bool loiterStarted = SpawnIncoming(def, cell, map, loiter);
                string message = loiterStarted
                    ? "DMSE.Missile.Loitering".Translate(config.LoiterTicks.ToStringTicksToPeriod()).ToString()
                    : "DMSE.Missile.SeekerLocked".Translate(config != null ? config.TargetingLabel : "DMSE.Guidance.Inertial".Translate().ToString()).ToString();
                Messages.Message(message, new TargetInfo(cell, map), MessageTypeDefOf.NeutralEvent, historical: false);
                Destroy();
                return;
            }

            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            if (Find.CurrentMap == map)
            {
                CameraJumper.TryJump(Find.CameraDriver.MapPosition, map, CameraJumper.MovementMode.Cut);
            }
            else
            {
                CameraJumper.TryJump(map.Center, map, CameraJumper.MovementMode.Cut);
            }

            bool launch = false;
            Find.Targeter.BeginTargeting(
                new TargetingParameters()
                {
                    canTargetLocations = true,
                    canTargetItems = false,
                    // 沒有穿甲彈頭／反碉堡酬載時，厚岩頂下的格子打不到（只會在岩層表面引爆）。
                    validator = t => config.PenetratesThickRoof || !MissileImpactUtility.UnderThickRoof(t.Cell, map)
                },
            action: t =>
                {
                    SpawnIncoming(def, t.Cell, map);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    launch = true;
                },
            null,
            null,
            null,
            actionWhenFinished: () =>
                {
                    if (!launch)
                    {
                        SpawnIncoming(def, map.Center, map);
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    }
                },
            null,
            true,
            t => Find.TickManager.CurTimeSpeed = TimeSpeed.Paused);
            Destroy();
        }

        /// <summary>生成落點導彈（從世界航跡最後一段的方向飛入地圖），並把裝配設定交給它。</summary>
        /// <param name="loiter">盤旋飛控：導引頭找不到目標時不落地，改在目標上空盤旋等待；由 <see cref="MissileBVRUtility.TryRegisterLoitering"/> 接手。</param>
        /// <returns>是否真的進入盤旋（<paramref name="loiter"/> 為 false 或無法交給 BVR 時為 false）。</returns>
        private bool SpawnIncoming(ThingDef def, IntVec3 cell, Map map, bool loiter = false)
        {
            Thing thing = MissileProjectileMaker.SpawnIncoming(def, cell, map, ArrivalHeading);
            if (thing is IIncomingMissile incoming)
            {
                incoming.Config = config != null ? config.Clone() : null;
                incoming.WorldPhaseDone = true;
                incoming.Attacker = base.Faction; // 玩家的巡航導彈，供敵方 BVR 攔截判定。
                incoming.StrikeMissionId = strikeMissionId;
                incoming.StrikeWave = strikeWave;
                if (thing is Projectile_MissileIncoming p)
                {
                    p.SetLauncher(launcherThing); // 傷害歸因於發射建築
                    p.ApplyLandingScatter();      // 命中偏移套在落點上，BVR 以偏移後的落點登記
                    cell = p.Position;
                }
                // 盤旋飛控：改由 BVR 波次接手，飛彈在上空持續尋找目標（期間仍可被終端制導攔截）。
                if (loiter && MissileBVRUtility.TryRegisterLoitering(thing, cell, incoming.Config)) { return true; }
                MissileBVRUtility.TryRegister(thing, cell);
            }
            return false;
        }
    }
}
