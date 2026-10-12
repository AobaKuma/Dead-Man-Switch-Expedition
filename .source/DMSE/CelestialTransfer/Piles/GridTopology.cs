using System;
using System.Collections.Generic;

namespace DMSE
{
    /// <summary>
    /// 方格連通性的增量維護（移植自 TopologyLab 的 IncTopology，演算法與推演見該工具的「算法与推演.md」）。
    ///
    /// 三件事撐起全部結論：
    ///   ① 繞數 = 連通塊數 − 孔洞數（平面圖歐拉公式的方格離散形式）：χ = w/4 = c − h
    ///   ② w 局部可算：每個格點看周圍 4 格，恰 1 格 +1、恰 3 格 −1、2 格對角 ±2；改一格只動它的 4 個角點
    ///   ③ 前景與背景必須互補配對（這裡固定為前景 4 鄰域 + 背景 8 鄰域：必須共邊才算相連）
    ///
    /// 不對稱解法：
    ///   · 拿掉一格 ⇒ 背景只會合併（並查集精確給 Δh）⇒ Δc = Δw/4 + Δh，只有 Δc &gt; 0 才泛洪修復前景
    ///   · 放上一格 ⇒ 前景只會合併（並查集精確給 Δc）⇒ Δh = Δc − Δw/4，背景的重排可證明無害時跳過
    ///
    /// 坐標紀律：對外 API 用內部坐標 (x, y)（0..W-1 / 0..H-1）；內部陣列用含外圈 padding 的索引
    /// i = (y+1)*Size + (x+1)。兩者不可混用。
    ///
    /// 與原工具的差異：遊戲內不拋例外——局部修復對不上帳時一律整圖重建（Compact）兜底，正確性優先。
    /// </summary>
    public sealed class GridTopology
    {
        private static readonly int[] Off4 = { 1, 0, -1, 0, 0, 1, 0, -1 };

        private static readonly int[] Off8 =
        {
            1, 0, -1, 0, 0, 1, 0, -1,
            1, 1, 1, -1, -1, 1, -1, -1,
        };

        // 前景 4 鄰域 + 背景 8 鄰域
        private static readonly int[] FgOff = Off4;

        private static readonly int[] BgOff = Off8;

        public readonly int Size, HSize, W, H;

        private readonly bool[] present;

        private readonly int cells;

        // 每格 → 並查集節點（0 = 無）
        private readonly int[] fgNode;

        private readonly int[] bgNode;

        private readonly UnionFind fg = new UnionFind();

        private readonly UnionFind bg = new UnionFind();

        private bool[] bgTouch = new bool[2048];

        private int fgCount, holes, wCorner;

        private readonly int[] mark;

        private int markGen;

        private readonly int[] queue;

        private readonly int[] fgRoots = new int[8];

        private readonly int[] bgRoots = new int[8];

        private readonly List<int> bgSeeds = new List<int>(16);

        private readonly List<int> fgSeeds = new List<int>(16);

        private readonly List<List<int>> pieces = new List<List<int>>();

        public GridTopology(int w, int h)
        {
            W = w;
            H = h;
            Size = w + 2;
            HSize = h + 2;
            cells = Size * HSize;
            present = new bool[cells];
            fgNode = new int[cells];
            bgNode = new int[cells];
            mark = new int[cells];
            queue = new int[cells];

            // 初始全空 ⇒ 整張圖（含 padding）是一個連外部的背景分量
            int root = NewBgNode();
            bgTouch[root] = true;
            for (int i = 0; i < cells; i++)
            {
                bgNode[i] = root;
            }
            bg.Size[root] = cells;
        }

        public int ComponentCount => fgCount;

        public int HoleCount => holes;

        /// <summary>內部坐標 → padded 索引（批次 API 用）。</summary>
        public int Index(int x, int y) => (y + 1) * Size + (x + 1);

        public bool Present(int x, int y) => present[Index(x, y)];

        /// <summary>前景連通塊的代表（0 = 該格不存在）。只在兩次修改之間可比較。</summary>
        public int ComponentOf(int x, int y)
        {
            int i = Index(x, y);
            return present[i] && fgNode[i] != 0 ? fg.Find(fgNode[i]) : 0;
        }

