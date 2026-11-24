using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    // 末端落點標記（docs/末端落點顯示與導引頭重新鎖定_實作計畫.md §3）：
    // 被 BVR 收起的導彈本體看不見，在末端窗口內於地面落點畫鎖定圖示（Effect_LockOn）與標籤（敵方紅、我方青、盤旋琥珀）。
    // 純資料視圖：只讀 MapComponent_BVRCombat.Waves，不影響任何判定，也不存檔。

    public enum ImpactMarkerSide { Friendly, Hostile, Neutral }

    /// <summary>一個落點群組：同一陣營、落點相近的導彈合併成一個標記。</summary>
    public struct ImpactMarker
    {
        public IntVec3 cell;
        public ImpactMarkerSide side;
        /// <summary>這個群組有幾枚導彈。</summary>
        public int count;
        /// <summary>群組內最早的剩餘 ticks。</summary>
        public int ticksToImpact;
        /// <summary>群組內全部都在盤旋。</summary>
        public bool loitering;
    }

    /// <summary>標記與末端重鎖共用的時序（純函式，不依賴 Map）。</summary>
    public static class TerminalTiming
    {
        /// <summary>有效末端窗口：有能接戰的 CIWS 用它的窗口，否則用 fallback。</summary>
        public static int EffectiveWindow(int ciwsWindow, int fallback) => ciwsWindow > 0 ? ciwsWindow : Mathf.Max(0, fallback);

        /// <summary>
        /// 標記是否顯示：盤旋中一律顯示；其餘需尚未交接（timeLeft &gt; 0），且已在末端窗口內。
        /// terminalOnly 波次（世界層抵達的導彈）整段生命都是末端窗口：CIWS 中途停火或被摧毀讓窗口變短時仍顯示。
        /// </summary>
        public static bool MarkerVisible(bool loitering, bool terminalOnly, int timeLeft, int effectiveWindow)
            => loitering || (timeLeft > 0 && (terminalOnly || timeLeft <= effectiveWindow));

        /// <summary>重鎖觸發點：剩 min(retargetTicks, 窗口) ticks；任一為 0 表示關閉（回傳 0）。</summary>
        public static int TriggerTicks(int effectiveWindow, int retargetTicks)
            => effectiveWindow <= 0 || retargetTicks <= 0 ? 0 : Mathf.Min(retargetTicks, effectiveWindow);

        public static bool RetargetDue(int timeLeft, int effectiveWindow, int retargetTicks)
        {
            int trigger = TriggerTicks(effectiveWindow, retargetTicks);
            return trigger > 0 && timeLeft > 0 && timeLeft <= trigger;
        }

        /// <summary>新落點是否在位移上限內；maxShiftCells &lt;= 0 表示不限。</summary>
        public static bool WithinShift(IntVec3 from, IntVec3 to, int maxShiftCells)
            => maxShiftCells <= 0 || from.DistanceTo(to) <= maxShiftCells;
    }

    /// <summary>標記資料：陣營分類與聚合。</summary>
    public static class TerminalImpactMarkers
    {
        /// <summary>圖示上限（超過只畫最近的）。</summary>
        public const int MaxRings = 32;

        /// <summary>標籤上限（其餘只畫圖示）。</summary>
        public const int MaxLabels = 16;

        /// <summary>
        /// 以玩家為基準分類：玩家發射 → 我方；對玩家敵對的攻擊方 → 敵方；攻擊方不明但打的是玩家（防禦方或地圖是玩家的）→ 敵方；
        /// 其餘（中立、盟友、與玩家無關的 NPC 互打）→ 中立。
        /// </summary>
        public static ImpactMarkerSide SideOf(Faction attacker, Faction defender, Map map)
        {
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null) { return ImpactMarkerSide.Neutral; }
            if (attacker != null)
            {
                if (attacker == player) { return ImpactMarkerSide.Friendly; }
                return attacker.HostileTo(player) ? ImpactMarkerSide.Hostile : ImpactMarkerSide.Neutral;
            }
            if (defender == player || (map != null && (map.IsPlayerHome || map.ParentFaction == player)))
            {
                return ImpactMarkerSide.Hostile;
            }
            return ImpactMarkerSide.Neutral;
        }

        /// <summary>設定模式下是否顯示這一方：0 關閉、1 只顯示敵方、2 敵我雙方；中立一律不畫。</summary>
        public static bool ShowSide(ImpactMarkerSide side, int mode)
            => side == ImpactMarkerSide.Hostile ? mode >= 1 : side == ImpactMarkerSide.Friendly && mode >= 2;

        private struct Group
        {
            public IntVec3 anchor;
            public long sumX, sumZ;
            public ImpactMarkerSide side;
            public int count;
            public int eta;
            public bool loitering;
        }

        private static readonly List<Group> groups = new List<Group>();

        /// <summary>
        /// 收集地圖上應顯示的落點群組（已排序：ETA 升冪、枚數降冪；最多 <see cref="MaxRings"/> 個）。
        /// 只看導彈（空投艙不畫），落點須在地圖內。
        /// </summary>
        public static void Collect(Map map, int mode, List<ImpactMarker> into)
        {
            into.Clear();
            if (mode <= 0 || map == null) { return; }
            MapComponent_BVRCombat bvr = map.GetComponent<MapComponent_BVRCombat>();
            if (bvr == null || bvr.Waves.Count == 0) { return; }

            int now = Find.TickManager.TicksGame;
            float merge = BVRWorldTuningDef.Current.terminalMarkerMergeCells;
            groups.Clear();
            foreach (BVRWave wave in bvr.Waves)
            {
                int timeLeft = wave.tickToImpact - now;
                bool loitering = wave.loitering;
                bool terminalOnly = wave.TerminalOnly;
                // 窗口只在需要時才算（要掃 CIWS 清單）。
                int window = loitering || terminalOnly ? 0 : bvr.EffectiveTerminalWindow(wave.defenderFaction);
                if (!TerminalTiming.MarkerVisible(loitering, terminalOnly, timeLeft, window)) { continue; }

                foreach (BVRTarget target in wave.targets)
                {
                    IIncomingMissile missile = target.Missile;
                    if (missile == null) { continue; }
                    if (!target.position.IsValid || !target.position.InBounds(map)) { continue; }
                    // 玩家防空已識破的誘餌不是威脅：不畫落點。
                    if (target.decoyIdentified && wave.defenderFaction != null && wave.defenderFaction.IsPlayer) { continue; }
                    ImpactMarkerSide side = SideOf(missile.Attacker, wave.defenderFaction, map);
                    if (!ShowSide(side, mode)) { continue; }
                    Add(target.position, side, Mathf.Max(0, timeLeft), loitering, merge);
                }
            }

            for (int i = 0; i < groups.Count; i++)
            {
                Group g = groups[i];
                IntVec3 center = new IntVec3(Mathf.RoundToInt((float)g.sumX / g.count), 0, Mathf.RoundToInt((float)g.sumZ / g.count));
                into.Add(new ImpactMarker
                {
                    cell = center.InBounds(map) ? center : g.anchor,
                    side = g.side,
                    count = g.count,
                    ticksToImpact = g.eta,
                    loitering = g.loitering,
                });
            }
            groups.Clear();
            into.Sort((a, b) => a.ticksToImpact != b.ticksToImpact ? a.ticksToImpact.CompareTo(b.ticksToImpact) : b.count.CompareTo(a.count));
            if (into.Count > MaxRings) { into.RemoveRange(MaxRings, into.Count - MaxRings); }
        }

        /// <summary>同一陣營、離群組第一枚落點不超過 <paramref name="merge"/> 格就併入；否則開新群組。</summary>
        private static void Add(IntVec3 cell, ImpactMarkerSide side, int eta, bool loitering, float merge)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                Group g = groups[i];
                if (g.side != side || g.anchor.DistanceTo(cell) > merge) { continue; }
                g.count++;
                g.sumX += cell.x;
                g.sumZ += cell.z;
                g.eta = Mathf.Min(g.eta, eta);
                g.loitering &= loitering;
                groups[i] = g;
                return;
            }
            groups.Add(new Group { anchor = cell, sumX = cell.x, sumZ = cell.z, side = side, count = 1, eta = eta, loitering = loitering });
        }
    }

    /// <summary>
    /// 在目前地圖畫末端落點標記。沒有任何存檔狀態：讀舊存檔時由 <c>Map.FillComponents</c> 自動補上。
    /// 繪製放在 <see cref="MapComponentUpdate"/>（與本模組其他地圖特效一致），標籤在 <see cref="MapComponentOnGUI"/>。
    /// </summary>
    public class MapComponent_TerminalImpactMarkers : MapComponent
    {
        public MapComponent_TerminalImpactMarkers(Map map) : base(map) { }

        private static readonly Color HostileColor = new Color(1f, 0.28f, 0.22f, 0.9f);
        private static readonly Color FriendlyColor = new Color(0.35f, 0.85f, 1f, 0.9f);
        private static readonly Color LoiterColor = new Color(1f, 0.8f, 0.25f, 0.85f);

        /// <summary>落點圖示：白色警示三角形，依陣營著色。</summary>
        private const string IconTexPath = "Visual/Effect_LockOn";

        // 3176：在迷霧（3175）之上、地圖邊緣遮罩（4000）之下，霧格裡的落點也看得到
        // （BVR 波次只存在於已偵測的目標，不算洩漏未偵測的導彈）。
        private const int RenderQueue = 3176;

        private static Material hostileMat, friendlyMat, loiterMat;

        private readonly List<ImpactMarker> markers = new List<ImpactMarker>();
        private readonly List<Rect> drawnLabels = new List<Rect>();
        private int cachedFrame = -1;

        private static int Mode => DMSEMod.Settings?.terminalImpactMarkerMode ?? 2;

        private bool ShouldDraw => Mode > 0 && Find.CurrentMap == map && !WorldRendererUtility.WorldSelected;

        /// <summary>每幀只收集一次（Update 與 OnGUI 共用）。</summary>
        private List<ImpactMarker> Markers
        {
            get
            {
                if (cachedFrame != Time.frameCount)
                {
                    cachedFrame = Time.frameCount;
                    TerminalImpactMarkers.Collect(map, Mode, markers);
                }
                return markers;
            }
        }

        /// <summary>圖示邊長（格）：單枚 2.4 格，每多一枚 +0.24，上限 4.8。</summary>
        private static float IconSize(int count) => Mathf.Min(4.8f, 2.4f + 0.24f * (count - 1));

        /// <summary>盤旋中的群組用琥珀色圖示；其餘依陣營。標籤一律用陣營色。</summary>
        private static Material MatFor(ImpactMarker m)
        {
            if (m.loitering)
            {
                return loiterMat ??= MaterialPool.MatFrom(IconTexPath, ShaderDatabase.Transparent, LoiterColor, RenderQueue);
            }
            if (m.side == ImpactMarkerSide.Friendly)
            {
                return friendlyMat ??= MaterialPool.MatFrom(IconTexPath, ShaderDatabase.Transparent, FriendlyColor, RenderQueue);
            }
            return hostileMat ??= MaterialPool.MatFrom(IconTexPath, ShaderDatabase.Transparent, HostileColor, RenderQueue);
        }

        private static Color ColorFor(ImpactMarkerSide side) => side == ImpactMarkerSide.Friendly ? FriendlyColor : HostileColor;

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (!ShouldDraw) { return; }
            List<ImpactMarker> list = Markers;
            if (list.Count == 0) { return; }

            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(4);
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            for (int i = 0; i < list.Count; i++)
            {
                ImpactMarker m = list[i];
                if (!view.Contains(m.cell)) { continue; }
                float size = IconSize(m.count);
                Matrix4x4 matrix = Matrix4x4.TRS(m.cell.ToVector3Shifted().WithY(y), Quaternion.identity, new Vector3(size, 1f, size));
                Graphics.DrawMesh(MeshPool.plane10, matrix, MatFor(m), 0);
            }
        }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            if (Event.current.type != EventType.Repaint || !ShouldDraw) { return; }
            List<ImpactMarker> list = Markers;
            if (list.Count == 0) { return; }

            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(4);
            drawnLabels.Clear();
            int labels = 0;
            // 已依 ETA 排序：最先落地的先畫；和已畫標籤重疊的就跳過（警報清單仍有完整資訊）。
            for (int i = 0; i < list.Count && labels < TerminalImpactMarkers.MaxLabels; i++)
            {
                ImpactMarker m = list[i];
                if (!view.Contains(m.cell)) { continue; }
                string text = LabelFor(m);
                Vector3 shifted = m.cell.ToVector3Shifted();
                Vector2 screen = new Vector3(shifted.x, 0f, shifted.z - IconSize(m.count) * 0.5f - 0.15f).MapToUIPosition();
                Text.Font = GameFont.Tiny;
                float width = Text.CalcSize(text).x + 12f;
                Text.Font = GameFont.Small;
                Rect rect = new Rect(screen.x - width / 2f, screen.y, width, 14f);
                if (Overlaps(rect)) { continue; }
                drawnLabels.Add(rect);
                GenMapUI.DrawThingLabel(screen, text, ColorFor(m.side));
                labels++;
            }
        }

        private bool Overlaps(Rect rect)
        {
            for (int i = 0; i < drawnLabels.Count; i++)
            {
                if (drawnLabels[i].Overlaps(rect)) { return true; }
            }
            return false;
        }

        private static string LabelFor(ImpactMarker m)
        {
            string eta = m.ticksToImpact.ToStringTicksToPeriod(allowSeconds: true);
            string key = m.loitering ? "DMSE.BVR.Marker.Loitering"
                : m.side == ImpactMarkerSide.Friendly ? "DMSE.BVR.Marker.Friendly" : "DMSE.BVR.Marker.Hostile";
            return key.Translate(m.count, eta);
        }
    }
}
