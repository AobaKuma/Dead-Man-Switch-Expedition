using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>ITab 與確認視窗共用的轉移資訊繪製。</summary>
    public static class TransferUIUtility
    {
        private static readonly Color ErrorColor = DMSEUI.Error;

        private static readonly Color WarningColor = DMSEUI.Warning;

        private static readonly Dictionary<string, int> jumpIndex = new Dictionary<string, int>();

        public static string Hours(float hours)
        {
            return TransferFlightMath.HoursToTicks(hours).ToStringTicksToPeriod();
        }

        public static void Header(Listing_Standard listing, string text)
        {
            Text.Font = GameFont.Medium;
            listing.Label(text);
            Text.Font = GameFont.Small;
        }

        public static void Row(Listing_Standard listing, string label, string value, Color? valueColor = null)
        {
            DMSEUI.ValueRow(listing, label, value, valueColor);
        }

        // §2.1: decisions precede engineering details; readiness is not color-only.
        public static void DrawReadiness(Listing_Standard listing, List<TransferIssue> issues)
        {
            int errors = 0;
            foreach (TransferIssue issue in issues) { if (issue.IsError) { errors++; } }
            GUI.color = errors > 0 ? ErrorColor : (issues.Count > 0 ? WarningColor : DMSEUI.Success);
            listing.Label((errors > 0 ? "DMSE.UI.TransferBlocked" : "DMSE.UI.TransferReady")
                .Translate(errors, issues.Count - errors));
            GUI.color = Color.white;
        }

        public static void DrawSummary(Listing_Standard listing, TransferEstimate e)
        {
            Row(listing, "DMSE.Transfer.UI.MaxDistance".Translate(), e.maxDistance >= 0 ? e.maxDistance.ToString() : "DMSE.Transfer.UI.CannotDepart".Translate().ToString());
            Row(listing, (e.HasDestination ? "DMSE.UI.FuelRequiredAvailable" : "DMSE.Transfer.UI.FuelAvailable").Translate(), e.HasDestination
                ? e.fuelRequired.ToString("0") + " / " + e.fuelAvailable.ToString("0")
                : e.fuelAvailable.ToString("0"), e.EnoughFuel ? (Color?)null : ErrorColor);
            if (e.HasDestination)
            {
                Row(listing, "DMSE.Transfer.UI.TotalTime".Translate(), Hours(e.totalHours));
            }
        }

        /// <summary>推進與燃料估算；e.HasDestination 時另顯示燃料需求與時間。</summary>
        public static void DrawEstimate(Listing_Standard listing, TransferEstimate e, int pileCount)
        {
            TransferFlightTuningDef tuning = TransferFlightMath.Tuning;
            Row(listing, "DMSE.Transfer.UI.MassWeight".Translate(), "DMSE.Transfer.UI.MassWeightValue".Translate(e.massWeight.ToString("0.##"), pileCount));
            Row(listing, "DMSE.Transfer.UI.Thrust".Translate(), e.boosterThrust > 0f
                ? "DMSE.Transfer.UI.ThrustWithBoosters".Translate(e.thrust.ToString("0.##"), e.boosterThrust.ToString("0.##")).ToString()
                : e.thrust.ToString("0.##"));
            Row(listing, "DMSE.Transfer.UI.TWR".Translate(tuning.minTWR.ToString("0.##")), e.twr.ToString("0.##"), e.twr < tuning.minTWR ? ErrorColor : (Color?)null);
            Row(listing, "DMSE.Transfer.UI.Eta".Translate(), e.eta.ToStringPercent());
            Row(listing, "DMSE.Transfer.UI.FuelAvailable".Translate(), e.fuelAvailable.ToString("0"));
            Row(listing, "DMSE.Transfer.UI.MaxDistance".Translate(), e.maxDistance >= 0 ? e.maxDistance.ToString() : "DMSE.Transfer.UI.CannotDepart".Translate().ToString(), e.maxDistance < 1 ? ErrorColor : (Color?)null);
            if (e.boosterThrust > 0f)
            {
                // 助推器只在離軌點火：兩次點火時間不同
                Row(listing, "DMSE.Transfer.Phase.DepartureBurn".Translate(), Hours(e.burnHours));
                Row(listing, "DMSE.Transfer.Phase.ArrivalBurn".Translate(), Hours(e.arrivalBurnHours));
            }
            else
            {
                Row(listing, "DMSE.Transfer.UI.BurnTime".Translate(), Hours(e.burnHours));
            }
            if (e.HasDestination)
            {
                Row(listing, "DMSE.Transfer.UI.Distance".Translate(), e.distance.ToString());
                Row(listing, "DMSE.Transfer.UI.FuelRequired".Translate(), e.fuelRequired.ToString("0"), e.EnoughFuel ? (Color?)null : ErrorColor);
                Row(listing, "DMSE.Transfer.UI.CoastTime".Translate(), Hours(e.coastHours));
                Row(listing, "DMSE.Transfer.UI.TotalTime".Translate(), Hours(e.totalHours));
            }
        }

        /// <summary>整備檢查清單；點擊有對象的項目會輪流跳轉到各對象。</summary>
        public static void DrawIssues(Listing_Standard listing, List<TransferIssue> issues, ref GlobalTargetInfo pendingJump)
        {
            if (issues.Count == 0)
            {
                GUI.color = DMSEUI.Success;
                listing.Label("DMSE.Transfer.UI.NoIssues".Translate());
                GUI.color = Color.white;
                return;
            }
            float width = listing.ColumnWidth;
            for (int i = 0; i < issues.Count; i++)
            {
                TransferIssue issue = issues[i];
                string text = (issue.IsError ? "DMSE.Transfer.UI.ErrorPrefix" : "DMSE.Transfer.UI.WarningPrefix").Translate() + " " + issue.text;
                if (issue.targets.Count > 0) { text += "  [" + "DMSE.UI.Locate".Translate() + "]"; }
                float height = Text.CalcHeight(text, width);
                Rect rect = listing.GetRect(height);
                if (issue.targets.Count > 0)
                {
                    Widgets.DrawHighlightIfMouseover(rect);
                    if (Widgets.ButtonInvisible(rect))
                    {
                        int idx;
                        jumpIndex.TryGetValue(issue.text, out idx);
                        pendingJump = issue.targets[idx % issue.targets.Count];
                        jumpIndex[issue.text] = idx + 1;
                    }
                }
                GUI.color = issue.IsError ? ErrorColor : WarningColor;
                Widgets.Label(rect, text);
                GUI.color = Color.white;
            }
        }
    }
}
