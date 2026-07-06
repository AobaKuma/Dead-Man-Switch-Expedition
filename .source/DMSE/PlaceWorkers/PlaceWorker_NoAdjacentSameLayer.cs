using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 禁止在建築物佔地向外擴張1格的範圍內，放置任何 altitudeLayer 與其相同的建築。
    /// 用於避免同層建築彼此貼靠（例如需要保留維護/散熱/視野間距的設施）。
    /// </summary>
    public class PlaceWorker_NoAdjacentSameLayer : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            ThingDef thingDef = checkingDef as ThingDef;
            if (thingDef == null || map == null)
            {
                return true;
            }

            CellRect occupiedRect = GenAdj.OccupiedRect(loc, rot, thingDef.size);
            CellRect checkRect = occupiedRect.ExpandedBy(1);

            foreach (IntVec3 cell in checkRect)
            {
                // 只檢查建築本身佔地以外、向外1格範圍內的格子
                if (occupiedRect.Contains(cell) || !cell.InBounds(map))
                {
                    continue;
                }

                List<Thing> thingList = cell.GetThingList(map);
                for (int i = 0; i < thingList.Count; i++)
                {
                    Thing t = thingList[i];
                    if (t == thingToIgnore || t == thing)
                    {
                        continue;
                    }
                    if (t.def.category != ThingCategory.Building)
                    {
                        continue;
                    }
                    if (t.def.altitudeLayer == thingDef.altitudeLayer)
                    {
                        return new AcceptanceReport("DMSE.PlaceWorker.TooCloseSameLayer".Translate(t.LabelCap));
                    }
                }
            }

            return true;
        }
    }
}
