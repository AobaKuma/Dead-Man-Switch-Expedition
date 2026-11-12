using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    public enum TransferIssueSeverity
    {
        Warning,
        Error
    }

    public class TransferIssue
    {
        public TransferIssueSeverity severity;

        public string text;

        /// <summary>可點擊跳轉的對象（可為空）。</summary>
        public List<GlobalTargetInfo> targets = new List<GlobalTargetInfo>();

        public bool IsError => severity == TransferIssueSeverity.Error;

        public TransferIssue(TransferIssueSeverity severity, string text)
        {
            this.severity = severity;
            this.text = text;
        }

        public TransferIssue With(Thing thing)
        {
            if (thing != null)
            {
                targets.Add(thing);
            }
            return this;
        }

        public TransferIssue With<T>(IEnumerable<T> things) where T : Thing
        {
            foreach (T t in things)
            {
                targets.Add(t);
            }
            return this;
        }
    }

    /// <summary>
    /// 整備檢查（設計稿 §4-4）。Error 會擋下起飛；Warning 只提示。plan 為 null 時略過與目的地相關的檢查。
    /// </summary>
    public static class TransferReadiness
    {
        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        public static bool AnyError(List<TransferIssue> issues)
        {
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i].IsError)
                {
                    return true;
                }
            }
            return false;
        }

        /// <param name="includeWarnings">false 時只做便宜的阻擋性檢查（Gizmo 每幀呼叫用）；Warning 需掃描整個船體範圍。</param>
        public static List<TransferIssue> Check(CompTransferConsole console, TransferPlan plan, bool includeWarnings = true)
        {
            List<TransferIssue> issues = new List<TransferIssue>();
            Thing consoleThing = console.parent;
            Map map = consoleThing.MapHeld;
            if (map == null || !consoleThing.Spawned)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.ConsoleUnavailable", console).Translate()));
                return issues;
            }

            if (!TransferEnvironment.AllowsTransfer(map))
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.NotInSpace", console).Translate()));
            }
            WorldObject_TransferShip flyingShip = map.Parent as WorldObject_TransferShip;
            if (flyingShip != null && flyingShip.InFlight)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.InFlight", console).Translate()));
            }

            PileNetwork net = console.Network;
            if (net == null)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.ConsoleNotOnShip", console).Translate()).With(consoleThing));
                return issues;
            }
            if (!console.IsAvailable)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.ConsoleUnavailable", console).Translate()).With(consoleThing));
            }

            TransferShipSurvey survey = net.Survey;
            TransferFlightTuningDef tuning = TransferFlightMath.Tuning;

            // ── 推進與燃料 ──
            if (survey.operationalEngines == 0)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.NoEngines", console).Translate()));
            }
            else if (survey.TWR < tuning.minTWR)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.LowTWR", console).Translate(survey.TWR.ToString("0.##"), tuning.minTWR.ToString("0.##"))));
            }
            if (plan != null)
            {
                float required = TransferFlightMath.FuelRequired(survey, plan.distance);
                if (survey.totalFuel < required)
                {
                    issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.NotEnoughFuel", console).Translate(survey.totalFuel.ToString("0"), required.ToString("0"))));
                }
                if (plan.isImpact)
                {
                    // 撞擊飛行（設計稿 §9）：撞擊用引擎門檻；撞擊點在計畫後可能出現地圖（例如任務據點被造訪）
                    if (survey.operationalImpactEngines < tuning.impactMinEngines)
                    {
                        issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.ImpactEngines", console).Translate(survey.operationalImpactEngines, tuning.impactMinEngines)));
                    }
                    string targetProblem = TransferPlanningUtility.ImpactTargetBasicProblem(plan.destination);
                    if (targetProblem == null && !plan.approachTile.Valid)
                    {
                        targetProblem = TransferText.Key("DMSE.Transfer.Dest.Invalid", console).Translate();
                    }
                    if (targetProblem != null)
                    {
                        issues.Add(new TransferIssue(TransferIssueSeverity.Error, targetProblem));
                    }
                }
                // 防空封鎖（主動攔截）：航線穿越封鎖圈只警告，飛行中會被接戰。
                string airspace = TransferPlanningUtility.AirspaceProblem(plan);
                if (airspace != null)
                {
                    issues.Add(new TransferIssue(TransferIssueSeverity.Warning, airspace));
                }
            }

            // ── 同一地圖同時只允許一艘船在起飛程序中 ──
            List<CompTransferConsole> consoles = MapComponent_PileNetwork.For(map).Consoles;
            for (int i = 0; i < consoles.Count; i++)
            {
                CompTransferConsole other = consoles[i];
                if (other != console && (other.State == TransferConsoleState.AwaitingOperator || other.State == TransferConsoleState.Countdown))
                {
                    issues.Add(new TransferIssue(TransferIssueSeverity.Error, TransferText.Key("DMSE.Transfer.Issue.OtherShipDeparting", console).Translate()).With(other.parent));
                    break;
                }
            }

            if (!includeWarnings)
            {
                return issues;
            }

            HashSet<IntVec3> footprint = net.Footprint;

            // ── 範圍外的殖民者 / 囚犯 / 奴隸 / 動物 / 機械體 ──
            List<Pawn> leftBehind = new List<Pawn>();
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                bool ours = p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony;
                if (ours && !footprint.Contains(p.Position))
                {
                    leftBehind.Add(p);
                }
            }
            if (leftBehind.Count > 0)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.PawnsLeftBehind", console).Translate(leftBehind.Count)).With(leftBehind));
            }

            // ── 不隨船移動的物件、站在尾焰區的 pawn ──
            List<Thing> notCarried = new List<Thing>();
            foreach (IntVec3 c in footprint)
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int j = 0; j < things.Count; j++)
                {
                    Thing t = things[j];
                    // 飛行中的導彈（Skyfaller 或 Projectile）不算留在甲板上的物品。
                    if (!t.def.bringAlongOnGravship && !(t is Mote) && !(t is Skyfaller) && !(t is Projectile) && t.def.category != ThingCategory.Ethereal)
                    {
                        notCarried.Add(t);
                    }
                }
            }
            if (notCarried.Count > 0)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.NotCarried", console).Translate(notCarried.Count)).With(notCarried));
            }

            List<Pawn> inExhaust = new List<Pawn>();
            // 尾焰不傷人的圖層（presentation.exhaustDamage = 0，例如潛艇）不警告
            bool exhaustHurts = TransferEnvironment.Presentation(console.parent.MapHeld).exhaustDamage > 0f;
            for (int i = 0; exhaustHurts && i < survey.engines.Count; i++)
            {
                if (!survey.engines[i].Operational)
                {
                    continue;
                }
                survey.engines[i].engine.GetExclusionZone(tmpCells);
                for (int j = 0; j < tmpCells.Count; j++)
                {
                    if (!tmpCells[j].InBounds(map))
                    {
                        continue;
                    }
                    List<Thing> things = map.thingGrid.ThingsListAtFast(tmpCells[j]);
                    for (int k = 0; k < things.Count; k++)
                    {
                        Pawn p = things[k] as Pawn;
                        if (p != null && !inExhaust.Contains(p))
                        {
                            inExhaust.Add(p);
                        }
                    }
                }
            }
            if (inExhaust.Count > 0)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.PawnsInExhaust", console).Translate(inExhaust.Count)).With(inExhaust));
            }

            // ── 結構網路相關 ──
            if (net.nearbyUnlinkedPiles.Count > 0)
            {
                List<Thing> piles = new List<Thing>();
                for (int i = 0; i < net.nearbyUnlinkedPiles.Count; i++)
                {
                    piles.Add(net.nearbyUnlinkedPiles[i].parent);
                }
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.NearbyUnlinkedPiles", console).Translate(piles.Count)).With(piles));
            }

            List<Thing> tornApart = net.TornApartThings;
            if (tornApart.Count > 0)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.TornApart", console).Translate(tornApart.Count)).With(tornApart));
                List<Thing> withPawns = new List<Thing>();
                for (int i = 0; i < tornApart.Count; i++)
                {
                    if (ContainsPawn(tornApart[i]))
                    {
                        withPawns.Add(tornApart[i]);
                    }
                }
                if (withPawns.Count > 0)
                {
                    issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.TornApartWithPawns", console).Translate(withPawns.Count)).With(withPawns));
                }
            }

            List<IntVec3> contested = net.ContestedFootprintCells;
            if (contested.Count > 0)
            {
                int buildings = 0;
                List<Thing> foreignPiles = new List<Thing>();
                HashSet<Thing> seen = new HashSet<Thing>();
                for (int i = 0; i < contested.Count; i++)
                {
                    List<Thing> things = map.thingGrid.ThingsListAtFast(contested[i]);
                    for (int j = 0; j < things.Count; j++)
                    {
                        Thing t = things[j];
                        if (t.def.category != ThingCategory.Building || !seen.Add(t))
                        {
                            continue;
                        }
                        buildings++;
                        CompStructuralPile pile = t.TryGetComp<CompStructuralPile>();
                        if (pile != null && !net.Contains(pile) && PileNetwork.FullyInside(t, footprint))
                        {
                            foreignPiles.Add(t);
                        }
                    }
                }
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.Contested", console).Translate(contested.Count, buildings, foreignPiles.Count)).With(foreignPiles));
            }

            List<Thing> pendingOrders = new List<Thing>();
            for (int i = 0; i < net.piles.Count; i++)
            {
                if (net.piles[i].Orders.Count > 0)
                {
                    pendingOrders.Add(net.piles[i].parent);
                }
            }
            if (pendingOrders.Count > 0)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Warning, TransferText.Key("DMSE.Transfer.Issue.PendingBeamOrders", console).Translate(pendingOrders.Count)).With(pendingOrders));
            }

            return issues;
        }

        private static bool ContainsPawn(Thing t)
        {
            IThingHolder holder = t as IThingHolder;
            ThingOwner owner = holder?.GetDirectlyHeldThings();
            if (owner == null)
            {
                return false;
            }
            for (int i = 0; i < owner.Count; i++)
            {
                if (owner[i] is Pawn)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
