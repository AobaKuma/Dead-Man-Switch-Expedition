using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 結構樁（設計稿 §3）。提供方形覆蓋範圍；與其他樁之間的鋼樑連結由玩家以 Gizmo 下令、
    /// 殖民者送料施工完成後才生效。已送達的鋼樑材料存在本 Comp 的容器內，
    /// 原版 HaulToContainer 會經由 <see cref="IThingHolder"/> 找到它。
    /// </summary>
    public class CompStructuralPile : ThingComp, IThingHolder
    {
        /// <summary>已完工連結，雙向各存一份。</summary>
        private List<Thing> links = new List<Thing>();

        /// <summary>以本樁為施工端的施工/拆除單，依下單順序一次處理一張。</summary>
        private List<PileBeamOrder> orders = new List<PileBeamOrder>();

        /// <summary>目前施工單（orders[0]）已送達的材料。</summary>
        private ThingOwner<Thing> beamMaterials;

        // 選取目標模式的快取
        private readonly List<IntVec3> linkRangeCells = new List<IntVec3>();

        public CompStructuralPile()
        {
            beamMaterials = new ThingOwner<Thing>(this);
        }

        public CompProperties_StructuralPile Props => (CompProperties_StructuralPile)props;

        /// <summary>唯讀用途；請以 <see cref="PileLinkUtility"/> 變更連結。</summary>
        public List<Thing> Links => links;

        /// <summary>唯讀用途；請以 <see cref="AddOrder"/> / <see cref="CancelOrder"/> 變更。</summary>
        public List<PileBeamOrder> Orders => orders;

        public PileBeamOrder CurrentOrder => orders.Count > 0 ? orders[0] : null;

        public ThingOwner BeamMaterials => beamMaterials;

        public MapComponent_PileNetwork NetworkComp => MapComponent_PileNetwork.For(parent.Map);

        public PileNetwork Network => NetworkComp?.NetworkOf(this);

        /// <summary>目前是架設單且還缺材料。</summary>
        public bool NeedsMaterials
        {
            get
            {
                PileBeamOrder order = CurrentOrder;
                if (order == null || order.dismantle || !parent.Spawned)
                {
                    return false;
                }
                List<ThingDefCountClass> cost = CostOf(order);
                for (int i = 0; i < cost.Count; i++)
                {
                    if (MaterialMissing(cost[i]) > 0)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>目前施工單可以開工：拆除單，或材料已到齊的架設單。</summary>
        public bool CurrentOrderReady
        {
            get
            {
                PileBeamOrder order = CurrentOrder;
                if (order == null || !parent.Spawned)
                {
                    return false;
                }
                return order.dismantle || !NeedsMaterials;
            }
        }

        // ── IThingHolder ─────────────────────────────────────────────────────

        public ThingOwner GetDirectlyHeldThings()
        {
            return beamMaterials;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        // ── 施工單 ───────────────────────────────────────────────────────────

        public int BeamLengthOf(PileBeamOrder order)
        {
            return PileLinkUtility.BeamLength(parent, order.target);
        }

        public List<ThingDefCountClass> CostOf(PileBeamOrder order)
        {
            return PileLinkUtility.BeamCost(this, BeamLengthOf(order));
        }

        public float WorkOf(PileBeamOrder order)
        {
            return PileLinkUtility.BeamWork(this, BeamLengthOf(order), order.dismantle);
        }

        public int MaterialMissing(ThingDefCountClass need)
        {
            return Mathf.Max(0, need.count - beamMaterials.TotalStackCountOfDef(need.thingDef));
        }

        public bool HasOrderTo(Thing target, bool dismantle)
        {
            for (int i = 0; i < orders.Count; i++)
            {
                if (orders[i].target == target && orders[i].dismantle == dismantle)
                {
                    return true;
                }
            }
            return false;
        }

        public void AddOrder(PileBeamOrder order)
        {
            orders.Add(order);
        }

        /// <summary>取消施工單；若是目前施工單，已送達的材料全額丟回樁旁（比照原版取消 Frame）。</summary>
        public void CancelOrder(PileBeamOrder order)
        {
            int index = orders.IndexOf(order);
            if (index < 0)
            {
                return;
            }
            orders.RemoveAt(index);
            if (index == 0)
            {
                DropAllMaterials();
            }
        }

        /// <summary>施工完成（或上帝模式）。架設：消耗材料並建立連結；拆除：解除連結並依比例返還。</summary>
        public void CompleteOrder(PileBeamOrder order, Pawn worker)
        {
            int index = orders.IndexOf(order);
            if (index < 0)
            {
                return;
            }
            CompStructuralPile other = order.TargetComp;
            List<ThingDefCountClass> cost = CostOf(order);
            orders.RemoveAt(index);
            if (other == null)
            {
                if (index == 0)
                {
                    DropAllMaterials();
                }
                return;
            }
            if (order.dismantle)
            {
                if (links.Contains(order.target))
                {
                    PileLinkUtility.RemoveLink(this, other);
                    PileLinkUtility.DropMaterials(cost, Props.beamDismantleRefund, parent.Position, parent.Map);
                }
            }
            else
            {
                if (index == 0)
                {
                    ConsumeMaterials(cost);
                    DropAllMaterials();
                }
                PileLinkUtility.CompleteLink(this, other);
            }
        }

        private void ConsumeMaterials(List<ThingDefCountClass> cost)
        {
            for (int i = 0; i < cost.Count; i++)
            {
                int remaining = cost[i].count;
                for (int j = beamMaterials.Count - 1; j >= 0 && remaining > 0; j--)
                {
                    Thing t = beamMaterials[j];
                    if (t.def != cost[i].thingDef)
                    {
                        continue;
                    }
                    int take = Mathf.Min(remaining, t.stackCount);
                    beamMaterials.Take(t, take).Destroy();
                    remaining -= take;
                }
            }
        }

        private void DropAllMaterials()
        {
            if (beamMaterials.Count > 0 && parent.Spawned)
            {
                beamMaterials.TryDropAll(parent.Position, parent.Map, ThingPlaceMode.Near);
            }
        }

        // ── 連結（僅供 PileLinkUtility / MapComponent 使用） ─────────────────

        public void AddLinkRaw(Thing other)
        {
            if (other != null && !links.Contains(other))
            {
                links.Add(other);
            }
        }

        public void RemoveLinkRaw(Thing other)
        {
            links.Remove(other);
        }

        /// <summary>陣營改變時：解除所有連結（不返還）、取消所有施工單（設計稿 §3-2 生命週期）。</summary>
        public void ClearAllLinksAndOrders()
        {
            for (int i = links.Count - 1; i >= 0; i--)
            {
                Thing other = links[i];
                links.RemoveAt(i);
                other?.TryGetComp<CompStructuralPile>()?.RemoveLinkRaw(parent);
            }
            orders.Clear();
            DropAllMaterials();
            NetworkComp?.CancelOrdersTargeting(parent);
            NetworkComp?.MarkDirty();
        }

        // ── 生命週期 ─────────────────────────────────────────────────────────

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            NetworkComp?.Register(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_PileNetwork net = MapComponent_PileNetwork.For(map);

            // 隨船搬移（原版逆重船或轉移分離）：參照的是同一批 Thing，連結、施工單與材料原樣保留
            bool transported = mode == DestroyMode.WillReplace || parent.BeingTransportedOnGravship;
            if (!transported)
            {
                bool killed = mode == DestroyMode.KillFinalize || mode == DestroyMode.KillFinalizeLeavingsOnly;
                net?.CancelOrdersTargeting(parent);
                orders.Clear();
                if (beamMaterials.Count > 0)
                {
                    beamMaterials.TryDropAll(parent.Position, map, ThingPlaceMode.Near);
                }

                List<CompStructuralPile> formerNeighbors = new List<CompStructuralPile>();
                for (int i = links.Count - 1; i >= 0; i--)
                {
                    Thing other = links[i];
                    CompStructuralPile otherComp = other?.TryGetComp<CompStructuralPile>();
                    links.RemoveAt(i);
                    if (otherComp == null)
                    {
                        continue;
                    }
                    otherComp.RemoveLinkRaw(parent);
                    formerNeighbors.Add(otherComp);
                    // 拆除/打包：等同順便拆掉鋼樑，依比例返還；被摧毀：不返還
                    if (!killed)
                    {
                        List<ThingDefCountClass> cost = PileLinkUtility.BeamCost(this, PileLinkUtility.BeamLength(parent, other));
                        PileLinkUtility.DropMaterials(cost, Props.beamDismantleRefund, parent.Position, map);
                    }
                }
                if (formerNeighbors.Count >= 2)
                {
                    PileLinkUtility.NotifyIfSplit(formerNeighbors, formerNeighbors[0].parent);
                }
            }
            net?.Deregister(this);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            if (beamMaterials.Count == 0)
            {
                return;
            }
            if (previousMap != null)
            {
                beamMaterials.TryDropAll(parent.Position, previousMap, ThingPlaceMode.Near);
            }
            else
            {
                beamMaterials.ClearAndDestroyContents();
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                links.RemoveAll(t => t == null || t.Destroyed);
                orders.RemoveAll(o => o == null || o.target == null || o.target.Destroyed);
            }
            Scribe_Collections.Look(ref links, "pileLinks", LookMode.Reference);
            Scribe_Collections.Look(ref orders, "beamOrders", LookMode.Deep);
            Scribe_Deep.Look(ref beamMaterials, "beamMaterials", this);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (links == null)
                {
                    links = new List<Thing>();
                }
                links.RemoveAll(t => t == null);
                if (orders == null)
                {
                    orders = new List<PileBeamOrder>();
                }
                orders.RemoveAll(o => o == null || o.target == null);
                if (beamMaterials == null)
                {
                    beamMaterials = new ThingOwner<Thing>(this);
                }
            }
        }

        // ── 顯示 ─────────────────────────────────────────────────────────────

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            PileNetwork net = Network;
            if (net != null)
            {
                PileDrawUtility.DrawNetworkOverlay(net);
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned)
            {
                return null;
            }
            StringBuilder sb = new StringBuilder();
            PileNetwork net = Network;
            if (net != null)
            {
                sb.Append("DMSE.Pile.Inspect.Network".Translate(net.piles.Count, net.links.Count, net.Footprint.Count));
            }

            MapComponent_PileNetwork comp = NetworkComp;
            if (comp != null)
            {
                sb.AppendLineIfNotEmpty();
                if (Props.maxLinks > 0)
                {
                    sb.Append("DMSE.Pile.Inspect.Links".Translate(links.Count, comp.LinkCountWithPending(this), Props.maxLinks));
                }
                else
                {
                    sb.Append("DMSE.Pile.Inspect.LinksUnlimited".Translate(links.Count));
                }
            }

            PileBeamOrder order = CurrentOrder;
            if (order != null && order.target != null)
            {
                sb.AppendLineIfNotEmpty();
                IntVec3 to = order.target.Position;
                string progress = (order.workDone / WorkOf(order)).ToStringPercent();
                if (order.dismantle)
                {
                    sb.Append("DMSE.Pile.Inspect.Dismantling".Translate(to.x, to.z, BeamLengthOf(order), progress));
                }
                else
                {
                    sb.Append("DMSE.Pile.Inspect.Building".Translate(to.x, to.z, BeamLengthOf(order), MaterialsProgressLabel(order), progress));
                }
                if (orders.Count > 1)
                {
                    sb.AppendLine();
                    sb.Append("DMSE.Pile.Inspect.Queued".Translate(orders.Count - 1));
                }
            }
            return sb.ToString();
        }

        private string MaterialsProgressLabel(PileBeamOrder order)
        {
            List<ThingDefCountClass> cost = CostOf(order);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cost.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                int have = Mathf.Min(cost[i].count, beamMaterials.TotalStackCountOfDef(cost[i].thingDef));
                sb.Append(cost[i].thingDef.LabelCap).Append(" ").Append(have).Append("/").Append(cost[i].count);
            }
            return sb.ToString();
        }

        // ── Gizmo ────────────────────────────────────────────────────────────

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }
            if (parent.Faction != Faction.OfPlayer || NetworkComp == null)
            {
                yield break;
            }
            bool locked = NetworkComp.IsLocked(this);

            Command_Action build = new Command_Action
            {
                defaultLabel = "DMSE.Pile.BuildBeam".Translate(),
                defaultDesc = "DMSE.Pile.BuildBeamDesc".Translate(),
                icon = PileResources.BuildBeamIcon,
                alsoClickIfOtherInGroupClicked = false,
                action = BeginLinkTargeting
            };
            if (locked)
            {
                build.Disable("DMSE.Pile.Link.Locked".Translate());
            }
            else if (!PileLinkUtility.HasFreeLinkSlot(this))
            {
                build.Disable("DMSE.Pile.Link.MaxLinks".Translate(parent.LabelShort, Props.maxLinks));
            }
            yield return build;

            Command_Action dismantle = new Command_Action
            {
                defaultLabel = "DMSE.Pile.DismantleBeam".Translate(),
                defaultDesc = "DMSE.Pile.DismantleBeamDesc".Translate(),
                icon = PileResources.DismantleBeamIcon,
                alsoClickIfOtherInGroupClicked = false,
                action = OpenDismantleMenu
            };
            if (locked)
            {
                dismantle.Disable("DMSE.Pile.Link.Locked".Translate());
            }
            else if (DismantleableLinks().Count == 0)
            {
                dismantle.Disable(links.Count == 0 ? "DMSE.Pile.Disabled.NoLinks".Translate() : "DMSE.Pile.Disabled.AllDismantling".Translate());
            }
            yield return dismantle;

            Command_Action cancel = new Command_Action
            {
                defaultLabel = "DMSE.Pile.CancelBeam".Translate(),
                defaultDesc = "DMSE.Pile.CancelBeamDesc".Translate(),
                icon = PileResources.CancelBeamIcon,
                alsoClickIfOtherInGroupClicked = false,
                action = OpenCancelMenu
            };
            if (locked)
            {
                cancel.Disable("DMSE.Pile.Link.Locked".Translate());
            }
            else if (orders.Count == 0)
            {
                cancel.Disable("DMSE.Pile.Disabled.NoOrders".Translate());
            }
            yield return cancel;

            CompStructuralPile otherSelected = OtherSelectedPile();
            if (otherSelected != null)
            {
                Command_Action linkSelected = new Command_Action
                {
                    defaultLabel = "DMSE.Pile.LinkSelected".Translate(),
                    defaultDesc = "DMSE.Pile.LinkSelectedDesc".Translate(),
                    icon = PileResources.LinkSelectedIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate { PileLinkUtility.OrderLink(this, otherSelected); }
                };
                string reason;
                if (!PileLinkUtility.CanOrderLink(this, otherSelected.parent, out reason))
                {
                    linkSelected.Disable(reason);
                }
                yield return linkSelected;
            }

            yield return new Command_Action
            {
                defaultLabel = "DMSE.Pile.SelectNetwork".Translate(),
                defaultDesc = "DMSE.Pile.SelectNetworkDesc".Translate(),
                icon = PileResources.SelectNetworkIcon,
                alsoClickIfOtherInGroupClicked = false,
                action = SelectWholeNetwork
            };

            if (DebugSettings.ShowDevGizmos)
            {
                if (CurrentOrder != null)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Complete beam order",
                        action = delegate { CompleteOrder(CurrentOrder, null); }
                    };
                }
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Log pile network",
                    action = LogNetwork
                };
            }
        }

        /// <summary>已完工、且雙方都還沒有拆除單的連結。</summary>
        private List<CompStructuralPile> DismantleableLinks()
        {
            List<CompStructuralPile> result = new List<CompStructuralPile>();
            for (int i = 0; i < links.Count; i++)
            {
                CompStructuralPile other = links[i].TryGetComp<CompStructuralPile>();
                if (other != null && PileLinkUtility.CanOrderDismantle(this, other))
                {
                    result.Add(other);
                }
            }
            return result;
        }

        private void OpenDismantleMenu()
        {
            List<CompStructuralPile> candidates = DismantleableLinks();
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            for (int i = 0; i < candidates.Count; i++)
            {
                CompStructuralPile other = candidates[i];
                IntVec3 pos = other.parent.Position;
                string label = "DMSE.Pile.LinkOption".Translate(pos.x, pos.z, PileLinkUtility.BeamLength(parent, other.parent));
                options.Add(new FloatMenuOption(label, delegate { PileLinkUtility.OrderDismantle(this, other); })
                {
                    mouseoverGuiAction = delegate { TargetHighlighter.Highlight(other.parent, arrow: true, colonistBar: false, circleOverlay: true); }
                });
            }
            if (candidates.Count > 1)
            {
                options.Add(new FloatMenuOption("DMSE.Pile.DismantleAll".Translate(), delegate
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (PileLinkUtility.CanOrderDismantle(this, candidates[i]))
                        {
                            PileLinkUtility.OrderDismantle(this, candidates[i]);
                        }
                    }
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void OpenCancelMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            List<PileBeamOrder> snapshot = new List<PileBeamOrder>(orders);
            for (int i = 0; i < snapshot.Count; i++)
            {
                PileBeamOrder order = snapshot[i];
                if (order.target == null)
                {
                    continue;
                }
                IntVec3 pos = order.target.Position;
                string kind = order.dismantle ? "DMSE.Pile.OrderKind.Dismantle".Translate() : "DMSE.Pile.OrderKind.Build".Translate();
                string label = kind + " " + "DMSE.Pile.LinkOption".Translate(pos.x, pos.z, BeamLengthOf(order));
                Thing target = order.target;
                options.Add(new FloatMenuOption(label, delegate { CancelOrder(order); })
                {
                    mouseoverGuiAction = delegate { TargetHighlighter.Highlight(target, arrow: true, colonistBar: false, circleOverlay: true); }
                });
            }
            if (snapshot.Count > 1)
            {
                options.Add(new FloatMenuOption("DMSE.Pile.CancelAll".Translate(), delegate
                {
                    for (int i = orders.Count - 1; i >= 0; i--)
                    {
                        CancelOrder(orders[i]);
                    }
                }));
            }
            if (options.Count > 0)
            {
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>恰好選取兩根本地圖的樁時，回傳另一根；只讓 thingIDNumber 較小的那根顯示 Gizmo。</summary>
        private CompStructuralPile OtherSelectedPile()
        {
            Selector selector = Find.Selector;
            if (selector.NumSelected != 2)
            {
                return null;
            }
            List<object> selected = selector.SelectedObjects;
            for (int i = 0; i < selected.Count; i++)
            {
                ThingWithComps t = selected[i] as ThingWithComps;
                if (t == null || t == parent || t.Map != parent.Map)
                {
                    continue;
                }
                CompStructuralPile other = t.TryGetComp<CompStructuralPile>();
                if (other != null && parent.thingIDNumber < t.thingIDNumber)
                {
                    return other;
                }
            }
            return null;
        }

        private void SelectWholeNetwork()
        {
            PileNetwork net = Network;
            if (net == null)
            {
                return;
            }
            Selector selector = Find.Selector;
            selector.ClearSelection();
            for (int i = 0; i < net.piles.Count; i++)
            {
                selector.Select(net.piles[i].parent, playSound: false, forceDesignatorDeselect: false);
            }
        }

        private void LogNetwork()
        {
            PileNetwork net = Network;
            if (net == null)
            {
                Log.Message("[DMSE] " + parent + " has no pile network.");
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[DMSE] Pile network of " + parent);
            sb.AppendLine("  piles: " + net.piles.Count + ", links: " + net.links.Count + ", massWeight: " + net.massWeight);
            sb.AppendLine("  covered: " + net.covered.Count + ", footprint: " + net.Footprint.Count + ", contested: " + net.contestedCells.Count);
            sb.AppendLine("  torn apart: " + net.TornApartThings.Count + ", nearby unlinked piles: " + net.nearbyUnlinkedPiles.Count);
            for (int i = 0; i < net.piles.Count; i++)
            {
                CompStructuralPile p = net.piles[i];
                sb.AppendLine("  - " + p.parent + " @" + p.parent.Position + " links=" + p.links.Count + " orders=" + p.orders.Count + " materials=" + p.beamMaterials.ContentsString);
            }
            Log.Message(sb.ToString());
        }

        // ── 選取目標模式（架設鋼樑） ─────────────────────────────────────────

        public void BeginLinkTargeting()
        {
            if (!parent.Spawned)
            {
                return;
            }
            linkRangeCells.Clear();
            if (Props.ProvidesLinkRange)
            {
                linkRangeCells.AddRange(Props.LinkRangeCells(parent.Position, parent.Map));
            }

            Find.Targeter.BeginTargeting(
                PileLinkUtility.PileTargetingParameters(),
                OnLinkTargetChosen,
                delegate (LocalTargetInfo t)
                {
                    string unused;
                    if (t.HasThing && PileLinkUtility.CanOrderLink(this, t.Thing, out unused))
                    {
                        GenDraw.DrawTargetHighlight(t);
                    }
                },
                delegate (LocalTargetInfo t)
                {
                    string unused;
                    return t.HasThing && PileLinkUtility.CanOrderLink(this, t.Thing, out unused);
                },
                null,
                null,
                PileResources.BuildBeamIcon,
                true,
                DrawLinkTargetLabel,
                DrawLinkTargetPreview);
        }

        private void OnLinkTargetChosen(LocalTargetInfo target)
        {
            CompStructuralPile other = target.Thing?.TryGetComp<CompStructuralPile>();
            if (other == null)
            {
                return;
            }
            PileLinkUtility.OrderLink(this, other);
            // 按住 Shift：以目標樁為新來源繼續拉樑（設計稿 §3-2）。此 BeginTargeting 會把
            // needsStopTargetingCall 歸零，所以不會在 action 結束後被關掉。
            if (Event.current != null && Event.current.shift && PileLinkUtility.HasFreeLinkSlot(other))
            {
                other.BeginLinkTargeting();
            }
        }

        private void DrawLinkTargetLabel(LocalTargetInfo target)
        {
            if (!target.HasThing || target.Thing == parent)
            {
                return;
            }
            string reason;
            if (PileLinkUtility.CanOrderLink(this, target.Thing, out reason))
            {
                int length = PileLinkUtility.BeamLength(parent, target.Thing);
                List<ThingDefCountClass> cost = PileLinkUtility.BeamCost(this, length);
                float work = PileLinkUtility.BeamWork(this, length, false);
                Widgets.MouseAttachedLabel("DMSE.Pile.Link.CostLabel".Translate(length, PileLinkUtility.CostLabel(cost), Mathf.RoundToInt(work)));
            }
            else if (!reason.NullOrEmpty())
            {
                Widgets.MouseAttachedLabel(reason, 0f, 0f, ColorLibrary.RedReadable);
            }
        }

        private void DrawLinkTargetPreview(LocalTargetInfo target)
        {
            if (!parent.Spawned || parent.Map != Find.CurrentMap)
            {
                Find.Targeter.StopTargeting();
                return;
            }
            if (linkRangeCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(linkRangeCells, PileResources.LinkRangeColor);
            }

            float alt = PileResources.OverlayAltitude;
            Vector3 from = parent.TrueCenter().WithY(alt);
            MapComponent_PileNetwork comp = NetworkComp;
            if (comp != null)
            {
                List<CompStructuralPile> all = comp.Piles;
                for (int i = 0; i < all.Count; i++)
                {
                    Thing other = all[i].parent;
                    if (other == target.Thing)
                    {
                        continue;
                    }
                    string unused;
                    if (PileLinkUtility.CanOrderLink(this, other, out unused))
                    {
                        GenDraw.DrawLineBetween(from, other.TrueCenter().WithY(alt), PileResources.CandidateLineMat);
                    }
                }
            }

            if (target.HasThing && target.Thing != parent && target.Thing.TryGetComp<CompStructuralPile>() != null)
            {
                string unused;
                Material mat = PileLinkUtility.CanOrderLink(this, target.Thing, out unused) ? PileResources.ValidTargetLineMat : PileResources.InvalidTargetLineMat;
                GenDraw.DrawLineBetween(from, target.Thing.TrueCenter().WithY(alt), mat, 0.3f);
            }
        }
    }
}
