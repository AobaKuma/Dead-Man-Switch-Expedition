using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>平台支柱的設定（掛在支柱的 ThingDef 上）。柱身是往下畫的背景（<see cref="SectionLayer_SeaPlatformPillars"/>）。</summary>
    public class SeaPlatformPillarExtension : DefModExtension
    {
        /// <summary>柱身貼圖（畫在海面之上、甲板之下）。</summary>
        public string pillarTexPath;

        /// <summary>柱身大小（格）。</summary>
        public Vector2 pillarDrawSize = new Vector2(11f, 18.333f);

        /// <summary>柱身中心相對支柱中心的南北偏移（格）。</summary>
        public float pillarOffsetZ = -10f;

        /// <summary>支撐半徑（格，從支柱中心算的圓）：範圍內才能鋪平台，範圍外的平台會崩塌。</summary>
        public float supportRadius = 27.9f;

        /// <summary>入水處白沫的寬度（格）：柱身下方混凝土基座的寬。</summary>
        public int waterlineWidth = 9;

        /// <summary>支柱蓋好時在周圍開出的平台邊長（已經是平台的格子不動）。</summary>
        public int startPlatformSize = 5;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (pillarTexPath.NullOrEmpty())
            {
                yield return "pillarTexPath is empty";
            }
            if (supportRadius <= 0f)
            {
                yield return "supportRadius must be positive";
            }
        }
    }

    /// <summary>柱身位置、入水處、支撐範圍：純函式。</summary>
    public static class SeaPlatformGeometry
    {
        /// <summary>柱身貼圖裡入水處（混凝土基座中段）在從上往下的幾成位置。</summary>
        public const float WaterlineFraction = 0.86f;

        public static Rect PillarRect(float centerX, float centerZ, Vector2 size, float offsetZ)
        {
            return new Rect(centerX - size.x / 2f, centerZ + offsetZ - size.y / 2f, size.x, size.y);
        }

        /// <summary>入水處在海面座標裡的格子：支柱正下方，寬 <paramref name="width"/>、兩排。</summary>
        public static IEnumerable<IntVec3> WaterlineCells(IntVec3 basePos, int width)
        {
            int minX = basePos.x - width / 2;
            for (int z = basePos.z - 1; z <= basePos.z; z++)
            {
                for (int x = minX; x < minX + width; x++)
                {
                    yield return new IntVec3(x, 0, z);
                }
            }
        }

        public static bool Supports(Vector3 baseCenter, IntVec3 cell, float radius)
        {
            float dx = cell.x + 0.5f - baseCenter.x;
            float dz = cell.z + 0.5f - baseCenter.z;
            return dx * dx + dz * dz <= radius * radius;
        }

        public const int SplashChunk = 5;

        public static List<(Vector2 center, int count)> SplashGroups(IEnumerable<IntVec3> cells, int chunk)
        {
            Dictionary<(int, int), (Vector2 sum, int count)> groups = new Dictionary<(int, int), (Vector2, int)>();
            foreach (IntVec3 c in cells)
            {
                (int, int) key = (Mathf.FloorToInt(c.x / (float)chunk), Mathf.FloorToInt(c.z / (float)chunk));
                groups.TryGetValue(key, out (Vector2 sum, int count) g);
                groups[key] = (g.sum + new Vector2(c.x + 0.5f, c.z + 0.5f), g.count + 1);
            }
            return groups.Values.Select(g => (g.sum / g.count, g.count)).ToList();
        }

        public static float SplashScale(int count)
        {
            return Mathf.Clamp(Mathf.Sqrt(count) / 2f, 1f, 2.5f);
        }

        /// <summary>把柱身四邊形裁進一個區塊，回傳裁過的四邊形與對應的貼圖範圍；沒有交集時回 false。</summary>
        public static bool Clip(Rect pillar, Rect section, out Rect quad, out Rect uv)
        {
            float x0 = Mathf.Max(pillar.xMin, section.xMin);
            float x1 = Mathf.Min(pillar.xMax, section.xMax);
            float z0 = Mathf.Max(pillar.yMin, section.yMin);
            float z1 = Mathf.Min(pillar.yMax, section.yMax);
            if (x1 <= x0 || z1 <= z0)
            {
                quad = default;
                uv = default;
                return false;
            }
            quad = Rect.MinMaxRect(x0, z0, x1, z1);
            uv = Rect.MinMaxRect((x0 - pillar.xMin) / pillar.width, (z0 - pillar.yMin) / pillar.height,
                (x1 - pillar.xMin) / pillar.width, (z1 - pillar.yMin) / pillar.height);
            return true;
        }
    }

    /// <summary>
    /// 平台支柱：建在高空（或已有的平台）上的建築，往下畫一根柱身，並支撐半徑內的平台。
    /// 和 DepthRim 不同，沒有水面層，所以支柱不是升降梯，直接蓋在平台地圖上；蓋好時在周圍開出一小塊平台。
    /// </summary>
    public class Building_SeaPlatformPillar : Building
    {
        public SeaPlatformPillarExtension Ext => def.GetModExtension<SeaPlatformPillarExtension>();

        public Rect PillarRect
        {
            get
            {
                SeaPlatformPillarExtension ext = Ext;
                Vector3 c = this.TrueCenter();
                return SeaPlatformGeometry.PillarRect(c.x, c.z, ext.pillarDrawSize, ext.pillarOffsetZ);
            }
        }

        public bool Supports(IntVec3 cell)
        {
            return SeaPlatformGeometry.Supports(this.TrueCenter(), cell, Ext.supportRadius);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            if (!respawningAfterLoad)
            {
                OpenStartPlatform(map);
            }
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<MapComponent_SeaPlatformSupport>()?.Notify_PillarSpawned(this);
            DirtyPillar(map, PillarRect);
        }

        /// <summary>在支柱周圍（含佔地）的高空鋪平台；已經是平台或別的地形的格子不動。</summary>
        private void OpenStartPlatform(Map map)
        {
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(map);
            if (ext == null)
            {
                return;
            }
            int size = Mathf.Max(Ext.startPlatformSize, Mathf.Max(def.size.x, def.size.z));
            foreach (IntVec3 c in CellRect.CenteredOn(Position, size / 2).ClipInsideMap(map))
            {
                if (map.terrainGrid.TerrainAt(c) == ext.voidTerrain)
                {
                    map.terrainGrid.SetTerrain(c, ext.deckTerrain);
                }
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            Rect rect = PillarRect;
            IntVec3 pos = Position;
            base.DeSpawn(mode);
            DirtyPillar(map, rect);
            // 被打壞（KillFinalize）、拆除或任何方式移除都一樣：撐不住的平台進入崩塌倒數
            map.GetComponent<MapComponent_SeaPlatformSupport>()?.Notify_PillarRemoved(this, pos, destroyed: mode == DestroyMode.KillFinalize);
        }

        /// <summary>柱身蓋到的每個區塊都要重建。</summary>
        private static void DirtyPillar(Map map, Rect rect)
        {
            for (int x = Mathf.FloorToInt(rect.xMin); x < Mathf.CeilToInt(rect.xMax); x++)
            {
                for (int z = Mathf.FloorToInt(rect.yMin); z < Mathf.CeilToInt(rect.yMax); z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (c.InBounds(map))
                    {
                        map.mapDrawer.MapMeshDirty(c, (ulong)MapMeshFlagDefOf.Buildings);
                    }
                }
            }
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            GenDraw.DrawRadiusRing(Position, Ext.supportRadius);
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string line = "DMSE.SeaPlatform.SupportRadius".Translate(Ext.supportRadius.ToString("0"));
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }
    }

    /// <summary>
    /// 支柱的放置：只能在海上平台地圖；佔地每一格是高空或平台；旁邊至少一格能站人（才有人能蓋）；放置時畫出支撐範圍。
    /// </summary>
    public class PlaceWorker_SeaPlatformPillar : PlaceWorker
    {
        public override bool IsBuildDesignatorVisible(BuildableDef def)
        {
            return SeaPlatformUtility.IsSeaPlatformMap(Find.CurrentMap);
        }

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(map);
            if (ext == null || !(checkingDef is ThingDef def))
            {
                return new AcceptanceReport("DMSE.SeaPlatform.OnlySeaPlatformMap".Translate());
            }
            CellRect rect = GenAdj.OccupiedRect(loc, rot, def.size);
            foreach (IntVec3 c in rect)
            {
                TerrainDef t = c.InBounds(map) ? map.terrainGrid.BaseTerrainAt(c) : null;
                if (t != ext.voidTerrain && t != ext.deckTerrain)
                {
                    return new AcceptanceReport("DMSE.SeaPlatform.PillarNeedsOpenAir".Translate());
                }
            }
            if (DebugSettings.godMode)
            {
                return AcceptanceReport.WasAccepted;
            }
            foreach (IntVec3 c in rect.ExpandedBy(1).EdgeCells)
            {
                if (c.InBounds(map) && c.Standable(map))
                {
                    return AcceptanceReport.WasAccepted;
                }
            }
            return new AcceptanceReport("DMSE.SeaPlatform.PillarNeedsAccess".Translate());
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            SeaPlatformPillarExtension ext = def.GetModExtension<SeaPlatformPillarExtension>();
            if (ext != null)
            {
                GenDraw.DrawRadiusRing(center, ext.supportRadius);
            }
        }
    }

    /// <summary>平台只能鋪在某根支柱的支撐範圍內；建築分頁只在海上平台地圖顯示；放置時畫出所有支柱的範圍。</summary>
    public class PlaceWorker_SeaPlatformSupported : PlaceWorker
    {
        public override bool IsBuildDesignatorVisible(BuildableDef def)
        {
            return SeaPlatformUtility.IsSeaPlatformMap(Find.CurrentMap);
        }

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            // 放置只看支撐半徑：一次拖拉的整片藍圖彼此還沒連上，連通與否等蓋好後由崩塌檢查決定
            MapComponent_SeaPlatformSupport support = map.GetComponent<MapComponent_SeaPlatformSupport>();
            if (support != null && support.InRange(loc))
            {
                return AcceptanceReport.WasAccepted;
            }
            return new AcceptanceReport("DMSE.SeaPlatform.NeedsSupport".Translate());
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            MapComponent_SeaPlatformSupport support = Find.CurrentMap?.GetComponent<MapComponent_SeaPlatformSupport>();
            if (support == null)
            {
                return;
            }
            foreach (Building_SeaPlatformPillar b in support.Pillars)
            {
                GenDraw.DrawRadiusRing(b.Position, b.Ext.supportRadius);
            }
        }
    }

    /// <summary>
    /// 平台支撐：一格平台要「在某根支柱的支撐半徑內」而且「以共邊相連的方式連到某根支柱底下的平台」才撐得住
    /// （連通用 <see cref="GridTopology"/> 增量維護）。撐不住的平台進入 15 秒倒數（紅框），倒數結束就塌進海裡：
    /// 上面的建築與物品沒了，Pawn 受傷爬回最近撐得住的平台（附近沒有就落海失蹤），海面上濺起水花。
    /// 只在海上平台地圖上運作。
    /// </summary>
    public class MapComponent_SeaPlatformSupport : MapComponent
    {
        public const int CollapseDelayTicks = 900;

        public const int MaxSplashes = 12;

        public const float PillarSplashScale = 2.5f;

        /// <summary>結構變動後多久檢查一次（tick）：同一批施工或崩塌的變動合併成一次檢查。</summary>
        public const int StructureCheckInterval = 10;

        private readonly List<Building_SeaPlatformPillar> pillars = new List<Building_SeaPlatformPillar>();

        private Dictionary<IntVec3, int> pending = new Dictionary<IntVec3, int>();

        private GridTopology topology;

        private readonly HashSet<IntVec3> dirtyCells = new HashSet<IntVec3>();

        private readonly List<int> topoAdds = new List<int>();

        private readonly List<int> topoRemoves = new List<int>();

        private readonly HashSet<int> anchorComponents = new HashSet<int>();

        private bool anchorsDirty = true;

        private bool structureDirty;

        private List<IntVec3> tmpKeys;
        private List<int> tmpValues;

        public MapComponent_SeaPlatformSupport(Map map) : base(map)
        {
        }

        public List<Building_SeaPlatformPillar> Pillars => pillars;

        public int PendingCount => pending.Count;

        private SeaPlatformMapExtension Ext => SeaPlatformUtility.Ext(map);

        public bool InRange(IntVec3 c)
        {
            for (int i = 0; i < pillars.Count; i++)
            {
                if (pillars[i].Spawned && pillars[i].Supports(c))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsConnected(IntVec3 c)
        {
            SyncTopology();
            if (anchorsDirty)
            {
                RebuildAnchors();
            }
            int comp = topology.ComponentOf(c.x, c.z);
            return comp != 0 && anchorComponents.Contains(comp);
        }

        public bool IsSupported(IntVec3 c)
        {
            return InRange(c) && IsConnected(c);
        }

        /// <summary>地形變了（<see cref="Patch_SeaPlatformStructureChanged"/>）：記下這格，下一次檢查時更新連通塊。</summary>
        public void Notify_TerrainChanged(IntVec3 c)
        {
            if (topology == null)
            {
                return;
            }
            dirtyCells.Add(c);
            structureDirty = true;
        }

        private void SyncTopology()
        {
            SeaPlatformMapExtension ext = Ext;
            if (topology == null)
            {
                topology = new GridTopology(map.Size.x, map.Size.z);
                topoAdds.Clear();
                foreach (IntVec3 c in map.AllCells)
                {
                    if (SeaPlatformUtility.IsDeck(c, map, ext))
                    {
                        topoAdds.Add(topology.Index(c.x, c.z));
                    }
                }
                if (topoAdds.Count > 0)
                {
                    topology.AddBatch(topoAdds);
                }
                dirtyCells.Clear();
                anchorsDirty = true;
                return;
            }
            if (dirtyCells.Count == 0)
            {
                return;
            }
            topoAdds.Clear();
            topoRemoves.Clear();
            foreach (IntVec3 c in dirtyCells)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                bool want = SeaPlatformUtility.IsDeck(c, map, ext);
                if (want != topology.Present(c.x, c.z))
                {
                    (want ? topoAdds : topoRemoves).Add(topology.Index(c.x, c.z));
                }
            }
            dirtyCells.Clear();
            if (topoRemoves.Count > 0)
            {
                topology.RemoveBatch(topoRemoves);
            }
            if (topoAdds.Count > 0)
            {
                topology.AddBatch(topoAdds);
            }
            if (topoAdds.Count > 0 || topoRemoves.Count > 0)
            {
                anchorsDirty = true;
            }
        }

        private void RebuildAnchors()
        {
            anchorComponents.Clear();
            foreach (Building_SeaPlatformPillar b in pillars)
            {
                if (!b.Spawned)
                {
                    continue;
                }
                // 佔地外加一圈：支柱底下的甲板被拆掉（「拆除平台」）時，緊貼支柱的平台仍然算接在支柱上；
                // 平台失去支撐的條件只有「支柱本身沒了」或「和支柱斷開」
                foreach (IntVec3 c in b.OccupiedRect().ExpandedBy(1))
                {
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    int comp = topology.ComponentOf(c.x, c.z);
                    if (comp != 0)
                    {
                        anchorComponents.Add(comp);
                    }
                }
            }
            anchorsDirty = false;
        }

        public void Notify_PillarSpawned(Building_SeaPlatformPillar b)
        {
            if (!pillars.Contains(b))
            {
                pillars.Add(b);
            }
            anchorsDirty = true;
            ReleaseSupported();
            map.GetComponent<MapComponent_SeaPlatformSea>()?.MarkDirty();
        }

        /// <summary>
        /// 支柱沒了（被打壞或拆掉）：立刻重新判定支撐，撐不住的平台進入崩塌倒數（不等下一次結構檢查）。
        /// <paramref name="destroyed"/> 為 true（被打壞）時倒數的訊息改成「支柱被摧毀」。
        /// </summary>
        public void Notify_PillarRemoved(Building_SeaPlatformPillar b, IntVec3 pos, bool destroyed = false)
        {
            pillars.Remove(b);
            anchorsDirty = true;
            Splash(pos, PillarSplashScale);
            map.GetComponent<MapComponent_SeaPlatformSea>()?.MarkDirty();
            CheckSupport(destroyed ? "DMSE.SeaPlatform.PillarDestroyed" : null, pos);
        }

        private void ReleaseSupported()
        {
            if (pending.Count == 0)
            {
                return;
            }
            foreach (IntVec3 c in pending.Keys.ToList())
            {
                if (IsSupported(c))
                {
                    pending.Remove(c);
                }
            }
        }

        /// <summary>找出撐不住的平台，開始崩塌倒數；又撐得住的取消倒數。回傳新加入倒數的格數。</summary>
        /// <param name="messageKey">倒數訊息的翻譯鍵（參數：格數、秒數）；null 用一般的「失去支撐」。</param>
        /// <param name="lookAt">訊息指向的位置；無效時指向倒數中的格子。</param>
        public int CheckSupport(string messageKey = null, IntVec3 lookAt = default)
        {
            structureDirty = false;
            SeaPlatformMapExtension ext = Ext;
            if (ext == null)
            {
                return 0;
            }
            ReleaseSupported();
            int due = Find.TickManager.TicksGame + CollapseDelayTicks;
            List<IntVec3> added = new List<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                if (map.terrainGrid.BaseTerrainAt(c) != ext.deckTerrain || pending.ContainsKey(c) || IsSupported(c))
                {
                    continue;
                }
                pending[c] = due;
                added.Add(c);
            }
            if (added.Count > 0)
            {
                IntVec3 look = lookAt.IsValid && lookAt != default && lookAt.InBounds(map) ? lookAt : added[added.Count / 2];
                Messages.Message((messageKey ?? "DMSE.SeaPlatform.Unsupported").Translate(added.Count, (CollapseDelayTicks / 60).ToString()),
                    new LookTargets(new TargetInfo(look, map)), MessageTypeDefOf.ThreatBig);
            }
            return added.Count;
        }

        public override void MapComponentTick()
        {
            if (structureDirty && Find.TickManager.TicksGame % StructureCheckInterval == 0)
            {
                CheckSupport();
            }
            if (pending.Count == 0 || Find.TickManager.TicksGame % 30 != 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            List<IntVec3> dueCells = null;
            foreach (KeyValuePair<IntVec3, int> kv in pending)
            {
                if (kv.Value <= now)
                {
                    dueCells ??= new List<IntVec3>();
                    dueCells.Add(kv.Key);
                }
            }
            if (dueCells != null)
            {
                Collapse(dueCells);
            }
        }

        public override void MapComponentUpdate()
        {
            if (pending.Count > 0 && Find.CurrentMap == map)
            {
                GenDraw.DrawFieldEdges(pending.Keys.ToList(), Color.red);
            }
        }

        /// <summary>這些格子塌進海裡：上面的東西沒了（Pawn 另外處理），地形變回高空，海面濺起水花。</summary>
        public void Collapse(List<IntVec3> cells)
        {
            SeaPlatformMapExtension ext = Ext;
            if (ext == null)
            {
                return;
            }
            HashSet<IntVec3> falling = new HashSet<IntVec3>(cells);
            List<Thing> tmp = new List<Thing>();
            foreach (IntVec3 c in cells)
            {
                pending.Remove(c);
                tmp.Clear();
                tmp.AddRange(c.GetThingList(map));
                foreach (Thing t in tmp)
                {
                    if (t.Destroyed || !t.Spawned)
                    {
                        continue;
                    }
                    if (t is Pawn pawn)
                    {
                        Fall(pawn, falling);
                    }
                    else if (t.def.destroyable)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                    else
                    {
                        t.DeSpawn(DestroyMode.Vanish);
                    }
                }
            }
            foreach (IntVec3 c in cells)
            {
                map.terrainGrid.SetTerrain(c, ext.voidTerrain);
            }
            foreach ((Vector2 center, int count) in SeaPlatformGeometry.SplashGroups(cells, SeaPlatformGeometry.SplashChunk)
                         .OrderByDescending(g => g.count).Take(MaxSplashes))
            {
                Splash(center, SeaPlatformGeometry.SplashScale(count));
            }
            Messages.Message("DMSE.SeaPlatform.Collapsed".Translate(cells.Count), new LookTargets(new TargetInfo(cells[0], map)), MessageTypeDefOf.NegativeEvent);
        }

        /// <summary>Pawn 隨平台掉下去：爬回最近還撐得住的平台並受傷；附近沒有就落海失蹤（沒有水面層可以落）。</summary>
        private void Fall(Pawn pawn, HashSet<IntVec3> falling)
        {
            IntVec3 from = pawn.Position;
            IntVec3 safe = IntVec3.Invalid;
            for (int i = 0; i < GenRadial.NumCellsInRadius(20f); i++)
            {
                IntVec3 c = from + GenRadial.RadialPattern[i];
                if (c.InBounds(map) && !falling.Contains(c) && !pending.ContainsKey(c) && c.Standable(map))
                {
                    safe = c;
                    break;
                }
            }
            Splash(from);
            if (!safe.IsValid)
            {
                Messages.Message("DMSE.SeaPlatform.PawnLost".Translate(pawn.LabelShort, pawn), new LookTargets(new TargetInfo(from, map)), MessageTypeDefOf.PawnDeath);
                pawn.Kill(null);
                if (pawn.Corpse != null && !pawn.Corpse.Destroyed)
                {
                    pawn.Corpse.Destroy();
                }
                return;
            }
            pawn.pather?.StopDead();
            pawn.Position = safe;
            pawn.Notify_Teleported(true, true);
            pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Rand.Range(8f, 16f)));
            Messages.Message("DMSE.SeaPlatform.PawnFell".Translate(pawn.LabelShort, pawn), new LookTargets(pawn), MessageTypeDefOf.NegativeHealthEvent);
        }

        public bool Splash(IntVec3 deckCell, float scale = 1f)
        {
            return Splash(new Vector2(deckCell.x + 0.5f, deckCell.z + 0.5f), scale);
        }

        /// <summary>甲板上的點正下方的海面濺水花（海面在畫面上往南偏 seaDrop 格）。落點被平台蓋住或在地圖外就不畫。</summary>
        public bool Splash(Vector2 deckPoint, float scale)
        {
            SeaPlatformMapExtension ext = Ext;
            if (ext == null)
            {
                return false;
            }
            Vector2 point = deckPoint - new Vector2(0f, ext.seaDrop);
            IntVec3 spot = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.y));
            if (!SeaPlatformUtility.ShowsSea(spot, map, ext))
            {
                return false;
            }
            EffecterDef def = DefDatabase<EffecterDef>.GetNamedSilentFail("PawnEmergeFromWaterLarge");
            if (def == null)
            {
                return false;
            }
            Vector3 offset = new Vector3(point.x - (spot.x + 0.5f), 0f, point.y - (spot.z + 0.5f));
            Effecter effecter = def.Spawn(spot, map, offset, scale);
            map.effecterMaintainer.AddEffecterToMaintain(effecter, spot, 60);
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "pendingCollapse", LookMode.Value, LookMode.Value, ref tmpKeys, ref tmpValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pending ??= new Dictionary<IntVec3, int>();
            }
        }
    }

    /// <summary>海上平台地圖的地形在甲板與高空之間變動（鋪好、拆掉、崩塌）時：通知支撐檢查，並讓海面遮罩重建。</summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class Patch_SeaPlatformStructureChanged
    {
        [HarmonyPostfix]
        public static void Postfix(IntVec3 c, TerrainDef newTerr, Map ___map)
        {
            if (___map == null || ___map.generatorDef == null)
            {
                return;
            }
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(___map);
            if (ext == null || (newTerr != ext.deckTerrain && newTerr != ext.voidTerrain))
            {
                return;
            }
            ___map.GetComponent<MapComponent_SeaPlatformSupport>()?.Notify_TerrainChanged(c);
        }
    }
}