        // ------------------------------------------------------------------
        //  繞數：只影響被改動格的 4 個角點 ⇒ O(1)
        // ------------------------------------------------------------------
        private int VertexContrib(int vx, int vy)
        {
            bool a = CellAt(vx - 1, vy - 1);
            bool b = CellAt(vx, vy - 1);
            bool c = CellAt(vx - 1, vy);
            bool d = CellAt(vx, vy);
            int n = (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0);
            if (n == 1)
            {
                return 1;
            }
            if (n == 3)
            {
                return -1;
            }
            // 前景 4 鄰域：對角相碰不算連 ⇒ 兩個凸角都保留
            if (n == 2 && ((a && d) || (b && c)))
            {
                return 2;
            }
            return 0;
        }

        private bool CellAt(int px, int py)
        {
            if (px < 0 || py < 0 || px >= Size || py >= HSize)
            {
                return false;
            }
            return present[py * Size + px];
        }

        private int DeltaWForAdd(int x, int y)
        {
            int i = Index(x, y);
            bool old = present[i];
            present[i] = false;
            int before = CellVertexSum(x, y);
            present[i] = true;
            int after = CellVertexSum(x, y);
            present[i] = old;
            return after - before;
        }

        private int CellVertexSum(int x, int y)
        {
            int vx = x + 1, vy = y + 1;
            return VertexContrib(vx, vy) + VertexContrib(vx + 1, vy) + VertexContrib(vx, vy + 1) + VertexContrib(vx + 1, vy + 1);
        }

        private int NewFgNode() => fg.NewSingleton();

        private int NewBgNode()
        {
            int n = bg.NewSingleton();
            if (n >= bgTouch.Length)
            {
                Array.Resize(ref bgTouch, Math.Max(n + 1, bgTouch.Length * 2));
            }
            return n;
        }

        // ------------------------------------------------------------------
        //  放上一格（內部坐標）
        // ------------------------------------------------------------------
        public void Add(int x, int y)
        {
            int i = Index(x, y);
            if (present[i])
            {
                return;
            }

            int dw = DeltaWForAdd(x, y);
            wCorner += dw;
            present[i] = true;

            // 前景：本格拿全新節點，再與鄰居所在分量合併（精確給出 Δc）
            int me = NewFgNode();
            fgNode[i] = me;
            int k = GatherFgRoots(x, y);
            int dc;
            if (k == 0)
            {
                dc = 1;
            }
            else
            {
                for (int j = 0; j < k; j++)
                {
                    fg.Union(fgRoots[j], me);
                }
                dc = -(k - 1);
            }
            fgCount += dc;

            int dhAdd = dc - dw / 4;
            holes += dhAdd;

            // 背景：該格離開背景，舊節點留在樹裡當「穿線」
            int oldBgRoot = bg.Find(bgNode[i]);
            bgNode[i] = 0;
            bg.Size[oldBgRoot]--;
            if (bg.Size[oldBgRoot] < 0)
            {
                bg.Size[oldBgRoot] = 0;
            }

            // 可證明無害的跳過：原分量連外部且 Δh == 0 ⇒ 沒產生任何有限塊，整塊保留即可
            if (bg.Size[oldBgRoot] > 0 && !(dhAdd == 0 && bgTouch[oldBgRoot]))
            {
                int px = i % Size, py = i / Size;
                bgSeeds.Clear();
                for (int t = 0; t < BgOff.Length; t += 2)
                {
                    int nx = px + BgOff[t], ny = py + BgOff[t + 1];
                    if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize)
                    {
                        continue;
                    }
                    bgSeeds.Add(ny * Size + nx);
                }
                if (bgSeeds.Count > 0)
                {
                    BgRepair(bgSeeds);
                }
            }

            MaybeCompact();
        }

        // ------------------------------------------------------------------
        //  拿掉一格（內部坐標）
        // ------------------------------------------------------------------
        public void Remove(int x, int y)
        {
            int i = Index(x, y);
            if (!present[i])
            {
                return;
            }

            int dw = -DeltaWForAdd(x, y);
            wCorner += dw;
            present[i] = false;

            // 背景只會合併 ⇒ 精確 Δh；前景 Δc = Δw/4 + Δh（合法範圍 [−1, +3]）
            int dh = BgAttach(i);
            holes += dh;
            int dc = dw / 4 + dh;
            fgCount += dc;

            fgNode[i] = 0;
            if (dc > 0 && FgRepairSplit(x, y) != dc)
            {
                CompactAll();
                return;
            }

            MaybeCompact();
        }

