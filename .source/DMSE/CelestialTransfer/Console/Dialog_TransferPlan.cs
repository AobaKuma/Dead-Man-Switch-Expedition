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
            using (TransferText.Using(console.parent.MapHeld))
            {
                DoContents(inRect);
            }
        }

        private void DoContents(Rect inRect)
        {
            const float ButtonHeight = 38f;
            bool blocked = TransferReadiness.AnyError(issues);
            string blockedText = TransferText.Key("DMSE.Transfer.Dialog.ConfirmBlocked", console).Translate();
            float noticeHeight = blocked ? Text.CalcHeight(blockedText, inRect.width) + DMSEUI.Gap : 0f;
            Rect contentRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - ButtonHeight - DMSEUI.SectionGap - noticeHeight);
            GlobalTargetInfo pendingJump = GlobalTargetInfo.Invalid;

            Rect viewRect = new Rect(0f, 0f, contentRect.width - 16f, Mathf.Max(lastHeight, contentRect.height));
            Widgets.BeginScrollView(contentRect, ref scrollPos, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(0f, 0f, viewRect.width, 99999f));

            TransferShipSurvey survey = console.Survey;
            if (plan.isImpact)
            {
                TransferUIUtility.Header(listing, TransferText.Key("DMSE.Transfer.Dialog.ImpactTitle", console).Translate());
                GUI.color = ImpactColor;
                listing.Label(TransferText.Key("DMSE.Transfer.Dialog.ImpactWarning", console).Translate());
                if (survey != null && TransferImpactUtility.IsPlanetkiller(survey.impactEngines))
                {
                    listing.Label(TransferText.Key("DMSE.Transfer.Dialog.ImpactPlanetkiller", console).Translate(survey.impactEngines));
                }
                GUI.color = Color.white;
                listing.Label(TransferText.Key("DMSE.Transfer.Dialog.ImpactRoute", console).Translate(plan.destination.ToString(), plan.distance));
            }
            else
            {
                TransferUIUtility.Header(listing, TransferText.Key("DMSE.Transfer.Dialog.Title", console).Translate());
                listing.Label(TransferText.Key("DMSE.Transfer.Dialog.Route", console).Translate(plan.origin.ToString(), plan.destination.ToString(), plan.distance));
                if (plan.parkedAtNeighbor)
                {
                    listing.Label(TransferText.Key("DMSE.Transfer.UI.ParkedAtNeighbor", console).Translate());
                }
                MapParent site = plan.landOnSite ? Find.WorldObjects.MapParentAt(plan.destination) : null;
                if (site != null)
                {
                    listing.Label(TransferText.Key("DMSE.Transfer.UI.LandOnSite", console).Translate(site.Label));
                }
            }
            listing.GapLine();

            TransferUIUtility.DrawReadiness(listing, issues);
            if (survey != null)
            {
                TransferUIUtility.DrawSummary(listing, TransferEstimate.For(survey, plan.distance, plan.isImpact));
                listing.Gap(DMSEUI.SectionGap);
            }
            TransferUIUtility.Header(listing, TransferText.Key("DMSE.Transfer.UI.IssuesHeader", console).Translate());
            TransferUIUtility.DrawIssues(listing, issues, ref pendingJump);
            if (survey != null)
            {
                listing.Gap(DMSEUI.SectionGap);
                TransferUIUtility.Header(listing, "DMSE.UI.EngineeringDetails".Translate());
                TransferUIUtility.DrawEstimate(listing, TransferEstimate.For(survey, plan.distance, plan.isImpact), survey.network.piles.Count);
            }

            lastHeight = listing.CurHeight + 10f;
            listing.End();
            Widgets.EndScrollView();

            if (blocked)
            {
                GUI.color = DMSEUI.Error;
                Widgets.Label(new Rect(inRect.x, contentRect.yMax + DMSEUI.Gap, inRect.width, noticeHeight), blockedText);
                GUI.color = Color.white;
            }
            // §2.8: reselect/cancel are secondary; confirmation keeps its background.
            float buttonWidth = (inRect.width - 20f) / 3f;
            float y = inRect.yMax - ButtonHeight;
            if (Widgets.ButtonText(new Rect(inRect.x, y, buttonWidth, ButtonHeight), TransferText.Key("DMSE.Transfer.Dialog.Reselect", console).Translate(), drawBackground: false))
            {
                Close();
                TransferPlanningUtility.Reselect(console, plan);
            }
            if (Widgets.ButtonText(new Rect(inRect.x + buttonWidth + 10f, y, buttonWidth, ButtonHeight), "CancelButton".Translate(), drawBackground: false))
            {
                Close();
                ReturnToMap();
            }
            Rect confirmRect = new Rect(inRect.x + (buttonWidth + 10f) * 2f, y, buttonWidth, ButtonHeight);
            string confirmLabel = (plan.isImpact ? "DMSE.Transfer.Dialog.ConfirmImpact" : "DMSE.Transfer.Dialog.Confirm").Translate();
            if (blocked)
            {
                GUI.color = DMSEUI.Muted;
                Widgets.ButtonText(confirmRect, confirmLabel, active: false);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(confirmRect, TransferText.Key("DMSE.Transfer.Dialog.ConfirmBlocked", console).Translate());
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
                    Messages.Message(TransferText.Key("DMSE.Transfer.Dialog.PlanSet", console).Translate(), console.parent, MessageTypeDefOf.TaskCompletion, historical: false);
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
