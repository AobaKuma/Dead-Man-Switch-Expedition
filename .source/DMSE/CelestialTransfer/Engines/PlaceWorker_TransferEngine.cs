using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移引擎放置（設計稿 §5-1）：畫出尾焰區並即時顯示是否受阻（受阻時紅色），放下後若受阻發出警告。
    /// 受阻不擋放置——玩家可能之後才清出尾焰區。
    /// </summary>
    public class PlaceWorker_TransferEngine : PlaceWorker
    {
        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            CompProperties_TransferEngine props = def.GetCompProperties<CompProperties_TransferEngine>();
            Map map = Find.CurrentMap;
            if (props == null || map == null)
            {
                return;
            }
            Thing blocker;
            bool blocked = CompTransferEngine.IsExhaustBlocked(def, map, center, rot, thing, out blocker);
            props.GetExclusionZone(center, rot, tmpCells);
            GenDraw.DrawFieldEdges(tmpCells, blocked ? PileResources.TornApartColor : PileResources.ExhaustZoneColor);
            if (blocked && blocker != null)
            {
                GenDraw.DrawFieldEdges(new List<IntVec3>(blocker.OccupiedRect().Cells), PileResources.TornApartColor);
            }
        }

        public override void PostPlace(Map map, BuildableDef def, IntVec3 loc, Rot4 rot)
        {
            ThingDef thingDef = def as ThingDef;
            Thing blocker;
            if (thingDef != null && CompTransferEngine.IsExhaustBlocked(thingDef, map, loc, rot, null, out blocker))
            {
                Messages.Message("DMSE.Transfer.Engine.PlacedBlocked".Translate(def.LabelCap, blocker.LabelShort), MessageTypeDefOf.CautionInput, historical: false);
            }
        }
    }
}