        /// <summary>一次放多格（padded 索引），整批只做一輪背景修復。</summary>
        public void AddBatch(List<int> indices)
        {
            bgSeeds.Clear();
            for (int c = 0; c < indices.Count; c++)
            {
                int i = indices[c];
                if (present[i])
                {
                    continue;
                }
                int x = i % Size, y = i / Size;
                int dw = DeltaWForAdd(x - 1, y - 1);
                wCorner += dw;
                present[i] = true;

                int me = NewFgNode();
                fgNode[i] = me;
                int k = GatherFgRoots(x - 1, y - 1);
                int dc;
                if (k == 0)
                {
                    dc = 1;
                }
                else
                {
                    for (int j = 0; j < k; j++)
                    {
                        fg.Union(fgRoots[j], me);
                    }
                    dc = -(k - 1);
                }
                fgCount += dc;
                holes += dc - dw / 4;

                int oldBgRoot = bg.Find(bgNode[i]);
                bgNode[i] = 0;
                bg.Size[oldBgRoot]--;
                if (bg.Size[oldBgRoot] < 0)
                {
                    bg.Size[oldBgRoot] = 0;
                }

                for (int t = 0; t < BgOff.Length; t += 2)
                {
                    int nx = x + BgOff[t], ny = y + BgOff[t + 1];
                    if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize)
                    {
                        continue;
                    }
                    int ni = ny * Size + nx;
                    if (!present[ni])
                    {
                        bgSeeds.Add(ni);
                    }
                }
            }
            if (bgSeeds.Count > 0)
            {
                BgRepair(bgSeeds);
            }
            MaybeCompact();
        }

        /// <summary>一次拿掉多格（padded 索引），整批只做一輪前景修復。</summary>
        public void RemoveBatch(List<int> indices)
        {
            fgSeeds.Clear();
            int dcTotal = 0;
            // 正的部分之和 = 一共斷了幾次。不能看淨值：「拿掉孤塊(−1)」與「切斷鏈條(+1)」會互相抵消
            int dcPositive = 0;
            for (int c = 0; c < indices.Count; c++)
            {
                int i = indices[c];
                if (!present[i])
                {
                    continue;
                }
                int x = i % Size, y = i / Size;
                int dw = -DeltaWForAdd(x - 1, y - 1);
                wCorner += dw;
                present[i] = false;
                int dh = BgAttach(i);
                holes += dh;
                int dcCell = dw / 4 + dh;
                dcTotal += dcCell;
                if (dcCell > 0)
                {
                    dcPositive += dcCell;
                }

                fgNode[i] = 0;
                for (int t = 0; t < FgOff.Length; t += 2)
                {
                    int nx = x + FgOff[t], ny = y + FgOff[t + 1];
                    if (nx < 1 || ny < 1 || nx > W || ny > H)
                    {
                        continue;
                    }
                    int ni = ny * Size + nx;
                    if (present[ni])
                    {
                        fgSeeds.Add(ni);
                    }
                }
            }
            fgCount += dcTotal;
            if (dcPositive > 0 && FgRepairBatchSplit(fgSeeds) != dcPositive)
            {
                // 局部修復對不上帳 ⇒ 兜底整圖重建
                CompactFg();
            }
            MaybeCompact();
        }

        private int GatherFgRoots(int x, int y)
        {
            int k = 0;
            for (int t = 0; t < FgOff.Length; t += 2)
            {
                int nx = x + FgOff[t], ny = y + FgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H)
                {
                    continue;
                }
                int ni = Index(nx, ny);
                if (!present[ni] || fgNode[ni] == 0)
                {
                    continue;
                }
                int r = fg.Find(fgNode[ni]);
                bool dup = false;
                for (int j = 0; j < k; j++)
                {
                    if (fgRoots[j] == r)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                {
                    fgRoots[k++] = r;
                }
            }
            return k;
        }

        /// <summary>背景：把一格並入（padded 索引；只會合併 ⇒ 精確 Δh）。</summary>
        private int BgAttach(int i)
        {
            int me = NewBgNode();
            bgNode[i] = me;
            int x = i % Size, y = i / Size;
            int kr = 0;
            for (int t = 0; t < BgOff.Length; t += 2)
            {
                int nx = x + BgOff[t], ny = y + BgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize)
                {
                    continue;
                }
                int ni = ny * Size + nx;
                if (present[ni] || bgNode[ni] == 0)
                {
                    continue;
                }
                int r = bg.Find(bgNode[ni]);
                bool dup = false;
                for (int j = 0; j < kr; j++)
                {
                    if (bgRoots[j] == r)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                {
                    bgRoots[kr++] = r;
                }
            }

            if (kr == 0)
            {
                bgTouch[me] = false;
                return 1;
            }

            int finiteBefore = 0;
            bool touch = false;
            for (int j = 0; j < kr; j++)
            {
                if (!bgTouch[bgRoots[j]])
                {
                    finiteBefore++;
                }
                touch |= bgTouch[bgRoots[j]];
            }
            for (int j = 0; j < kr; j++)
            {
                bg.Union(bgRoots[j], me);
            }
            bgTouch[bg.Find(me)] = touch;
            return (touch ? 0 : 1) - finiteBefore;
        }

        /// <summary>背景修復：兩階段——先只泛洪標記、再統一換節點（中途改指針會破壞泛洪自己的 Find 判據）。</summary>
        private void BgRepair(List<int> seeds)
        {
            markGen++;
            for (int s = 0; s < seeds.Count; s++)
            {
                int start = seeds[s];
                if (present[start] || bgNode[start] == 0 || mark[start] == markGen)
                {
                    continue;
                }
                int oldRoot = bg.Find(bgNode[start]);

                int head = 0, tail = 0;
                queue[tail++] = start;
                mark[start] = markGen;
                bool touch = false;
                int count = 0;
                while (head < tail)
                {
                    int cur = queue[head++];
                    count++;
                    int cx = cur % Size, cy = cur / Size;
                    if (cx == 0 || cy == 0 || cx == Size - 1 || cy == HSize - 1)
                    {
                        touch = true;
                    }
                    for (int t = 0; t < BgOff.Length; t += 2)
                    {
                        int nx = cx + BgOff[t], ny = cy + BgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize)
                        {
                            continue;
                        }
                        int ni = ny * Size + nx;
                        if (present[ni] || bgNode[ni] == 0 || mark[ni] == markGen || bg.Find(bgNode[ni]) != oldRoot)
                        {
                            continue;
                        }
                        mark[ni] = markGen;
                        queue[tail++] = ni;
                    }
                }

                int fresh = NewBgNode();
                bgTouch[fresh] = touch;
                bg.Size[fresh] = count;
                for (int q = 0; q < tail; q++)
                {
                    bgNode[queue[q]] = fresh;
                }
                bg.Size[oldRoot] -= count;
                if (bg.Size[oldRoot] < 0)
                {
                    bg.Size[oldRoot] = 0;
                }
            }
        }

        /// <summary>前景斷裂修復：從被拿掉格的鄰居各自泛洪，每塊換新節點。回傳實際斷出的塊數（應等於 Δc）。</summary>
        private int FgRepairSplit(int x, int y)
        {
            pieces.Clear();
            markGen++;
            for (int t = 0; t < FgOff.Length; t += 2)
            {
                int nx = x + FgOff[t], ny = y + FgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H)
                {
                    continue;
                }
                int ni = Index(nx, ny);
                if (!present[ni] || mark[ni] == markGen)
                {
                    continue;
                }
                pieces.Add(FloodFgPiece(ni));
            }

            int splits = pieces.Count - 1;
            if (splits <= 0)
            {
                return 0;
            }
            for (int p = 0; p < pieces.Count; p++)
            {
                List<int> piece = pieces[p];
                int node = NewFgNode();
                fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++)
                {
                    fgNode[piece[c]] = node;
                }
            }
            return splits;
        }

        private int FgRepairBatchSplit(List<int> seeds)
        {
            markGen++;
            List<int> groupRoot = new List<int>();
            List<int> groupPieces = new List<int>();
            for (int s = 0; s < seeds.Count; s++)
            {
                int start = seeds[s];
                if (!present[start] || mark[start] == markGen)
                {
                    continue;
                }
                int oldRoot = fg.Find(fgNode[start]);
                List<int> piece = FloodFgPiece(start);
                int g = groupRoot.IndexOf(oldRoot);
                if (g < 0)
                {
                    groupRoot.Add(oldRoot);
                    groupPieces.Add(0);
                    g = groupRoot.Count - 1;
                }
                groupPieces[g]++;

                // 泛洪只依賴 present + mark，可以立刻換節點
                int node = NewFgNode();
                fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++)
                {
                    fgNode[piece[c]] = node;
                }
            }
            int splits = 0;
            for (int g = 0; g < groupRoot.Count; g++)
            {
                splits += groupPieces[g] - 1;
            }
            return splits;
        }

        private List<int> FloodFgPiece(int start)
        {
            List<int> piece = new List<int>(64);
            int head = 0, tail = 0;
            queue[tail++] = start;
            mark[start] = markGen;
            while (head < tail)
            {
                int cur = queue[head++];
                piece.Add(cur);
                int cx = cur % Size, cy = cur / Size;
                for (int k = 0; k < FgOff.Length; k += 2)
                {
                    int ax = cx + FgOff[k], ay = cy + FgOff[k + 1];
                    if (ax < 0 || ay < 0 || ax >= Size || ay >= HSize)
                    {
                        continue;
                    }
                    int ai = ay * Size + ax;
                    if (!present[ai] || mark[ai] == markGen)
                    {
                        continue;
                    }
                    mark[ai] = markGen;
                    queue[tail++] = ai;
                }
            }
            return piece;
        }

        // ------------------------------------------------------------------
        //  兜底：整圖重建並查集（O(格子數)）。節點只增不減，超過 4×格數時回收
        // ------------------------------------------------------------------
        private void MaybeCompact()
        {
            if (fg.NodeCount > 4 * cells || bg.NodeCount > 4 * cells)
            {
                CompactAll();
            }
        }

        public void CompactAll()
        {
            CompactFg();
            CompactBg();
        }

        private void CompactFg()
        {
            fg.Reset();
            for (int i = 0; i < cells; i++)
            {
                fgNode[i] = 0;
            }
            markGen++;
            int comps = 0;
            for (int i = 0; i < cells; i++)
            {
                if (!present[i] || fgNode[i] != 0)
                {
                    continue;
                }
                List<int> piece = FloodFgPiece(i);
                int node = NewFgNode();
                fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++)
                {
                    fgNode[piece[c]] = node;
                }
                comps++;
            }
            fgCount = comps;
        }

        private void CompactBg()
        {
            bg.Reset();
            bgTouch = new bool[2048];
            for (int i = 0; i < cells; i++)
            {
                bgNode[i] = 0;
            }
            markGen++;
            int holeCount = 0;
            List<int> compCells = new List<int>(64);
            for (int i = 0; i < cells; i++)
            {
                if (present[i] || bgNode[i] != 0 || mark[i] == markGen)
                {
                    continue;
                }
                int head = 0, tail = 0;
                queue[tail++] = i;
                mark[i] = markGen;
                bool touch = false;
                compCells.Clear();
                while (head < tail)
                {
                    int cur = queue[head++];
                    compCells.Add(cur);
                    int cx = cur % Size, cy = cur / Size;
                    if (cx == 0 || cy == 0 || cx == Size - 1 || cy == HSize - 1)
                    {
                        touch = true;
                    }
                    for (int t = 0; t < BgOff.Length; t += 2)
                    {
                        int nx = cx + BgOff[t], ny = cy + BgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize)
                        {
                            continue;
                        }
                        int ni = ny * Size + nx;
                        if (present[ni] || mark[ni] == markGen)
                        {
                            continue;
                        }
                        mark[ni] = markGen;
                        queue[tail++] = ni;
                    }
                }
                int node = NewBgNode();
                bgTouch[node] = touch;
                bg.Size[node] = compCells.Count;
                for (int c = 0; c < compCells.Count; c++)
                {
                    bgNode[compCells[c]] = node;
                }
                if (!touch)
                {
                    holeCount++;
                }
            }
            // 原工具在此對帳不符時拋例外；遊戲內以重建結果為準
            holes = holeCount;
        }

        /// <summary>並查集：節點只增不減（不支援刪除），每次「放上一格」給全新節點，永不複用。</summary>
        private sealed class UnionFind
        {
            public int[] Parent = new int[1024];

            public int[] Size = new int[1024];

            public int NodeCount;

            public int NewSingleton()
            {
                if (NodeCount == 0)
                {
                    NodeCount = 1; // 0 保留給「無節點」
                }
                if (NodeCount >= Parent.Length)
                {
                    Array.Resize(ref Parent, Parent.Length * 2);
                    Array.Resize(ref Size, Size.Length * 2);
                }
                int p = NodeCount++;
                Parent[p] = p;
                Size[p] = 1;
                return p;
            }

            public int Find(int i)
            {
                int root = i;
                while (Parent[root] != root)
                {
                    root = Parent[root];
                }
                while (Parent[i] != root)
                {
                    int nx = Parent[i];
                    Parent[i] = root;
                    i = nx;
                }
                return root;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb)
                {
                    return;
                }
                if (Size[ra] < Size[rb])
                {
                    int t = ra;
                    ra = rb;
                    rb = t;
                }
                Parent[rb] = ra;
                Size[ra] += Size[rb];
                Size[rb] = 0;
            }

            public void Reset()
            {
                NodeCount = 0;
            }
        }
    }
}
