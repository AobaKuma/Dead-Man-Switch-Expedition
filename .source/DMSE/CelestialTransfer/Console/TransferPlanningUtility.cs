using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 制定轉移計畫（設計稿 §4-2 步驟 1、§6-5）：世界地圖選目的地 → <see cref="Dialog_TransferPlan"/> 確認。
    /// </summary>
    public static class TransferPlanningUtility
    {
        private static readonly List<PlanetTile> tmpNeighbors = new List<PlanetTile>();

        /// <summary>不需目的地就能判斷的阻擋原因；可以制定計畫時回傳 null。</summary>
        public static string CannotPlanReason(CompTransferConsole console)
        {
            if (console.State == TransferConsoleState.AwaitingOperator || console.State == TransferConsoleState.Countdown)
            {
                return "DMSE.Transfer.Console.AlreadyLaunching".Translate();
            }
            List<TransferIssue> issues = TransferReadiness.Check(console, null, includeWarnings: false);
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i].IsError)
                {
                    return issues[i].text;
                }
            }
            return null;
        }

        public static void StartPlanning(CompTransferConsole console)
        {
            string reason = CannotPlanReason(console);
            if (reason != null)
            {
                Messages.Message(reason, console.parent, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Map map = console.parent.Map;
            PlanetTile origin = map.Tile;
            TransferEstimate estimate = TransferEstimate.For(console.Survey, -1);
            int maxDistance = estimate.maxDistance;

            CameraJumper.TryJump(CameraJumper.GetWorldTarget(console.parent), CameraJumper.MovementMode.Pan);
            Find.WorldSelector.ClearSelection();
            Find.TilePicker.StartTargeting_NewTemp(
                tile => ValidateDestination(origin, tile, maxDistance, showMessage: true),
                tile => OnDestinationChosen(console, origin, tile),
                null,
                delegate
                {
                    if (maxDistance > 0)
                    {
                        GenDraw.DrawWorldRadiusRing(origin, maxDistance, CompPilotConsole.GetThrusterRadiusMat(origin));
                    }
                },
                true,
                () => CameraJumper.TryJump(console.parent.Position, console.parent.Map),
                null,
                false,
                true,
                true,
                true,
                false,
                null);
        }

        /// <summary>設計稿 §6-5：同一太空層、不是起點、距離在航程內；被占用的格子交給選定後改停鄰格處理。</summary>
        public static bool ValidateDestination(PlanetTile origin, PlanetTile tile, int maxDistance, bool showMessage)
        {
            string reason = null;
            if (!tile.Valid || tile == origin)
            {
                reason = "DMSE.Transfer.Dest.Invalid".Translate();
            }
            else if (tile.Layer != origin.Layer)
            {
                reason = "DMSE.Transfer.Dest.OtherLayer".Translate();
            }
            else if (!DebugSettings.ignoreGravshipRange && TransferFlightMath.Distance(origin, tile) > maxDistance)
            {
                reason = "DMSE.Transfer.Dest.OutOfRange".Translate(UnityEngine.Mathf.Max(0, maxDistance));
            }
            else if (Find.WorldObjects.AnyWorldObjectAt(tile) && !FindFreeNeighbor(origin, tile, maxDistance).Valid)
            {
                reason = "DMSE.Transfer.Dest.Occupied".Translate();
            }
            if (reason != null)
            {
                if (showMessage)
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, historical: false);
                }
                return false;
            }
            return true;
        }

        // ── 撞擊飛行（設計稿 §9） ────────────────────────────────────────────

        /// <summary>船上運作中的撞擊用引擎達門檻時才提供撞擊飛行（與舊版模式選單相同，未達門檻時不顯示）。</summary>
        public static bool ImpactAvailable(CompTransferConsole console)
        {
            TransferShipSurvey survey = console.Survey;
            return survey != null && survey.operationalImpactEngines >= TransferFlightMath.Tuning.impactMinEngines;
        }

        /// <summary>確認視窗（6 秒後才能確認）→ 世界地圖選地表撞擊點 → <see cref="Dialog_TransferPlan"/>。</summary>
        public static void StartPlanningImpact(CompTransferConsole console)
        {
            string reason = CannotPlanReason(console);
            if (reason != null)
            {
                Messages.Message(reason, console.parent, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Dialog_MessageBox confirm = Dialog_MessageBox.CreateConfirmation("DMSE.Flight.Impact.Confirm".Translate(), delegate
            {
                PickImpactTarget(console);
            }, destructive: true);
            confirm.interactionDelay = 6f;
            Find.WindowStack.Add(confirm);
        }

        public static void PickImpactTarget(CompTransferConsole console)
        {
            if (!console.parent.Spawned)
            {
                return;
            }
            PlanetTile origin = console.parent.Map.Tile;
            TransferEstimate estimate = TransferEstimate.For(console.Survey, -1);
            int maxDistance = estimate.maxDistance;

            PlanetLayer surface = Find.WorldGrid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
            PlanetTile below = surface != null ? surface.GetClosestTile_NewTemp(origin) : PlanetTile.Invalid;
            CameraJumper.TryJump(below.Valid ? new GlobalTargetInfo(below) : CameraJumper.GetWorldTarget(console.parent), CameraJumper.MovementMode.Pan);
            Find.WorldSelector.ClearSelection();
            Find.TilePicker.StartTargeting_NewTemp(
                tile => ValidateImpactTarget(origin, tile, maxDistance, showMessage: true),
                tile => Find.WindowStack.Add(new Dialog_TransferPlan(console, TransferPlan.Impact(origin, tile))),
                null,
                delegate
                {
                    if (maxDistance > 0)
                    {
                        GenDraw.DrawWorldRadiusRing(origin, maxDistance, CompPilotConsole.GetThrusterRadiusMat(origin));
                    }
                },
                true,
                () => CameraJumper.TryJump(console.parent.Position, console.parent.Map),
                null,
                false,
                true,
                true,
                true,
                false,
                null);
        }

        /// <summary>撞擊點：地表層、目前沒有載入中的地圖；航程以起點太空層中撞擊點上方的格子計算。</summary>
        public static bool ValidateImpactTarget(PlanetTile origin, PlanetTile tile, int maxDistance, bool showMessage)
        {
            string reason = ImpactTargetProblem(origin, tile, maxDistance);
            if (reason != null)
            {
                if (showMessage)
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, historical: false);
                }
                return false;
            }
            return true;
        }

        /// <summary>撞擊點不合法的原因；合法時回傳 null。</summary>
        public static string ImpactTargetProblem(PlanetTile origin, PlanetTile tile, int maxDistance)
        {
            string basic = ImpactTargetBasicProblem(tile);
            if (basic != null)
            {
                return basic;
            }
            PlanetTile approach = TransferPlan.ApproachTileFor(origin, tile);
            if (!approach.Valid)
            {
                return "DMSE.Transfer.Dest.Invalid".Translate();
            }
            if (!DebugSettings.ignoreGravshipRange && TransferFlightMath.Distance(origin, approach) > maxDistance)
            {
                return "DMSE.Transfer.Dest.OutOfRange".Translate(UnityEngine.Mathf.Max(0, maxDistance));
            }
            return null;
        }

        /// <summary>不需航程計算的撞擊點檢查（整備檢查每幀呼叫，用來確認計畫制定後撞擊點仍然有效）。</summary>
        public static string ImpactTargetBasicProblem(PlanetTile tile)
        {
            if (!tile.Valid)
            {
                return "DMSE.Transfer.Dest.Invalid".Translate();
            }
            if (tile.LayerDef != PlanetLayerDefOf.Surface)
            {
                return "DMSE.Transfer.Dest.NotSurface".Translate();
            }
            MapParent mapParent = Find.WorldObjects.MapParentAt(tile);
            if (mapParent != null && mapParent.HasMap)
            {
                return "DMSE.Transfer.Dest.ImpactHasMap".Translate();
            }
            return null;
        }

        /// <summary>確認視窗的「重新選擇」：轉移回到選目的地，撞擊回到選撞擊點（不再跳確認）。</summary>
        public static void Reselect(CompTransferConsole console, TransferPlan plan)
        {
            if (plan != null && plan.isImpact)
            {
                PickImpactTarget(console);
            }
            else
            {
                StartPlanning(console);
            }
        }

        private static void OnDestinationChosen(CompTransferConsole console, PlanetTile origin, PlanetTile tile)
        {
            // 已有地點：降落到該地點（船先停在旁邊的空格，抵達後選位）
            if (TransferLandingUtility.IsLandableSite(tile, origin, out _))
            {
                TransferPlan landing = new TransferPlan(origin, tile, false) { landOnSite = true };
                Find.WindowStack.Add(new Dialog_TransferPlan(console, landing));
                return;
            }
            bool parked = false;
            if (Find.WorldObjects.AnyWorldObjectAt(tile))
            {
                TransferEstimate estimate = TransferEstimate.For(console.Survey, -1);
                tile = FindFreeNeighbor(origin, tile, estimate.maxDistance);
                parked = true;
            }
            TransferPlan plan = new TransferPlan(origin, tile, parked);
            Find.WindowStack.Add(new Dialog_TransferPlan(console, plan));
        }

        /// <summary>最近（離起點最近）的空鄰格；目的地附近停泊用。</summary>
        public static PlanetTile FindFreeNeighbor(PlanetTile origin, PlanetTile tile, int maxDistance)
        {
            Find.WorldGrid.GetTileNeighbors(tile, tmpNeighbors);
            PlanetTile best = PlanetTile.Invalid;
            int bestDist = int.MaxValue;
            for (int i = 0; i < tmpNeighbors.Count; i++)
            {
                PlanetTile n = tmpNeighbors[i];
                if (n == origin || Find.WorldObjects.AnyWorldObjectAt(n))
                {
                    continue;
                }
                int d = TransferFlightMath.Distance(origin, n);
                if (!DebugSettings.ignoreGravshipRange && d > maxDistance)
                {
                    continue;
                }
                if (d < bestDist)
                {
                    best = n;
                    bestDist = d;
                }
            }
            return best;
        }
    }
}
