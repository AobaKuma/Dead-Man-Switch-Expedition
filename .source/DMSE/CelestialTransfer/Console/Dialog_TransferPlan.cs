using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移計畫確認視窗（設計稿 §4-2 步驟 2，取代 Dialog_SelectFlightMode）。
    /// 顯示質量、推力、TWR、燃料需求、點火與滑行時間、ETA 與整備檢查；有 Error 時無法確認。
    /// 撞擊飛行（§9）共用此視窗，另加紅字警告與結局等級。
    /// </summary>
    public class Dialog_TransferPlan : Window
    {
        private static readonly Color ImpactColor = new Color(1f, 0.35f, 0.35f);

        private readonly CompTransferConsole console;

        private readonly TransferPlan plan;

        private readonly List<TransferIssue> issues;

        private Vector2 scrollPos;

        private float lastHeight;

        public override Vector2 InitialSize => new Vector2(520f, 640f);

        public Dialog_TransferPlan(CompTransferConsole console, TransferPlan plan)
        {
            this.console = console;
            this.plan = plan;
            issues = TransferReadiness.Check(console, plan);
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            const float ButtonHeight = 38f;
            Rect contentRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - ButtonHeight - 10f);
            GlobalTargetInfo pendingJump = GlobalTargetInfo.Invalid;

            Rect viewRect = new Rect(0f, 0f, contentRect.width - 16f, Mathf.Max(lastHeight, contentRect.height));
            Widgets.BeginScrollView(contentRect, ref scrollPos, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(0f, 0f, viewRect.width, 99999f));

            TransferShipSurvey survey = console.Survey;
            if (plan.isImpact)
            {
                TransferUIUtility.Header(listing, "DMSE.Transfer.Dialog.ImpactTitle".Translate());
                GUI.color = ImpactColor;
                listing.Label("DMSE.Transfer.Dialog.ImpactWarning".Translate());
                if (survey != null && TransferImpactUtility.IsPlanetkiller(survey.impactEngines))
                {
                    listing.Label("DMSE.Transfer.Dialog.ImpactPlanetkiller".Translate(survey.impactEngines));
                }
                GUI.color = Color.white;
                listing.Label("DMSE.Transfer.Dialog.ImpactRoute".Translate(plan.destination.ToString(), plan.distance));
            }
            else
            {
                TransferUIUtility.Header(listing, "DMSE.Transfer.Dialog.Title".Translate());
                listing.Label("DMSE.Transfer.Dialog.Route".Translate(plan.origin.ToString(), plan.destination.ToString(), plan.distance));
                if (plan.parkedAtNeighbor)
                {
                    listing.Label("DMSE.Transfer.UI.ParkedAtNeighbor".Translate());
                }
                MapParent site = plan.landOnSite ? Find.WorldObjects.MapParentAt(plan.destination) : null;
                if (site != null)
                {
                    listing.Label("DMSE.Transfer.UI.LandOnSite".Translate(site.Label));
                }
            }
            listing.GapLine();

            if (survey != null)
            {
                TransferUIUtility.DrawEstimate(listing, TransferEstimate.For(survey, plan.distance, plan.isImpact), survey.network.piles.Count);
                listing.GapLine();
            }

            TransferUIUtility.Header(listing, "DMSE.Transfer.UI.IssuesHeader".Translate());
            TransferUIUtility.DrawIssues(listing, issues, ref pendingJump);

            lastHeight = listing.CurHeight + 10f;
            listing.End();
            Widgets.EndScrollView();

            float buttonWidth = (inRect.width - 20f) / 3f;
            float y = inRect.yMax - ButtonHeight;
            if (Widgets.ButtonText(new Rect(inRect.x, y, buttonWidth, ButtonHeight), "DMSE.Transfer.Dialog.Reselect".Translate()))
            {
                Close();
                TransferPlanningUtility.Reselect(console, plan);
            }
            if (Widgets.ButtonText(new Rect(inRect.x + buttonWidth + 10f, y, buttonWidth, ButtonHeight), "CancelButton".Translate()))
            {
                Close();
                ReturnToMap();
            }
            bool blocked = TransferReadiness.AnyError(issues);
            Rect confirmRect = new Rect(inRect.x + (buttonWidth + 10f) * 2f, y, buttonWidth, ButtonHeight);
            string confirmLabel = (plan.isImpact ? "DMSE.Transfer.Dialog.ConfirmImpact" : "DMSE.Transfer.Dialog.Confirm").Translate();
            if (blocked)
            {
                GUI.color = Color.gray;
                Widgets.ButtonText(confirmRect, confirmLabel, active: false);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(confirmRect, "DMSE.Transfer.Dialog.ConfirmBlocked".Translate());
            }
            else
            {
                if (plan.isImpact)
                {
                    GUI.color = ImpactColor;
                }
                bool clicked = Widgets.ButtonText(confirmRect, confirmLabel);
                GUI.color = Color.white;
                if (clicked)
                {
                    console.SetPlan(plan);
                    Messages.Message("DMSE.Transfer.Dialog.PlanSet".Translate(), console.parent, MessageTypeDefOf.TaskCompletion, historical: false);
                    Close();
                    ReturnToMap();
                }
            }

            if (pendingJump.IsValid)
            {
                Close();
                CameraJumper.TryJumpAndSelect(pendingJump);
            }
        }

        private void ReturnToMap()
        {
            if (console.parent.Spawned)
            {
                CameraJumper.TryJump(console.parent);
            }
        }
    }
}
