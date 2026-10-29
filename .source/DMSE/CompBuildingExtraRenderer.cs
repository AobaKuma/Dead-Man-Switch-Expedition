using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompBuildingExtraRenderer : ThingComp
    {
        public CompProperties_BuildingExtraRenderer Props => (CompProperties_BuildingExtraRenderer)props;

        private bool DrawInRealtime => parent != null && parent.def.drawerType == DrawerType.RealtimeOnly;

        public override void PostPrintOnto(SectionLayer layer)
        {
            base.PostPrintOnto(layer);
            if (DrawInRealtime) { return; }
            ExtraGraphic.ForEach(g => g.Print(layer, parent, 0f));
        }

        public override void PostDraw()
        {
            base.PostDraw();
            if (!DrawInRealtime) { return; }

            Vector3 drawPos = parent.DrawPos;
            foreach (Graphic graphic in ExtraGraphic)
            {
                graphic.Draw(drawPos, parent.Rotation, parent);
            }
        }
        public override void Notify_DefsHotReloaded()
        {
            base.Notify_DefsHotReloaded();
            extraGraphic = null;
        }
        public List<Graphic> ExtraGraphic
        {
            get
            {
                if (extraGraphic == null)
                {
                    extraGraphic = new List<Graphic>();
                    foreach (var gd in Props.extraGraphic)
                    {
                        extraGraphic.Add(gd.GraphicColoredFor(parent));
                    }
                }
                return extraGraphic;
            }
        }

        private List<Graphic> extraGraphic;
    }
    public class CompProperties_BuildingExtraRenderer : CompProperties
    {
        public List<GraphicData> extraGraphic;
        public CompProperties_BuildingExtraRenderer()
        {
            compClass = typeof(CompBuildingExtraRenderer);
        }
    }
}
