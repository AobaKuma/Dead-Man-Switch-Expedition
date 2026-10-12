using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 地圖上所有結構樁的索引與網路快取（設計稿 §3-6）。連結與施工單存在各樁的 Comp 上，
    /// 這裡不存檔，讀檔時由樁的 PostSpawnSetup 重新註冊。
    /// </summary>
    public class MapComponent_PileNetwork : MapComponent
    {
        private const int ValidateInterval = 250;

        private readonly List<CompStructuralPile> piles = new List<CompStructuralPile>();

        private readonly List<PileNetwork> networks = new List<PileNetwork>();

        private readonly Dictionary<CompStructuralPile, PileNetwork> networkOf = new Dictionary<CompStructuralPile, PileNetwork>();

        private readonly List<CompTransferConsole> consoles = new List<CompTransferConsole>();

        private bool dirty = true;

        public MapComponent_PileNetwork(Map map) : base(map)
        {
        }

        public static MapComponent_PileNetwork For(Map map)
        {
            return map?.GetComponent<MapComponent_PileNetwork>();
        }

        public List<CompStructuralPile> Piles => piles;

        public List<PileNetwork> Networks
        {
            get
            {
                if (dirty)
                {
                    Rebuild();
                }
                return networks;
            }
        }

        public void Register(CompStructuralPile pile)
        {
            if (!piles.Contains(pile))
            {
                piles.Add(pile);
            }
            MarkDirty();
        }

        public void Deregister(CompStructuralPile pile)
        {
            piles.Remove(pile);
            MarkDirty();
        }

        /// <summary>連結完工/解除、樁增刪時呼叫；網路與 footprint 一併失效，鋼樑圖層重建。</summary>
        public void MarkDirty()
        {
            dirty = true;
            MapDrawLayer_StructuralBeams.SetDirty(map);
        }

        public PileNetwork NetworkOf(CompStructuralPile pile)
        {
            if (dirty)
            {
                Rebuild();
            }
            PileNetwork net;
            return networkOf.TryGetValue(pile, out net) ? net : null;
        }

        /// <summary>
        /// 佔地完整位於其 footprint 內的網路（控制台、引擎等所屬的船）。爭議格可能同屬多個網路的 footprint，
        /// 此時回傳第一個符合者。
        /// </summary>
        public PileNetwork NetworkContaining(Thing thing)
        {
            if (thing == null || !thing.Spawned || thing.Map != map)
            {
                return null;
            }
            List<PileNetwork> nets = Networks;
            for (int i = 0; i < nets.Count; i++)
            {
                if (nets[i].covered.Contains(thing.Position) && PileNetwork.FullyInside(thing, nets[i].Footprint))
                {
                    return nets[i];
                }
            }
            return null;
        }

        // ── 控制台 ───────────────────────────────────────────────────────────

        public List<CompTransferConsole> Consoles => consoles;

        public void RegisterConsole(CompTransferConsole console)
        {
            if (!consoles.Contains(console))
            {
                consoles.Add(console);
            }
        }

        public void DeregisterConsole(CompTransferConsole console)
        {
            consoles.Remove(console);
        }

        // ── 施工單查詢 ───────────────────────────────────────────────────────

        /// <summary>已完工連結 + 本樁的架設單 + 他樁指向本樁的架設單（設計稿 §3-1 maxLinks「施工中的也算」）。</summary>
        public int LinkCountWithPending(CompStructuralPile pile)
        {
            int count = pile.Links.Count;
            for (int i = 0; i < pile.Orders.Count; i++)
            {
                if (!pile.Orders[i].dismantle)
                {
                    count++;
                }
            }
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile other = piles[i];
                if (other == pile)
                {
                    continue;
                }
                List<PileBeamOrder> orders = other.Orders;
                for (int j = 0; j < orders.Count; j++)
                {
                    if (!orders[j].dismantle && orders[j].target == pile.parent)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        public static bool HasOrderBetween(CompStructuralPile a, CompStructuralPile b, bool dismantle)
        {
            return a.HasOrderTo(b.parent, dismantle) || b.HasOrderTo(a.parent, dismantle);
        }

        /// <summary>取消所有他樁指向 target 的施工單（已送達材料丟回各自的施工端）。</summary>
        public void CancelOrdersTargeting(Thing target)
        {
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile p = piles[i];
                if (p.parent == target)
                {
                    continue;
                }
                for (int j = p.Orders.Count - 1; j >= 0; j--)
                {
                    if (p.Orders[j].target == target)
                    {
                        p.CancelOrder(p.Orders[j]);
                    }
                }
            }
        }

        /// <summary>目前施工單是架設單、且還缺材料的樁（倒數中的船除外）。</summary>
        public IEnumerable<Thing> PilesNeedingMaterials()
        {
            for (int i = 0; i < piles.Count; i++)
            {
                if (piles[i].NeedsMaterials && !IsLocked(piles[i]))
                {
                    yield return piles[i].parent;
                }
            }
        }

        /// <summary>目前施工單已可開工（拆除單，或材料已到齊的架設單）的樁（倒數中的船除外）。</summary>
        public IEnumerable<Thing> PilesReadyToWork()
        {
            for (int i = 0; i < piles.Count; i++)
            {
                if (piles[i].CurrentOrderReady && !IsLocked(piles[i]))
                {
                    yield return piles[i].parent;
                }
            }
        }

        public bool AnyPileNeedsMaterials()
        {
            for (int i = 0; i < piles.Count; i++)
            {
                if (piles[i].NeedsMaterials && !IsLocked(piles[i]))
                {
                    return true;
                }
            }
            return false;
        }

        public bool AnyPileReadyToWork()
        {
            for (int i = 0; i < piles.Count; i++)
            {
                if (piles[i].CurrentOrderReady && !IsLocked(piles[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // ── 生命週期 ─────────────────────────────────────────────────────────

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // 補齊單向殘缺：A 有 B、B 沒有 A 就補上
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile p = piles[i];
                for (int j = 0; j < p.Links.Count; j++)
                {
                    CompStructuralPile other = p.Links[j].TryGetComp<CompStructuralPile>();
                    if (other != null && piles.Contains(other) && !other.Links.Contains(p.parent))
                    {
                        other.AddLinkRaw(p.parent);
                    }
                }
            }
            MarkDirty();
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (Find.TickManager.TicksGame % ValidateInterval == 0)
            {
                Validate();
            }
            for (int i = consoles.Count - 1; i >= 0; i--)
            {
                if (i < consoles.Count && consoles[i].InLaunchSequence)
                {
                    consoles[i].LaunchTick();
                }
            }
        }

        /// <summary>倒數中的船：鋼樑操作鎖定、鋼樑 WorkGiver 略過（設計稿 §3-8、§4-2）。</summary>
        public bool IsLocked(CompStructuralPile pile)
        {
            for (int i = 0; i < consoles.Count; i++)
            {
                if (consoles[i].IsLockingPile(pile.parent))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 清理失效的連結與施工單：另一端已不在本地圖（被搬走、被摧毀）、陣營改變等。
        /// 走 PostDeSpawn 的正常路徑已處理大部分情況，這裡是保底。
        /// </summary>
        private void Validate()
        {
            bool changed = false;
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile p = piles[i];
                if (p.parent.Faction != Faction.OfPlayer)
                {
                    if (p.Links.Count > 0 || p.Orders.Count > 0)
                    {
                        p.ClearAllLinksAndOrders();
                        changed = true;
                    }
                    continue;
                }
                for (int j = p.Links.Count - 1; j >= 0; j--)
                {
                    Thing other = p.Links[j];
                    CompStructuralPile otherComp = other?.TryGetComp<CompStructuralPile>();
                    if (otherComp == null || !other.Spawned || other.Map != map || !piles.Contains(otherComp) || other.Faction != p.parent.Faction)
                    {
                        p.RemoveLinkRaw(other);
                        otherComp?.RemoveLinkRaw(p.parent);
                        changed = true;
                    }
                }
                for (int j = p.Orders.Count - 1; j >= 0; j--)
                {
                    PileBeamOrder order = p.Orders[j];
                    CompStructuralPile targetComp = order.TargetComp;
                    bool invalid = targetComp == null || !order.target.Spawned || order.target.Map != map || !piles.Contains(targetComp);
                    if (!invalid)
                    {
                        bool linked = p.Links.Contains(order.target);
                        invalid = order.dismantle ? !linked : linked;
                    }
                    if (invalid)
                    {
                        p.CancelOrder(order);
                    }
                }
            }
            if (changed)
            {
                MarkDirty();
            }
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (Find.CurrentMap != map || WorldRendererUtility.WorldSelected || GravshipCapturer.IsGravshipRenderInProgress)
            {
                return;
            }
            // 施工中的鋼樑常駐顯示（不只選取時），設計稿 §3-7
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile p = piles[i];
                for (int j = 0; j < p.Orders.Count; j++)
                {
                    PileDrawUtility.DrawPendingOrder(p, p.Orders[j]);
                }
            }
            // 控制台「顯示船體範圍」切換
            for (int i = 0; i < consoles.Count; i++)
            {
                if (consoles[i].ShowFootprint)
                {
                    PileNetwork net = NetworkContaining(consoles[i].parent);
                    if (net != null)
                    {
                        PileDrawUtility.DrawShipOverlay(net);
                    }
                }
            }
        }

        // ── 重建 ─────────────────────────────────────────────────────────────

        private void Rebuild()
        {
            dirty = false;
            networks.Clear();
            networkOf.Clear();

            HashSet<CompStructuralPile> registered = new HashSet<CompStructuralPile>(piles);
            Queue<CompStructuralPile> queue = new Queue<CompStructuralPile>();
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile start = piles[i];
                if (networkOf.ContainsKey(start))
                {
                    continue;
                }
                PileNetwork net = new PileNetwork(map);
                networks.Add(net);
                networkOf[start] = net;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    CompStructuralPile cur = queue.Dequeue();
                    net.piles.Add(cur);
                    for (int j = 0; j < cur.Links.Count; j++)
                    {
                        CompStructuralPile next = cur.Links[j].TryGetComp<CompStructuralPile>();
                        if (next == null || !registered.Contains(next) || networkOf.ContainsKey(next))
                        {
                            continue;
                        }
                        networkOf[next] = net;
                        queue.Enqueue(next);
                    }
                }
            }

            for (int i = 0; i < networks.Count; i++)
            {
                BuildNetworkData(networks[i], registered);
            }
            FindContestedCells();
            FindNearbyUnlinkedPiles();
        }

        private void BuildNetworkData(PileNetwork net, HashSet<CompStructuralPile> registered)
        {
            HashSet<long> seenPairs = new HashSet<long>();
            // 無地形篩選的樁覆蓋的格子一律接受地形；帶篩選的樁在第二輪只為其餘格子記錄位元遮罩
            HashSet<IntVec3> unfilteredCovered = new HashSet<IntVec3>();
            List<CompStructuralPile> filteredPiles = new List<CompStructuralPile>();
            for (int i = 0; i < net.piles.Count; i++)
            {
                CompStructuralPile p = net.piles[i];
                net.massWeight += p.Props.massWeight;

                if (p.Props.HasTerrainFilter)
                {
                    filteredPiles.Add(p);
                }
                else
                {
                    foreach (IntVec3 c in p.Props.CoverageCells(p.parent.Position, map))
                    {
                        net.covered.Add(c);
                        unfilteredCovered.Add(c);
                    }
                }

                for (int j = 0; j < p.Links.Count; j++)
                {
                    CompStructuralPile other = p.Links[j].TryGetComp<CompStructuralPile>();
                    if (other == null || !registered.Contains(other))
                    {
                        continue;
                    }
                    int idA = Mathf.Min(p.parent.thingIDNumber, other.parent.thingIDNumber);
                    int idB = Mathf.Max(p.parent.thingIDNumber, other.parent.thingIDNumber);
                    long key = ((long)idA << 32) | (uint)idB;
                    if (seenPairs.Add(key))
                    {
                        net.links.Add(new PileNetwork.Link(p, other));
                    }
                }
            }

            for (int i = 0; i < filteredPiles.Count; i++)
            {
                CompStructuralPile p = filteredPiles[i];
                int bit = net.filteredCoverProps.IndexOf(p.Props);
                if (bit < 0)
                {
                    bit = net.filteredCoverProps.Count;
                    net.filteredCoverProps.Add(p.Props);
                }
                foreach (IntVec3 c in p.Props.CoverageCells(p.parent.Position, map))
                {
                    net.covered.Add(c);
                    if (!unfilteredCovered.Contains(c))
                    {
                        net.filteredCoverMask.TryGetValue(c, out int mask);
                        net.filteredCoverMask[c] = mask | (1 << bit);
                    }
                }
            }
        }

        private void FindContestedCells()
        {
            if (networks.Count < 2)
            {
                return;
            }
            // 格子 → 第一個覆蓋它的網路（索引 + 1；0 = 尚無）
            int[] owner = new int[map.cellIndices.NumGridCells];
            for (int n = 0; n < networks.Count; n++)
            {
                PileNetwork net = networks[n];
                foreach (IntVec3 c in net.covered)
                {
                    int idx = map.cellIndices.CellToIndex(c);
                    int prev = owner[idx];
                    if (prev == 0)
                    {
                        owner[idx] = n + 1;
                    }
                    else if (prev != n + 1)
                    {
                        net.contestedCells.Add(c);
                        networks[prev - 1].contestedCells.Add(c);
                    }
                }
            }
        }

        private void FindNearbyUnlinkedPiles()
        {
            for (int n = 0; n < networks.Count; n++)
            {
                PileNetwork net = networks[n];
                for (int i = 0; i < piles.Count; i++)
                {
                    CompStructuralPile q = piles[i];
                    if (networkOf[q] == net)
                    {
                        continue;
                    }
                    for (int j = 0; j < net.piles.Count; j++)
                    {
                        CompStructuralPile p = net.piles[j];
                        if (PileLinkUtility.InLinkRange(p.Props, p.parent.Position, q.Props, q.parent.Position))
                        {
                            net.nearbyUnlinkedPiles.Add(q);
                            net.nearbyUnlinkedPileCells.AddRange(q.parent.OccupiedRect().Cells);
                            break;
                        }
                    }
                }
            }
        }
    }
}
