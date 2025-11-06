using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE.Scorer
{
    public class CompPropertiesLandFillable : CompProperties 
    {
        public CompPropertiesLandFillable() 
        {
            this.compClass = typeof(CompLandFillable);
        }

        public Texture2D Icon 
        {
            get 
            {
                if (this.icon == null) 
                {
                    this.icon = ContentFinder<Texture2D>.Get(this.iconPath);
                }
                return this.icon;
            }
        }

        public int tickToFill = 120;
        public ThingDef filled;
        Texture2D icon;
        public string iconPath = "a";
        public string landfillText = "Landfill";
        public JobDef job;
    }
    public class CompLandFillable : ThingComp
    {
        public CompPropertiesLandFillable Props => (CompPropertiesLandFillable)this.props;
        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn selPawn)
        {
            if (this.startLandfill && selPawn.CanReserveAndReach(this.parent,PathEndMode.Touch,Danger.Deadly)) 
            {
                yield return new FloatMenuOption(this.Props.landfillText.Translate(),() => 
                selPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(this.Props.job,this.parent)));
            }
            yield break;
        }
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Action()
            {
                defaultLabel = this.startLandfill ? "CancelLandfill" : "Landfill",
                defaultDesc = this.startLandfill ? "CancelLandfillDesc" : "LandfillDesc",
                icon = this.Props.Icon,
                action = () => this.startLandfill = !this.startLandfill
            };
            yield break;
        }
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref this.startLandfill, "startLandfill");
        }

        public bool startLandfill;
    }
}
