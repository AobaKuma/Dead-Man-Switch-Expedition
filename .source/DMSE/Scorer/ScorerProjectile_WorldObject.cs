using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class ScorerProjectile_WorldObject : WorldObject
    {
        public ThingDef skyfallerIncoming;
        // PlanetTile.Valid ⟺ tileId >= 0。在 PostAdd() 執行前或存讀失敗時，
        // initialTile/destinationTile 可能仍為 PlanetTile.Invalid（-1），
        // 直接傳入 GetTileCenter 會觸發越界錯誤，因此加 Valid 防衛。
        private Vector3 Start
        {
            get
            {
                PlanetTile t = initialTile.Valid ? initialTile : Tile;
                return t.Valid ? Find.WorldGrid.GetTileCenter(t) : Vector3.zero;
            }
        }
        private Vector3 End
        {
            get
            {
                return destinationTile.Valid
                    ? Find.WorldGrid.GetTileCenter(destinationTile)
                    : Vector3.zero;
            }
        }
        public override Vector3 DrawPos
        {
            get
            {
                return Vector3.Slerp(Start, End, traveledPct);
            }
        }
        public override bool ExpandingIconFlipHorizontal
        {
            get
            {
                return GenWorldUI.WorldToUIPosition(Start).x > GenWorldUI.WorldToUIPosition(End).x;
            }
        }
        public override Color ExpandingIconColor
        {
            get
            {
                return base.Faction.Color;
            }
        }
        public override float ExpandingIconRotation
        {
            get
            {
                if (!this.def.rotateGraphicWhenTraveling)
                {
                    return base.ExpandingIconRotation;
                }
                Vector2 vector = GenWorldUI.WorldToUIPosition(this.Start);
                Vector2 vector2 = GenWorldUI.WorldToUIPosition(this.End);
                float num = Mathf.Atan2(vector2.y - vector.y, vector2.x - vector.x) * 57.29578f;
                if (num > 180f)
                {
                    num -= 180f;
                }
                return num + 90f;
            }
        }
        private float TraveledPctStepPerTick
        {
            get
            {
                Vector3 start = this.Start;
                Vector3 end = this.End;
                if (start == end)
                {
                    return 1f;
                }
                float num = GenMath.SphericalDistance(start.normalized, end.normalized);
                if (num == 0f)
                {
                    return 1f;
                }
                float speedFactor = config != null ? config.WorldSpeedFactor : 1f;
                return (0.00025f * speedFactor) / num;
            }
        }
        public override void ExposeData()
        {
            base.ExposeData();
            // PlanetTile 是含兩個 int 欄位的 struct，Scribe_Values 無法可靠地序列化它。
            // 以 int 分別儲存 tileId，layerId 維持 Surface（0）即可。
            int destId = destinationTile.tileId;
            int initId = initialTile.tileId;
            Scribe_Values.Look(ref destId, "destinationTileId", -1);
            Scribe_Values.Look(ref initId, "initialTileId", -1);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                destinationTile = destId >= 0 ? new PlanetTile(destId) : PlanetTile.Invalid;
                initialTile     = initId >= 0 ? new PlanetTile(initId) : PlanetTile.Invalid;
            }
            Scribe_Values.Look(ref this.arrived, "arrived", false);
            Scribe_Values.Look(ref this.traveledPct, "traveledPct", 0f);
            Scribe_Values.Look(ref this.skyfallerIncoming, "skyfallerIncoming", null);
            Scribe_Deep.Look(ref this.config, "config");
        }
        public override void PostAdd()
        {
            base.PostAdd();
            this.initialTile = base.Tile;
        }
        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            this.traveledPct += TraveledPctStepPerTick * (float)delta;
            if (this.traveledPct >= 1f)
            {
                this.traveledPct = 1f;
                this.Arrived();
            }
        }
        private void Arrived()
        {
            if (arrived)
            {
                return;
            }
            arrived = true;
            if (Find.World.worldObjects.WorldObjectAt<WorldObject>(destinationTile) is WorldObject target &&
                target.Faction is Faction f &&
                !f.IsPlayer &&
                f.RelationKindWith(Faction.OfPlayer)
            != FactionRelationKind.Hostile)
            {
                f.TryAffectGoodwillWith(Faction.OfPlayer, f.GoodwillToMakeHostile(Faction.OfPlayer), true);
            }
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(destinationTile, WorldObjectDefOf.Camp);
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

        public MissileConfig config;
        public PlanetTile destinationTile = PlanetTile.Invalid;
        private bool arrived;
        private PlanetTile initialTile = PlanetTile.Invalid;
        private float traveledPct;

        private const string ProjectileDefName = "MeteoriteIncoming";
        //选中并生成的坠落物定义，用原版的陨石当占位符
    }
}