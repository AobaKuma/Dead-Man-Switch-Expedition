using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace DMSE
{
    public class CompPropertiesScorer : CompProperties
    {
        public CompPropertiesScorer()
        {
            this.compClass = typeof(CompScorer);
        }
        public ThingDef skyfaller;
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
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            Command_Action command = new Command_Action()
            {
                defaultLabel = "ScorerLaunch".Translate(),
                defaultDesc = "ScorerLaunchDesc".Translate(),
                icon = CompLaunchable.LaunchCommandTex,
                action = () => 
                {
                    CameraJumper.TryJump(CameraJumper.GetWorldTarget(this.parent), CameraJumper.MovementMode.Pan);
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
                    }, true, Props.worldObjectDef.ExpandingIconTexture,true,null,null,null,null,true);
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
            ScorerProjectile faller = (ScorerProjectile)SkyfallerMaker.SpawnSkyfaller(this.Props.skyfaller,
this.parent.Position, this.parent.Map);
            faller.Rotation = this.parent.Rotation;
            faller.angle = faller.Rotation.AsAngle;
            ScorerProjectile_WorldObject wo
            = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(Props.worldObjectDef);
            wo.SetFaction(Faction.OfPlayer);
            wo.Tile = this.parent.Map.Tile;
            wo.destinationTile = t.Tile;
            faller.worldObject = wo;
            if (Find.World.worldObjects.WorldObjectAt<WorldObject>(t.Tile) is WorldObject target
            && target.Faction is Faction f && !f.IsPlayer && f.RelationKindWith(Faction.OfPlayer)
            != FactionRelationKind.Hostile)
            {
                f.TryAffectGoodwillWith(Faction.OfPlayer,
                    f.GoodwillToMakeHostile(Faction.OfPlayer), true);
            }
            this.Refuelable.ConsumeFuel(this.Refuelable.Fuel);
        }


        public CompRefuelable refuelable;
    }
}
