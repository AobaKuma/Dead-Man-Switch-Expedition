using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using Verse.AI;

namespace DMSE.Scorer
{
    public class JobDriver_LandFill : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return this.pawn.Reserve(this.TargetA,this.job);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.Goto(TargetIndex.A,PathEndMode.Touch);
            CompLandFillable comp = this.TargetThingA.TryGetComp<CompLandFillable>();
            yield return Toils_General.WaitWith(TargetIndex.A,comp.Props.tickToFill,true,false,
                false,TargetIndex.A);
            yield return new Toil() 
            {
                initAction = () => 
                {
                    Map map = this.TargetA.Thing.Map;
                    IntVec3 pos = this.TargetA.Cell;
                    if (this.TargetA.Thing is MapPortal portal) 
                    { 
                        PocketMapUtility.DestroyPocketMap(portal.PocketMap);
                    }
                    this.TargetThingA.Destroy();
                    GenSpawn.Spawn(comp.Props.filled,pos,map);
                }
            };
            yield break;
        }
    }
}
