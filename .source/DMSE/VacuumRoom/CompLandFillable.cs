using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE
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
        /// <summary>按鈕與右鍵選單的翻譯鍵；說明文字用同名鍵加上 "Desc"。</summary>
        public string landfillText = "DMSE.Landfill.Fill";
        public JobDef job;
    }
    public class CompLandFillable : ThingComp
    {
        public CompPropertiesLandFillable Props => (CompPropertiesLandFillable)this.props;

        /// <summary>
        /// 舊版填埋時沒有設定陣營，填埋入口是無主建築且 claimable=false，玩家只能用上帝模式拆除。
        /// 新生成的會帶陣營（見 <see cref="JobDriver_LandFill"/>）；這裡讓存檔裡已經無主的那些也能拆。
        /// </summary>
        public override bool CompForceDeconstructable()
        {
            return this.parent.Faction == null && this.parent.def.building?.IsDeconstructible == true;
        }

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
                defaultLabel = this.startLandfill ? "DMSE.Landfill.Cancel".Translate() : this.Props.landfillText.Translate(),
                defaultDesc = this.startLandfill ? "DMSE.Landfill.CancelDesc".Translate() : (this.Props.landfillText + "Desc").Translate(),
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
