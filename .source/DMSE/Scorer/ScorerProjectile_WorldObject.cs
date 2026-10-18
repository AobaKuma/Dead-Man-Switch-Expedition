using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 玩家發射的導彈世界航跡。飛行、存檔與終端更新見 <see cref="MissileWorldObject"/>；
    /// 抵達目標地圖後暫停遊戲，由玩家在當地選擇落點。
    /// </summary>
    public class ScorerProjectile_WorldObject : MissileWorldObject
    {
        public ThingDef skyfallerIncoming;

        // 選中並生成的墜落物定義，用原版的隕石當佔位符
        private const string ProjectileDefName = "MeteoriteIncoming";

        public override Color ExpandingIconColor => base.Faction.Color;

        // 打擊尚未生成地圖的據點時，生成其地圖供玩家選落點
        protected override bool AllowGenerateTargetMap => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref this.skyfallerIncoming, "skyfallerIncoming", null);

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

        protected override void OnArrived(Map map)
        {
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
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            if (Find.CurrentMap == map)
            {
                CameraJumper.TryJump(Find.CameraDriver.MapPosition, map, CameraJumper.MovementMode.Cut);
            }
            else
            {
                CameraJumper.TryJump(map.Center, map, CameraJumper.MovementMode.Cut);
            }

            ThingDef def = skyfallerIncoming ?? ThingDef.Named(ProjectileDefName);

            bool launch = false;
            Find.Targeter.BeginTargeting(
                new TargetingParameters()
                {
                    canTargetLocations = true,
                    canTargetItems = false
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

        /// <summary>生成落點導彈，並把裝配設定交給 MissileIncoming。</summary>
        private void SpawnIncoming(ThingDef def, IntVec3 cell, Map map)
        {
            Skyfaller faller = SkyfallerMaker.SpawnSkyfaller(def, cell, map);
            if (faller is MissileIncoming incoming)
            {
                incoming.config = config != null ? config.Clone() : null;
                incoming.attacker = base.Faction; // 玩家的巡航導彈，供敵方 BVR 攔截判定。
                MissileBVRUtility.TryRegister(incoming, cell);
            }
        }
    }
}
