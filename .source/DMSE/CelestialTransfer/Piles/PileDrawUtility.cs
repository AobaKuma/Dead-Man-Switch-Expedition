using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 結構網路的地面上層疊加顯示（設計稿 §3-7）。地板下的鋼樑由 <see cref="MapDrawLayer_StructuralBeams"/> 負責。
    /// </summary>
    public static class PileDrawUtility
    {
        /// <summary>
        /// 選取任一樁時：footprint 邊界、已完工連結、爭議格（橘）、將被摧毀的跨界建築（紅）、附近未連結的樁（黃）。
        /// 同一網路同一幀只畫一次（多選同網路的樁時避免重複）。
        /// </summary>
        public static void DrawNetworkOverlay(PileNetwork net)
        {
            if (net.lastOverlayFrame == Time.frameCount)
            {
                return;
            }
            net.lastOverlayFrame = Time.frameCount;

            if (net.FootprintCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(net.FootprintCells, PileResources.FootprintColor);
            }
            if (net.ContestedFootprintCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(net.ContestedFootprintCells, PileResources.ContestedColor);
            }
            if (net.TornApartCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(net.TornApartCells, PileResources.TornApartColor);
            }
            if (net.nearbyUnlinkedPileCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(net.nearbyUnlinkedPileCells, PileResources.UnlinkedPileColor);
            }

            float alt = PileResources.OverlayAltitude;
            for (int i = 0; i < net.links.Count; i++)
            {
                PileNetwork.Link link = net.links[i];
                GenDraw.DrawLineBetween(link.a.parent.TrueCenter().WithY(alt), link.b.parent.TrueCenter().WithY(alt), PileResources.LinkLineMat, 0.3f);
            }
        }

        private static readonly List<IntVec3> tmpExhaustCells = new List<IntVec3>();

        /// <summary>選取控制台或開啟「顯示船體範圍」時：網路疊加層 + 船上每具轉移引擎的尾焰區（可用者橘、不可用者紅）。</summary>
        public static void DrawShipOverlay(PileNetwork net)
        {
            DrawNetworkOverlay(net);
            if (net.lastShipOverlayFrame == Time.frameCount)
            {
                return;
            }
            net.lastShipOverlayFrame = Time.frameCount;
            List<TransferShipSurvey.EngineEntry> engines = net.Survey.engines;
            for (int i = 0; i < engines.Count; i++)
            {
                engines[i].engine.GetExclusionZone(tmpExhaustCells);
                GenDraw.DrawFieldEdges(tmpExhaustCells, engines[i].Operational ? PileResources.ExhaustZoneColor : PileResources.TornApartColor);
            }
        }

        /// <summary>施工中/待拆除的鋼樑常駐顯示；架設單另以較亮的線段畫出施工進度。</summary>
        public static void DrawPendingOrder(CompStructuralPile pile, PileBeamOrder order)
        {
            Thing target = order.target;
            if (target == null || !target.Spawned || target.Map != pile.parent.Map)
            {
                return;
            }
            float alt = PileResources.OverlayAltitude;
            Vector3 from = pile.parent.TrueCenter().WithY(alt);
            Vector3 to = target.TrueCenter().WithY(alt);
            if (order.dismantle)
            {
                GenDraw.DrawLineBetween(from, to, PileResources.PendingDismantleLineMat, 0.35f);
                return;
            }
            GenDraw.DrawLineBetween(from, to, PileResources.PendingBuildLineMat, 0.35f);
            if (order.workDone > 0f)
            {
                float progress = Mathf.Clamp01(order.workDone / pile.WorkOf(order));
                GenDraw.DrawLineBetween(from, Vector3.Lerp(from, to, progress), PileResources.BuildProgressLineMat, 0.5f);
            }
        }
    }
}
