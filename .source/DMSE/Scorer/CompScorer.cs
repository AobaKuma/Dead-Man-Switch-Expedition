using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
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
                        Launch(t);
                        return true;
                    },
                    canTargetTiles: true,
                    mouseAttachment: Props.worldObjectDef.ExpandingIconTexture,
                    closeWorldTabWhenFinished: true,
                    onUpdate: null,
                    extraLabelGetter: null,
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
        private void StartLaunchSequence()
        {

        }
        private void Launch(GlobalTargetInfo t)
        {
            ScorerProjectile faller = (ScorerProjectile)SkyfallerMaker.SpawnSkyfaller(Props.skyfaller, parent.Position + (parent.Rotation.AsIntVec3 * 2), parent.Map);
            faller.Rotation = parent.Rotation;
            faller.angle = faller.Rotation.AsAngle;
            ScorerProjectile_WorldObject wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(Props.worldObjectDef);
            wo.SetFaction(Faction.OfPlayer);
            wo.Tile = this.parent.Map.Tile;
            wo.destinationTile = t.Tile;
            faller.worldObject = wo;
            this.Refuelable.ConsumeFuel(this.Refuelable.Fuel);
        }
        public CompRefuelable refuelable;
    }
}
