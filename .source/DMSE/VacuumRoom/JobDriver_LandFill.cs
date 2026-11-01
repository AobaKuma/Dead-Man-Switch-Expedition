using RimWorld;
using System;
using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace DMSE
{
    public class JobDriver_LandFill : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return this.pawn.Reserve(this.TargetA, this.job);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.Goto(TargetIndex.A, PathEndMode.Touch);
            CompLandFillable comp = this.TargetThingA.TryGetComp<CompLandFillable>();
            yield return Toils_General.WaitWith(TargetIndex.A, comp.Props.tickToFill, true, false,
                false, TargetIndex.A);
            yield return new Toil()
            {
                initAction = () =>
                {
                    Map map = this.TargetA.Thing.Map;
                    IntVec3 pos = this.TargetA.Cell;
                    // 替換後的建築沿用原陣營：無主建築（claimable=false）玩家拆不掉。舊存檔的無主入口改歸執行者陣營。
                    Faction faction = this.TargetThingA.Faction ?? this.pawn.Faction;
                    if (this.TargetA.Thing is MapPortal portal)
                    {
                        PocketMapUtility.DestroyPocketMap(portal.PocketMap);
                    }
                    this.TargetThingA.Destroy();
                    Thing replacement = ThingMaker.MakeThing(comp.Props.filled);
                    replacement.SetFaction(faction);
                    GenSpawn.Spawn(replacement, pos, map);
                }
            };
            yield break;
        }
    }
}