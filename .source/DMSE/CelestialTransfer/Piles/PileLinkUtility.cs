using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 結構樁連結與鋼樑施工單的規則與操作（設計稿 §3-2）。
    /// </summary>
    public static class PileLinkUtility
    {
        /// <summary>
        /// 兩樁中心是否在連結距離內。只計提供連結範圍的一端（linkRange &gt; 0）：兩端都提供時須同時在
        /// 兩端範圍內（上限取較小者，距離取兩種算法中較大者）；兩端都不提供則無法連結（range = 0）。
        /// </summary>
        public static bool InLinkRange(CompProperties_StructuralPile aProps, IntVec3 aPos, CompProperties_StructuralPile bProps, IntVec3 bPos, out int dist, out int range)
        {
            dist = 0;
            range = int.MaxValue;
            if (aProps.ProvidesLinkRange)
            {
                dist = Mathf.Max(dist, aProps.Distance(aPos, bPos));
                range = Mathf.Min(range, aProps.linkRange);
            }
            if (bProps.ProvidesLinkRange)
            {
                dist = Mathf.Max(dist, bProps.Distance(aPos, bPos));
                range = Mathf.Min(range, bProps.linkRange);
            }
            if (range == int.MaxValue)
            {
                range = 0;
                return false;
            }
            return dist <= range;
        }

        public static bool InLinkRange(CompProperties_StructuralPile aProps, IntVec3 aPos, CompProperties_StructuralPile bProps, IntVec3 bPos)
        {
            return InLinkRange(aProps, aPos, bProps, bPos, out _, out _);
        }

        /// <summary>
        /// 不提供連結範圍的樁只能建在某根已建成、提供範圍的己方樁的連結範圍內。
        /// </summary>
        public static bool InAnyProviderRange(Map map, CompProperties_StructuralPile props, IntVec3 pos, Thing ignore = null)
        {
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(map);
            if (comp == null)
            {
                return false;
            }
            List<CompStructuralPile> piles = comp.Piles;
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile p = piles[i];
                if (p.parent != ignore && p.Props.ProvidesLinkRange && p.parent.Faction == Faction.OfPlayerSilentFail
                    && InLinkRange(props, pos, p.Props, p.parent.Position))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>鋼樑長度：兩樁中心歐氏距離，四捨五入、至少 1。</summary>
        public static int BeamLength(Thing a, Thing b)
        {
            float dist = (a.TrueCenter() - b.TrueCenter()).MagnitudeHorizontal();
            return Mathf.Max(1, Mathf.RoundToInt(dist));
        }

        public static List<ThingDefCountClass> BeamCost(CompStructuralPile owner, int length)
        {
            List<ThingDefCountClass> cost = new List<ThingDefCountClass>();
            List<ThingDefCountClass> perCell = owner.Props.beamCostPerCell;
            if (perCell == null)
            {
                return cost;
            }
            for (int i = 0; i < perCell.Count; i++)
            {
                cost.Add(new ThingDefCountClass(perCell[i].thingDef, perCell[i].count * length));
            }
            return cost;
        }

        public static float BeamWork(CompStructuralPile owner, int length, bool dismantle)
        {
            float work = owner.Props.beamWorkPerCell * length;
            if (dismantle)
            {
                work *= owner.Props.beamDismantleWorkFactor;
            }
            return Mathf.Max(1f, work);
        }

        public static string CostLabel(List<ThingDefCountClass> cost)
        {
            if (cost.NullOrEmpty())
            {
                return "-";
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cost.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(cost[i].thingDef.LabelCap).Append(" ").Append(cost[i].count);
            }
            return sb.ToString();
        }

        public static TargetingParameters PileTargetingParameters()
        {
            return new TargetingParameters
            {
                canTargetBuildings = true,
                canTargetPawns = false,
                canTargetItems = false,
                canTargetLocations = false,
                mapObjectTargetsMustBeAutoAttackable = false,
                validator = t => t.HasThing && t.Thing.TryGetComp<CompStructuralPile>() != null
            };
        }

        // ── 條件 ─────────────────────────────────────────────────────────────

        public static bool HasFreeLinkSlot(CompStructuralPile pile)
        {
            if (pile.Props.maxLinks <= 0)
            {
                return true;
            }
            MapComponent_PileNetwork net = pile.NetworkComp;
            return net == null || net.LinkCountWithPending(pile) < pile.Props.maxLinks;
        }

        /// <summary>
        /// 設計稿 §3-2「連結條件」。reason 為 null 表示不需顯示原因（例如目標就是自己或不是有效目標）。
        /// </summary>
        public static bool CanOrderLink(CompStructuralPile a, Thing bThing, out string reason)
        {
            reason = null;
            if (a == null || bThing == null || !a.parent.Spawned)
            {
                return false;
            }
            if (bThing == a.parent)
            {
                return false;
            }
            CompStructuralPile b = bThing.TryGetComp<CompStructuralPile>();
            if (b == null || !bThing.Spawned || bThing.Map != a.parent.Map || bThing.Faction != a.parent.Faction)
            {
                reason = "DMSE.Pile.Link.NotPile".Translate();
                return false;
            }
            if (!a.Props.ProvidesLinkRange && !b.Props.ProvidesLinkRange)
            {
                reason = "DMSE.Pile.Link.NoRange".Translate();
                return false;
            }
            if (!InLinkRange(a.Props, a.parent.Position, b.Props, bThing.Position, out int dist, out int range))
            {
                reason = "DMSE.Pile.Link.OutOfRange".Translate(dist, range);
                return false;
            }
            if (a.Links.Contains(bThing) || MapComponent_PileNetwork.HasOrderBetween(a, b, false))
            {
                reason = "DMSE.Pile.Link.AlreadyLinked".Translate();
                return false;
            }
            MapComponent_PileNetwork netComp = a.NetworkComp;
            if (netComp != null && (netComp.IsLocked(a) || netComp.IsLocked(b)))
            {
                reason = "DMSE.Pile.Link.Locked".Translate();
                return false;
            }
            if (!HasFreeLinkSlot(a))
            {
                reason = "DMSE.Pile.Link.MaxLinks".Translate(a.parent.LabelShort, a.Props.maxLinks);
                return false;
            }
            if (!HasFreeLinkSlot(b))
            {
                reason = "DMSE.Pile.Link.MaxLinks".Translate(bThing.LabelShort, b.Props.maxLinks);
                return false;
            }
            return true;
        }

        public static bool CanOrderDismantle(CompStructuralPile a, CompStructuralPile b)
        {
            MapComponent_PileNetwork netComp = a.NetworkComp;
            if (netComp != null && (netComp.IsLocked(a) || netComp.IsLocked(b)))
            {
                return false;
            }
            return a.Links.Contains(b.parent) && !MapComponent_PileNetwork.HasOrderBetween(a, b, true);
        }

        // ── 下單 ─────────────────────────────────────────────────────────────

        public static PileBeamOrder OrderLink(CompStructuralPile a, CompStructuralPile b)
        {
            PileBeamOrder order = new PileBeamOrder(b.parent, false);
            a.AddOrder(order);
            if (DebugSettings.godMode)
            {
                a.CompleteOrder(order, null);
            }
            return order;
        }

        public static PileBeamOrder OrderDismantle(CompStructuralPile a, CompStructuralPile b)
        {
            PileBeamOrder order = new PileBeamOrder(b.parent, true);
            a.AddOrder(order);
            if (DebugSettings.godMode)
            {
                a.CompleteOrder(order, null);
            }
            return order;
        }

        // ── 連結本體 ─────────────────────────────────────────────────────────

        public static void CompleteLink(CompStructuralPile a, CompStructuralPile b)
        {
            a.AddLinkRaw(b.parent);
            b.AddLinkRaw(a.parent);
            a.NetworkComp?.MarkDirty();
        }

        /// <summary>解除雙向連結；若因此讓兩端不再相連，顯示「結構網路已分裂」。</summary>
        public static void RemoveLink(CompStructuralPile a, CompStructuralPile b)
        {
            a.RemoveLinkRaw(b.parent);
            b.RemoveLinkRaw(a.parent);
            a.NetworkComp?.MarkDirty();
            if (!AreConnected(a, b))
            {
                NotifyNetworkSplit(a.parent);
            }
        }

        /// <summary>沿已完工連結走訪，判斷兩根樁是否仍在同一網路。</summary>
        public static bool AreConnected(CompStructuralPile from, CompStructuralPile to)
        {
            if (from == to)
            {
                return true;
            }
            HashSet<CompStructuralPile> visited = new HashSet<CompStructuralPile> { from };
            Queue<CompStructuralPile> queue = new Queue<CompStructuralPile>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                CompStructuralPile cur = queue.Dequeue();
                for (int i = 0; i < cur.Links.Count; i++)
                {
                    CompStructuralPile next = cur.Links[i].TryGetComp<CompStructuralPile>();
                    if (next == null || !visited.Add(next))
                    {
                        continue;
                    }
                    if (next == to)
                    {
                        return true;
                    }
                    queue.Enqueue(next);
                }
            }
            return false;
        }

        /// <summary>樁被移除後，若原本的鄰居彼此不再相連，代表網路分裂。</summary>
        public static void NotifyIfSplit(List<CompStructuralPile> formerNeighbors, Thing lookAt)
        {
            for (int i = 1; i < formerNeighbors.Count; i++)
            {
                if (!AreConnected(formerNeighbors[0], formerNeighbors[i]))
                {
                    NotifyNetworkSplit(lookAt);
                    return;
                }
            }
        }

        private static void NotifyNetworkSplit(Thing lookAt)
        {
            if (lookAt.Faction != Faction.OfPlayer)
            {
                return;
            }
            Messages.Message("DMSE.Pile.NetworkSplit".Translate(), new LookTargets(lookAt), MessageTypeDefOf.CautionInput, historical: false);
        }

        // ── 材料 ─────────────────────────────────────────────────────────────

        /// <summary>在 cell 附近掉落 cost × fraction 的材料（逐項隨機取整）。</summary>
        public static void DropMaterials(List<ThingDefCountClass> cost, float fraction, IntVec3 cell, Map map)
        {
            if (cost == null || map == null)
            {
                return;
            }
            for (int i = 0; i < cost.Count; i++)
            {
                int remaining = GenMath.RoundRandom(cost[i].count * fraction);
                ThingDef def = cost[i].thingDef;
                while (remaining > 0)
                {
                    Thing thing = ThingMaker.MakeThing(def);
                    thing.stackCount = Mathf.Min(remaining, def.stackLimit);
                    remaining -= thing.stackCount;
                    GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near);
                }
            }
        }
    }
}
