using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public class CompPropertiesScorer : CompProperties
    {
        public CompPropertiesScorer() => this.compClass = typeof(CompScorer);
        public ThingDef skyfaller;
        public ThingDef skyfallerIncoming;
        public WorldObjectDef worldObjectDef;
    }
    public class CompScorer : ThingComp
    {
        public CompPropertiesScorer Props => (CompPropertiesScorer)this.props;
        CompRefuelable Refuelable 
        {
            get 
            {
                if(refuelable == null) this.refuelable = this.parent.GetComp<CompRefuelable>();
                return refuelable;
            }
        }
        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
        }
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            Command_Action command = new Command_Action()
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
                        (Find.World.worldObjects.WorldObjectAt<WorldObject>(t.Tile) is WorldObject wo
                        && !(wo is MapParent)))
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
                    mouseAttachment: Props.worldObjectDef.ExpandingIconTexture,
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
            if (this.Refuelable != null && !this.Refuelable.IsFull) 
            {
                command.Disable("MissingPartWithLabel".Translate(this.Refuelable.Props.FuelLabel));
            }
            yield return command;
            yield break;
        }
        private void Launch(GlobalTargetInfo t)
        {
            ScorerProjectile faller = (ScorerProjectile)SkyfallerMaker.SpawnSkyfaller(Props.skyfaller, parent.Position + (parent.Rotation.AsIntVec3 * 2), parent.Map);
            faller.Rotation = parent.Rotation;
            faller.angle = faller.Rotation.AsAngle;
            ScorerProjectile_WorldObject wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(Props.worldObjectDef);
            wo.skyfallerIncoming = Props.skyfallerIncoming;
            wo.SetFaction(Faction.OfPlayer);
            wo.Tile = this.parent.Map.Tile;
            wo.destinationTile = t.Tile;
            CompMissileConfig cfg = parent.GetComp<CompMissileConfig>();
            if (cfg != null && cfg.config != null)
            {
                wo.config = cfg.config.Clone();
            }
            faller.worldObject = wo;
            this.Refuelable.ConsumeFuel(this.Refuelable.Fuel);
        }

        /// <summary>已裝填導彈設定算出的射程（世界 tile）；0 = 不限。</summary>
        private int LoadedRange
        {
            get
            {
                CompMissileConfig cfg = parent.GetComp<CompMissileConfig>();
                return cfg != null && cfg.config != null ? cfg.config.Range : 0;
            }
        }

        private int DistanceToTile(GlobalTargetInfo t)
        {
            return (int)Find.WorldGrid.ApproxDistanceInTiles(parent.Map.Tile, t.Tile);
        }

        public CompRefuelable refuelable;
    }
}
