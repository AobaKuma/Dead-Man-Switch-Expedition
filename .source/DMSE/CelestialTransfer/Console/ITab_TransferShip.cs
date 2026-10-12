using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移飛行控制台的檢視分頁（設計稿 §4-3）：網路、推進、燃料、計畫與整備檢查。
    /// 飛行中的階段資訊待 P5 加入。
    /// </summary>
    public class ITab_TransferShip : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(460f, 600f);

        private Vector2 scrollPos;

        private float lastHeight;

        private CompTransferConsole Console => SelThing?.TryGetComp<CompTransferConsole>();

        public override bool IsVisible => Console != null;

        public ITab_TransferShip()
        {
            size = WinSize;
            labelKey = "DMSE.Transfer.Tab";
        }

        protected override void FillTab()
        {
            CompTransferConsole console = Console;
            if (console == null)
            {
                return;
            }
            GlobalTargetInfo pendingJump = GlobalTargetInfo.Invalid;

            Rect outRect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(lastHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(0f, 0f, viewRect.width, 99999f));

            WorldObject_TransferShip flying = console.parent.MapHeld?.Parent as WorldObject_TransferShip;
            if (flying != null && flying.InFlight)
            {
                TransferUIUtility.Header(listing, "DMSE.Transfer.UI.FlightHeader".Translate(flying.Label));
                TransferUIUtility.Row(listing, "DMSE.Transfer.UI.Phase".Translate(), flying.CurrentPhaseLabel);
                TransferUIUtility.Row(listing, "DMSE.Transfer.UI.PhaseLeft".Translate(), flying.PhaseTicksLeft.ToStringTicksToPeriod());
                TransferUIUtility.Row(listing, "DMSE.Transfer.UI.FlightProgress".Translate(), flying.Progress.ToStringPercent());
                TransferUIUtility.Row(listing, "DMSE.Transfer.UI.Destination".Translate(), flying.destination.ToString());
                TransferUIUtility.Row(listing, (flying.isImpact ? "DMSE.Transfer.UI.ImpactIn" : "DMSE.Transfer.UI.ArrivalIn").Translate(), flying.TotalTicksLeft.ToStringTicksToPeriod());
                listing.GapLine();
            }

            PileNetwork net = console.Network;
            if (net == null)
            {
                GUI.color = ColorLibrary.RedReadable;
                listing.Label("DMSE.Transfer.Console.NotOnShip".Translate());
                GUI.color = Color.white;
            }
            else
            {
                TransferShipSurvey survey = net.Survey;
                DrawShipName(listing, console);
                DrawNetwork(listing, net, ref pendingJump);
                listing.GapLine();
                DrawPropulsion(listing, survey, ref pendingJump);
                listing.GapLine();
                DrawFuel(listing, survey, ref pendingJump);
                listing.GapLine();

                TransferPlan plan = console.State == TransferConsoleState.Idle ? null : console.plan;
                string planHeader;
                if (plan == null)
                {
                    planHeader = "DMSE.Transfer.UI.EstimateHeader".Translate();
                }
                else
                {
                    planHeader = (plan.isImpact ? "DMSE.Transfer.UI.ImpactPlanHeader" : "DMSE.Transfer.UI.PlanHeader").Translate(plan.destination.ToString());
                }
                TransferUIUtility.Header(listing, planHeader);
                if (plan != null && plan.parkedAtNeighbor)
                {
                    listing.Label("DMSE.Transfer.UI.ParkedAtNeighbor".Translate());
                }
                TransferUIUtility.DrawEstimate(listing, TransferEstimate.For(survey, plan != null ? plan.distance : -1, plan != null && plan.isImpact), net.piles.Count);
                listing.GapLine();

                TransferUIUtility.Header(listing, "DMSE.Transfer.UI.IssuesHeader".Translate());
                TransferUIUtility.DrawIssues(listing, console.Issues, ref pendingJump);
            }

            lastHeight = listing.CurHeight + 10f;
            listing.End();
            Widgets.EndScrollView();

            if (pendingJump.IsValid)
            {
                CameraJumper.TryJumpAndSelect(pendingJump);
            }
        }

        /// <summary>船名列：名稱 + 命名按鈕（尚未命名時顯示起飛時會自動命名）。</summary>
        private static void DrawShipName(Listing_Standard listing, CompTransferConsole console)
        {
            Text.Font = GameFont.Medium;
            Rect rect = listing.GetRect(Text.LineHeight + 4f);
            Rect btnRect = new Rect(rect.xMax - 24f, rect.y + (rect.height - 24f) / 2f, 24f, 24f);
            string name = console.ShipName;
            Rect labelRect = new Rect(rect.x, rect.y, rect.width - btnRect.width - 4f, rect.height);
            if (name.NullOrEmpty())
            {
                GUI.color = Color.gray;
                Widgets.Label(labelRect, "DMSE.Transfer.Console.Unnamed".Translate());
                GUI.color = Color.white;
            }
            else
            {
                Widgets.Label(labelRect, name.Truncate(labelRect.width));
            }
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(btnRect, "DMSE.Transfer.Console.Rename".Translate());
            if (Widgets.ButtonImage(btnRect, TexButton.Rename))
            {
                Find.WindowStack.Add(new Dialog_RenameTransferShip(console));
            }
            listing.Gap(4f);
        }

        private static void DrawNetwork(Listing_Standard listing, PileNetwork net, ref GlobalTargetInfo pendingJump)
        {
            TransferUIUtility.Header(listing, "DMSE.Transfer.UI.NetworkHeader".Translate());
            TransferUIUtility.Row(listing, "DMSE.Transfer.UI.Piles".Translate(), net.piles.Count.ToString());
            TransferUIUtility.Row(listing, "DMSE.Transfer.UI.Beams".Translate(), net.links.Count.ToString());
            TransferUIUtility.Row(listing, "DMSE.Transfer.UI.Footprint".Translate(), net.Footprint.Count.ToString());
            ClickableCount(listing, "DMSE.Transfer.UI.TornApart".Translate(), net.TornApartThings, ref pendingJump);
            TransferUIUtility.Row(listing, "DMSE.Transfer.UI.Contested".Translate(), net.ContestedFootprintCells.Count.ToString());
            List<Thing> unlinked = new List<Thing>();
            for (int i = 0; i < net.nearbyUnlinkedPiles.Count; i++)
            {
                unlinked.Add(net.nearbyUnlinkedPiles[i].parent);
            }
            ClickableCount(listing, "DMSE.Transfer.UI.UnlinkedPiles".Translate(), unlinked, ref pendingJump);
            List<Thing> ordered = new List<Thing>();
            for (int i = 0; i < net.piles.Count; i++)
            {
                if (net.piles[i].Orders.Count > 0)
                {
                    ordered.Add(net.piles[i].parent);
                }
            }
            ClickableCount(listing, "DMSE.Transfer.UI.PendingOrders".Translate(), ordered, ref pendingJump);
        }

        private static void DrawPropulsion(Listing_Standard listing, TransferShipSurvey survey, ref GlobalTargetInfo pendingJump)
        {
            TransferUIUtility.Header(listing, "DMSE.Transfer.UI.PropulsionHeader".Translate());
            if (survey.engines.Count == 0)
            {
                listing.Label("DMSE.Transfer.UI.NoEngines".Translate());
            }
            for (int i = 0; i < survey.engines.Count; i++)
            {
                TransferShipSurvey.EngineEntry e = survey.engines[i];
                string label = e.engine.parent.LabelCap + " (" + e.engine.Props.thrust.ToString("0.#") + ")";
                string status = CompTransferEngine.StatusLabel(e.status, e.blocker);
                if (ClickableRow(listing, label, status, e.Operational ? (Color?)null : ColorLibrary.RedReadable))
                {
                    pendingJump = e.engine.parent;
                }
            }
            TransferUIUtility.Row(listing, "DMSE.Transfer.UI.ReactorSupport".Translate(), survey.reactorDemand + " / " + survey.reactorCapacity,
                survey.reactorDemand > survey.reactorCapacity ? ColorLibrary.RedReadable : (Color?)null);
        }

        private static void DrawFuel(Listing_Standard listing, TransferShipSurvey survey, ref GlobalTargetInfo pendingJump)
        {
            TransferUIUtility.Header(listing, "DMSE.Transfer.UI.FuelHeader".Translate());
            if (survey.fuelSources.Count == 0)
            {
                listing.Label("DMSE.Transfer.UI.NoFuelSources".Translate());
            }
            for (int i = 0; i < survey.fuelSources.Count; i++)
            {
                ITransferFuelSource source = survey.fuelSources[i];
                if (ClickableRow(listing, source.Parent.LabelCap, source.Available.ToString("0"), null))
                {
                    pendingJump = source.Parent;
                }
            }
        }

        private static void ClickableCount(Listing_Standard listing, string label, List<Thing> things, ref GlobalTargetInfo pendingJump)
        {
            if (ClickableRow(listing, label, things.Count.ToString(), things.Count > 0 ? new Color(1f, 0.85f, 0.35f) : (Color?)null) && things.Count > 0)
            {
                pendingJump = things[0];
            }
        }

        private static bool ClickableRow(Listing_Standard listing, string label, string value, Color? valueColor)
        {
            Rect rect = listing.GetRect(Text.LineHeight);
            Widgets.DrawHighlightIfMouseover(rect);
            Widgets.Label(rect.LeftPart(0.6f), label);
            Color old = GUI.color;
            if (valueColor.HasValue)
            {
                GUI.color = valueColor.Value;
            }
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(rect.RightPart(0.4f), value);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = old;
            return Widgets.ButtonInvisible(rect);
        }
    }
}
