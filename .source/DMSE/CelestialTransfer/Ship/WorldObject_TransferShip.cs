using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public enum TransferPhase
    {
        DepartureBurn,
        Coast,
        ArrivalBurn,
        Arrived
    }

    /// <summary>
    /// 分離出的船體地圖的父物件（設計稿 §6-2）。繼承 SpaceMapParent：本身就有地圖、可當玩家據點、
    /// 可收穿梭機選單。飛行狀態完整存在這裡：離軌點火 → 滑行 → 入軌點火 → 抵達。
    /// 只有點火階段有推力效果（<see cref="MapComponent_TransferBurn"/>）；滑行期殖民地完全照常運作。
    /// 撞擊飛行（設計稿 §9，<see cref="isImpact"/>）：離軌點火後不減速，飛到撞擊點上方時撞向地表並結束遊戲。
    /// </summary>
    [StaticConstructorOnStartup]
    public class WorldObject_TransferShip : SpaceMapParent
    {
        private static readonly Texture2D AbandonCommandTex = ContentFinder<Texture2D>.Get("UI/Commands/AbandonHome");

        public TransferPhase phase = TransferPhase.Arrived;

        public PlanetTile origin = PlanetTile.Invalid;

        public PlanetTile destination = PlanetTile.Invalid;

        /// <summary>航線起點：分離時船所在的格子（起點地圖旁的第一個空格）。顯示位置與 Tile 都從這裡開始。</summary>
        public PlanetTile flightStart = PlanetTile.Invalid;

        /// <summary>入軌點火時目的地被占用而改停鄰格：原目的地（航線 = 起點 → 原目的地 → 新目的地）。</summary>
        public PlanetTile reroutedFrom = PlanetTile.Invalid;

        /// <summary>要降落的地點（已有東西的格子）；船停在旁邊的空格，抵達後選位降落（<see cref="TransferLandingUtility"/>）。</summary>
        public PlanetTile landingTarget = PlanetTile.Invalid;

        /// <summary>起飛的控制台（找回所屬網路以重算入軌推力）。</summary>
        public Thing flightConsole;

        public int departTick = -1;

        /// <summary>離軌點火時長 t_a；計畫中的入軌點火 t_d 與它相同。</summary>
        public int burnTicks;

        public int coastTicks;

        /// <summary>入軌點火實際時長（開始時依當下推力重算）。</summary>
        public int arrivalBurnTicks;

        public int arrivalStartTick = -1;

        public float progressAtArrivalStart;

        public float fuelSpent;

        /// <summary>撞擊飛行：<see cref="destination"/> 是地表的撞擊點，船沿太空層航線飛到 <see cref="approachTile"/>。</summary>
        public bool isImpact;

        /// <summary>撞擊飛行時，太空層中最接近撞擊點的格子。</summary>
        public PlanetTile approachTile = PlanetTile.Invalid;

        private bool impactFadeStarted;

        [Unsaved(false)]
        private List<PlanetTile> path;

        /// <summary>航線各格中心沿路徑的累積球面角距（與 <see cref="path"/> 等長，[0] = 0）。</summary>
        [Unsaved(false)]
        private List<float> pathCumulative;

        public bool InFlight => phase == TransferPhase.DepartureBurn || phase == TransferPhase.Coast || phase == TransferPhase.ArrivalBurn;

        public bool IsBurning => phase == TransferPhase.DepartureBurn || phase == TransferPhase.ArrivalBurn;

        // ── 起飛 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 分離完成時呼叫：開始離軌點火。burnTicks/coastTicks 由 <see cref="TransferFlightMath"/> 換算。
        /// arrivalBurn 為入軌點火的預估（不含一次性助推器），入軌點火開始時會依當下推力重算。
        /// </summary>
        public void BeginFlight(PlanetTile from, PlanetTile to, int burn, int arrivalBurn, int coast, float fuel, Thing console)
        {
            origin = from;
            destination = to;
            burnTicks = Mathf.Max(1, burn);
            coastTicks = Mathf.Max(0, coast);
            arrivalBurnTicks = Mathf.Max(1, arrivalBurn);
            fuelSpent = fuel;
            flightConsole = console;
            departTick = Find.TickManager.TicksGame;
            arrivalStartTick = -1;
            progressAtArrivalStart = 0f;
            phase = TransferPhase.DepartureBurn;
            isImpact = false;
            approachTile = PlanetTile.Invalid;
            impactFadeStarted = false;
            flightStart = Tile;
            reroutedFrom = PlanetTile.Invalid;
            path = null;
        }

        /// <summary>撞擊飛行（設計稿 §9）：在 <see cref="BeginFlight"/> 之後呼叫，航線終點改為撞擊點上方的太空格。</summary>
        public void SetImpactCourse(PlanetTile approach)
        {
            isImpact = true;
            approachTile = approach;
            impactFadeStarted = false;
            path = null;
        }

        /// <summary>船在太空層中的航線終點：轉移為目的地，撞擊為撞擊點上方的格子。</summary>
        public PlanetTile FlightTarget => isImpact && approachTile.Valid ? approachTile : destination;

        /// <summary>撞擊時刻：沒有入軌減速，以滑行速度走完剩下的進度（= 離軌點火 + 滑行 + 半個入軌點火時長）。</summary>
        public int ImpactTick => departTick + burnTicks + coastTicks + arrivalBurnTicks / 2;

        /// <summary>從起點走向終點的貪婪鄰格路徑（設計稿 §6-4）；確定性，所以不存檔、讀檔後重建。</summary>
        public static List<PlanetTile> BuildPath(PlanetTile from, PlanetTile to)
        {
            List<PlanetTile> result = new List<PlanetTile> { from };
            if (!from.Valid || !to.Valid || from.Layer != to.Layer)
            {
                return result;
            }
            WorldGrid grid = Find.WorldGrid;
            Vector3 target = grid.GetTileCenter(to).normalized;
            List<PlanetTile> neighbors = new List<PlanetTile>();
            PlanetTile cur = from;
            for (int guard = 0; guard < 2000 && cur != to; guard++)
            {
                grid.GetTileNeighbors(cur, neighbors);
                PlanetTile best = PlanetTile.Invalid;
                float bestDist = GenMath.SphericalDistance(grid.GetTileCenter(cur).normalized, target);
                for (int i = 0; i < neighbors.Count; i++)
                {
                    float d = GenMath.SphericalDistance(grid.GetTileCenter(neighbors[i]).normalized, target);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = neighbors[i];
                    }
                }
                if (!best.Valid)
                {
                    break;
                }
                cur = best;
                result.Add(cur);
            }
            if (result[result.Count - 1] != to)
            {
                result.Add(to);
            }
            return result;
        }

        /// <summary>
        /// 航線（設計稿 §6-4）：起點 → 終點的貪婪鄰格路徑，確定性所以不存檔、讀檔後重建。
        /// 顯示位置沿這條折線前進，Tile 取顯示位置所在的那一格，兩者一致。
        /// </summary>
        private List<PlanetTile> Path
        {
            get
            {
                if (path == null)
                {
                    PlanetTile start = flightStart.Valid ? flightStart : origin;
                    if (reroutedFrom.Valid)
                    {
                        path = BuildPath(start, reroutedFrom);
                        if (path[path.Count - 1] != destination)
                        {
                            path.Add(destination);
                        }
                    }
                    else
                    {
                        path = BuildPath(start, FlightTarget);
                    }
                    WorldGrid grid = Find.WorldGrid;
                    pathCumulative = new List<float>(path.Count) { 0f };
                    for (int i = 1; i < path.Count; i++)
                    {
                        float seg = GenMath.SphericalDistance(grid.GetTileCenter(path[i - 1]).normalized, grid.GetTileCenter(path[i]).normalized);
                        pathCumulative.Add(pathCumulative[i - 1] + seg);
                    }
                }
                return path;
            }
        }

        private float PathLength
        {
            get
            {
                List<PlanetTile> p = Path;
                return pathCumulative[p.Count - 1];
            }
        }

        /// <summary>進度 progress 在航線上的位置：所在線段的起點索引與線段內比例。</summary>
        private void LocateOnPath(float progress, out int segment, out float fraction)
        {
            List<PlanetTile> p = Path;
            float total = pathCumulative[p.Count - 1];
            if (p.Count < 2 || total <= 0f)
            {
                segment = 0;
                fraction = 0f;
                return;
            }
            float s = Mathf.Clamp01(progress) * total;
            segment = p.Count - 2;
            for (int i = 0; i < p.Count - 1; i++)
            {
                if (s <= pathCumulative[i + 1])
                {
                    segment = i;
                    break;
                }
            }
            float len = pathCumulative[segment + 1] - pathCumulative[segment];
            fraction = len > 0f ? Mathf.Clamp01((s - pathCumulative[segment]) / len) : 1f;
        }

        /// <summary>進度對應的世界座標：沿航線折線在相鄰兩格中心之間插值（相鄰格的中心連線只經過這兩格）。</summary>
        private Vector3 PathPosition(float progress)
        {
            List<PlanetTile> p = Path;
            WorldGrid grid = Find.WorldGrid;
            if (p.Count < 2)
            {
                return grid.GetTileCenter(p[0]);
            }
            LocateOnPath(progress, out int segment, out float fraction);
            return Vector3.Slerp(grid.GetTileCenter(p[segment]), grid.GetTileCenter(p[segment + 1]), fraction);
        }

        /// <summary>進度對應的格子：顯示位置所在的那一格。</summary>
        private PlanetTile PathTile(float progress)
        {
            List<PlanetTile> p = Path;
            if (p.Count < 2)
            {
                return p[0];
            }
            LocateOnPath(progress, out int segment, out float fraction);
            return fraction < 0.5f ? p[segment] : p[segment + 1];
        }

        // ── 進度（設計稿 §6-3：只在兩端加減速的梯形速度剖面） ───────────────

        /// <summary>0 = 起點、1 = 終點。</summary>
        public float Progress
        {
            get
            {
                if (!InFlight)
                {
                    return 1f;
                }
                int now = Find.TickManager.TicksGame;
                if (phase == TransferPhase.ArrivalBurn)
                {
                    // 入軌點火：從當下進度以線性減速收斂到 1（t_d 可能已依推力重算，仍保持位置連續）
                    float x = Mathf.Clamp01((float)(now - arrivalStartTick) / Mathf.Max(1, arrivalBurnTicks));
                    return progressAtArrivalStart + (1f - progressAtArrivalStart) * (2f * x - x * x);
                }
                float ta = burnTicks;
                float tc = coastTicks;
                float v = 1f / (ta + tc);
                float t = now - departTick;
                if (t < ta)
                {
                    return 0.5f * (v / ta) * t * t;
                }
                if (isImpact)
                {
                    // 撞擊：不減速，等速衝到終點
                    return Mathf.Min(0.5f * v * ta + v * (t - ta), 1f);
                }
                return Mathf.Min(0.5f * v * ta + v * (t - ta), 0.5f * v * ta + v * tc);
            }
        }

        public int PhaseTicksLeft
        {
            get
            {
                int now = Find.TickManager.TicksGame;
                switch (phase)
                {
                    case TransferPhase.DepartureBurn:
                        return Mathf.Max(0, departTick + burnTicks - now);
                    case TransferPhase.Coast:
                        return Mathf.Max(0, (isImpact ? ImpactTick : departTick + burnTicks + coastTicks) - now);
                    case TransferPhase.ArrivalBurn:
                        return Mathf.Max(0, arrivalStartTick + arrivalBurnTicks - now);
                    default:
                        return 0;
                }
            }
        }

        public int TotalTicksLeft
        {
            get
            {
                int now = Find.TickManager.TicksGame;
                switch (phase)
                {
                    case TransferPhase.DepartureBurn:
                    case TransferPhase.Coast:
                        if (isImpact)
                        {
                            return Mathf.Max(0, ImpactTick - now);
                        }
                        return Mathf.Max(0, departTick + burnTicks + coastTicks + arrivalBurnTicks - now);
                    case TransferPhase.ArrivalBurn:
                        return Mathf.Max(0, arrivalStartTick + arrivalBurnTicks - now);
                    default:
                        return 0;
                }
            }
        }

        private Vector3 InterpolatedCenter => PathPosition(Progress);

        public override Vector3 DrawPos => InFlight ? InterpolatedCenter : base.DrawPos;

        /// <summary>原版 WorldCameraDriver 以此決定地圖背景的行星位置：飛行中背景平滑移動（設計稿 §6-6）。</summary>
        public override Vector3 WorldCameraPosition => InFlight ? InterpolatedCenter : base.WorldCameraPosition;

        public override float ExpandingIconRotation
        {
            get
            {
                if (!InFlight)
                {
                    return base.ExpandingIconRotation;
                }
                return Find.WorldGrid.GetHeadingFromTo(DrawPos, Find.WorldGrid.GetTileCenter(FlightTarget));
            }
        }

        // ── 狀態機 ───────────────────────────────────────────────────────────

        /// <summary>撞擊航程需要逐 tick 判斷，淡出與撞擊時刻才準確。</summary>
        protected override void Tick()
        {
            base.Tick();
            if (isImpact && phase == TransferPhase.Coast && !Destroyed)
            {
                TickImpactCourse();
            }
        }

        protected override void TickInterval(int delta)
        {
            if (Destroyed)
            {
                // 同一 tick 內已在 Tick() 撞擊銷毀；不要再讓 MapParent 檢查地圖去留
                return;
            }
            base.TickInterval(delta);
            if (!InFlight || Destroyed)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            switch (phase)
            {
                case TransferPhase.DepartureBurn:
                    if (now >= departTick + burnTicks)
                    {
                        phase = TransferPhase.Coast;
                        BurnOutBoosters();
                        if (isImpact)
                        {
                            Messages.Message("DMSE.Transfer.Flight.ImpactCourseBegin".Translate(Label, (ImpactTick - now).ToStringTicksToPeriod()), this, MessageTypeDefOf.ThreatBig);
                        }
                        else
                        {
                            Messages.Message("DMSE.Transfer.Flight.CoastBegin".Translate(Label), this, MessageTypeDefOf.NeutralEvent);
                        }
                    }
                    break;
                case TransferPhase.Coast:
                    if (!isImpact && now >= departTick + burnTicks + coastTicks)
                    {
                        BeginArrivalBurn();
                    }
                    break;
                case TransferPhase.ArrivalBurn:
                    if (now >= arrivalStartTick + arrivalBurnTicks)
                    {
                        Arrive();
                    }
                    break;
            }
            if (InFlight)
            {
                StepTileAlongPath();
            }
        }

        /// <summary>離軌點火結束：起飛時點燃的一次性助推器燒毀（留下殘骸）。</summary>
        private void BurnOutBoosters()
        {
            if (!HasMap)
            {
                return;
            }
            List<Building> buildings = Map.listerBuildings.allBuildingsColonist.ListFullCopy();
            int count = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                CompTransferEngine engine = buildings[i].GetComp<CompTransferEngine>();
                if (engine != null && engine.Fired && !buildings[i].Destroyed)
                {
                    buildings[i].Destroy(DestroyMode.KillFinalize);
                    count++;
                }
            }
            if (count == 0)
            {
                return;
            }
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(Map);
            if (comp != null)
            {
                List<PileNetwork> nets = comp.Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    nets[i].InvalidateSurvey();
                }
            }
            Messages.Message("DMSE.Transfer.Flight.BoostersBurnedOut".Translate(Label, count), this, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>
        /// 入軌點火開始時重算可用推力：引擎在滑行中被毀 → 點火拉長；推力歸零 → 以姿態推進器慢速減速並發警告，
        /// 不設計「無法減速而飛過頭」的失敗狀態（設計稿 §6-3）。目的地若已被占用，改停最近的空鄰格。
        /// </summary>
        private void BeginArrivalBurn()
        {
            progressAtArrivalStart = Progress;
            arrivalStartTick = Find.TickManager.TicksGame;
            TransferShipSurvey survey = FlightSurvey();
            TransferFlightTuningDef tuning = TransferFlightMath.Tuning;
            if (survey == null || survey.totalThrust <= 0f)
            {
                arrivalBurnTicks = TransferFlightMath.HoursToTicks(tuning.maxBurnHours * 2f);
                Find.LetterStack.ReceiveLetter("DMSE.Transfer.Flight.NoThrustLabel".Translate(), "DMSE.Transfer.Flight.NoThrustText".Translate(Label, arrivalBurnTicks.ToStringTicksToPeriod()), LetterDefOf.NegativeEvent, this);
            }
            else
            {
                arrivalBurnTicks = TransferFlightMath.HoursToTicks(TransferFlightMath.BurnHours(survey.ArrivalTWR));
            }
            if (OccupiedByOther(destination))
            {
                PlanetTile alt = FindFreeTileNear(destination);
                if (alt.Valid)
                {
                    // 航線延伸到鄰格；換算進度讓目前位置不跳動
                    float traveled = progressAtArrivalStart * PathLength;
                    reroutedFrom = destination;
                    destination = alt;
                    path = null;
                    float total = PathLength;
                    progressAtArrivalStart = total > 0f ? Mathf.Clamp01(traveled / total) : 1f;
                    Messages.Message("DMSE.Transfer.Flight.Rerouted".Translate(Label), this, MessageTypeDefOf.CautionInput);
                }
            }
            phase = TransferPhase.ArrivalBurn;
            Messages.Message("DMSE.Transfer.Flight.ArrivalBurnBegin".Translate(Label), this, MessageTypeDefOf.NeutralEvent);
        }

        private void Arrive()
        {
            phase = TransferPhase.Arrived;
            if (!OccupiedByOther(destination))
            {
                Tile = destination;
            }
            path = null;
            // 轉移後安全期從抵達開始計時；之後降落到其他地圖時只轉移剩餘時間
            TransferGraceUtility.BeginAfterArrival(this);
            MapParent site = landingTarget.Valid ? Find.WorldObjects.MapParentAt(landingTarget) : null;
            if (site != null)
            {
                Find.LetterStack.ReceiveLetter("DMSE.Transfer.Flight.ArrivedLabel".Translate(), "DMSE.Transfer.Land.ArrivedText".Translate(Label, site.Label), LetterDefOf.PositiveEvent, this);
                LongEventHandler.ExecuteWhenFinished(delegate { TransferLandingUtility.BeginLanding(this); });
                return;
            }
            if (landingTarget.Valid)
            {
                // 飛行途中目標地點消失（例如小行星被移除）：改為停在原處
                Messages.Message("DMSE.Transfer.Land.SiteGone".Translate(), this, MessageTypeDefOf.NegativeEvent);
            }
            landingTarget = PlanetTile.Invalid;
            Find.LetterStack.ReceiveLetter("DMSE.Transfer.Flight.ArrivedLabel".Translate(), "DMSE.Transfer.Flight.ArrivedText".Translate(Label), LetterDefOf.PositiveEvent, this);
        }

        /// <summary>撞擊前 impactFadeSeconds（實際時間）畫面淡出為白色，時間到即撞擊（沿用舊 WorldObject_ImpactGravship）。</summary>
        private void TickImpactCourse()
        {
            int ticksLeft = ImpactTick - Find.TickManager.TicksGame;
            if (ticksLeft <= 0)
            {
                Impact();
                return;
            }
            if (!impactFadeStarted)
            {
                float realSeconds = ticksLeft / (GenTicks.TicksPerRealSecond * Mathf.Max(1f, Find.TickManager.TickRateMultiplier));
                if (realSeconds <= TransferFlightMath.Tuning.impactFadeSeconds)
                {
                    ScreenFader.StartFade(Color.white, Mathf.Max(0.1f, realSeconds));
                    impactFadeStarted = true;
                }
            }
        }

        /// <summary>撞擊（設計稿 §9）：撞擊坑、片尾，並銷毀船與船體地圖。</summary>
        private void Impact()
        {
            if (!isImpact || Destroyed)
            {
                return;
            }
            phase = TransferPhase.Arrived;
            int engines = HasMap ? TransferImpactUtility.CountImpactEngines(Map.listerBuildings.allBuildingsColonist) : 0;
            TransferImpactUtility.Resolve(Label, engines, destination, this);
        }

        /// <summary>
        /// 設計稿 §6-4：Tile 設為顯示位置所在的航線格子（穿梭機航程、出發點都以 Tile 計算，必須與畫面一致）。
        /// 該格已有其他地圖時保留目前的格子，飛過後再跟上。
        /// </summary>
        private void StepTileAlongPath()
        {
            PlanetTile next = PathTile(Progress);
            if (!next.Valid || next == Tile || OccupiedByMapOther(next))
            {
                return;
            }
            Tile = next;
        }

        private bool OccupiedByOther(PlanetTile tile)
        {
            List<WorldObject> objects = Find.WorldObjects.ObjectsAt(tile).ToList();
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] != this)
                {
                    return true;
                }
            }
            return false;
        }

        private bool OccupiedByMapOther(PlanetTile tile)
        {
            List<WorldObject> objects = Find.WorldObjects.ObjectsAt(tile).ToList();
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] != this && objects[i] is MapParent mp && mp.HasMap)
                {
                    return true;
                }
            }
            return false;
        }

        private PlanetTile FindFreeTileNear(PlanetTile tile)
        {
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(tile, neighbors);
            PlanetTile best = PlanetTile.Invalid;
            int bestDist = int.MaxValue;
            for (int i = 0; i < neighbors.Count; i++)
            {
                if (OccupiedByOther(neighbors[i]))
                {
                    continue;
                }
                int d = TransferFlightMath.Distance(Tile, neighbors[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = neighbors[i];
                }
            }
            return best;
        }

        /// <summary>起飛網路的勘查：起飛的控制台還在就用它所屬的網路，否則取推力最大的網路。</summary>
        public TransferShipSurvey FlightSurvey()
        {
            if (!HasMap)
            {
                return null;
            }
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(Map);
            if (comp == null)
            {
                return null;
            }
            if (flightConsole != null && flightConsole.Spawned && flightConsole.Map == Map)
            {
                PileNetwork net = comp.NetworkContaining(flightConsole);
                if (net != null)
                {
                    return net.Survey;
                }
            }
            TransferShipSurvey best = null;
            List<PileNetwork> nets = comp.Networks;
            for (int i = 0; i < nets.Count; i++)
            {
                TransferShipSurvey s = nets[i].Survey;
                if (best == null || s.totalThrust > best.totalThrust)
                {
                    best = s;
                }
            }
            return best;
        }

        // ── 地圖去留 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 飛行中不移除地圖；靜止時只要船上還有結構樁就保留（船是玩家資產，不像臨時太空據點那樣在無人時消失）。
        /// </summary>
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            if (InFlight)
            {
                return false;
            }
            MapComponent_PileNetwork comp = HasMap ? MapComponent_PileNetwork.For(Map) : null;
            if (comp != null && comp.Piles.Count > 0)
            {
                return false;
            }
            return base.ShouldRemoveMapNow(out alsoRemoveWorldObject);
        }

        // ── UI ───────────────────────────────────────────────────────────────

        public static string PhaseLabel(TransferPhase phase)
        {
            switch (phase)
            {
                case TransferPhase.DepartureBurn:
                    return "DMSE.Transfer.Phase.DepartureBurn".Translate();
                case TransferPhase.Coast:
                    return "DMSE.Transfer.Phase.Coast".Translate();
                case TransferPhase.ArrivalBurn:
                    return "DMSE.Transfer.Phase.ArrivalBurn".Translate();
                default:
                    return "DMSE.Transfer.Phase.Arrived".Translate();
            }
        }

        /// <summary>目前階段名稱；撞擊飛行的滑行段顯示為「撞擊航程」。</summary>
        public string CurrentPhaseLabel => isImpact && phase == TransferPhase.Coast ? "DMSE.Transfer.Phase.ImpactCourse".Translate().ToString() : PhaseLabel(phase);

        public string FlightStatusText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("DMSE.Transfer.Flight.Status".Translate(CurrentPhaseLabel, PhaseTicksLeft.ToStringTicksToPeriod()));
            sb.AppendLine();
            string key = isImpact ? "DMSE.Transfer.Flight.ImpactProgress" : "DMSE.Transfer.Flight.Progress";
            sb.Append(key.Translate(Progress.ToStringPercent(), destination.ToString(), TotalTicksLeft.ToStringTicksToPeriod()));
            return sb.ToString();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (!HasMap || Faction != Faction.OfPlayer)
            {
                yield break;
            }
            if (InFlight)
            {
                if (DebugSettings.ShowDevGizmos)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Next flight phase",
                        action = DebugNextPhase
                    };
                    yield return new Command_Action
                    {
                        defaultLabel = isImpact ? "DEV: Impact now" : "DEV: Arrive now",
                        action = isImpact ? (System.Action)Impact : Arrive
                    };
                }
                yield break;
            }
            MapParent landingSite = landingTarget.Valid ? Find.WorldObjects.MapParentAt(landingTarget) : null;
            if (landingSite != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DMSE.Transfer.Land.Gizmo".Translate(landingSite.Label),
                    defaultDesc = "DMSE.Transfer.Land.GizmoDesc".Translate(landingSite.Label),
                    icon = PileResources.LaunchTransferIcon,
                    action = delegate { TransferLandingUtility.BeginLanding(this); }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DMSE.Transfer.Land.Auto".Translate(),
                    defaultDesc = "DMSE.Transfer.Land.AutoDesc".Translate(),
                    icon = PileResources.LaunchTransferIcon,
                    action = delegate { TransferLandingUtility.LandAutomatically(this); }
                };
                yield return new Command_Action
                {
                    defaultLabel = "DMSE.Transfer.Land.Cancel".Translate(),
                    defaultDesc = "DMSE.Transfer.Land.CancelDesc".Translate(),
                    icon = PileResources.AbortTransferIcon,
                    action = delegate { landingTarget = PlanetTile.Invalid; }
                };
            }
            Command_Action abandon = new Command_Action
            {
                defaultLabel = "DMSE.Transfer.Ship.Abandon".Translate(),
                defaultDesc = "DMSE.Transfer.Ship.AbandonDesc".Translate(),
                icon = AbandonCommandTex,
                action = delegate
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DMSE.Transfer.Ship.AbandonConfirm".Translate(Label), delegate
                    {
                        GravshipUtility.AbandonMap(Map);
                    }, destructive: true));
                }
            };
            if (Map.mapPawns.FreeColonistsSpawnedCount > 0)
            {
                abandon.Disable("DMSE.Transfer.Ship.AbandonHasColonists".Translate());
            }
            yield return abandon;
        }

        private void DebugNextPhase()
        {
            int now = Find.TickManager.TicksGame;
            switch (phase)
            {
                case TransferPhase.DepartureBurn:
                    departTick = now - burnTicks;
                    break;
                case TransferPhase.Coast:
                    departTick = now - burnTicks - coastTicks - (isImpact ? arrivalBurnTicks / 2 : 0);
                    break;
                case TransferPhase.ArrivalBurn:
                    arrivalStartTick = now - arrivalBurnTicks;
                    break;
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (InFlight)
            {
                if (!text.NullOrEmpty())
                {
                    text += "\n";
                }
                text += FlightStatusText();
            }
            if (HasMap)
            {
                MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(Map);
                if (comp != null)
                {
                    if (!text.NullOrEmpty())
                    {
                        text += "\n";
                    }
                    text += "DMSE.Transfer.Ship.Inspect".Translate(comp.Piles.Count, comp.Networks.Count);
                }
            }
            return text;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref phase, "transferPhase", TransferPhase.Arrived);
            Scribe_Values.Look(ref origin, "transferOrigin", PlanetTile.Invalid);
            Scribe_Values.Look(ref destination, "transferDestination", PlanetTile.Invalid);
            Scribe_Values.Look(ref flightStart, "flightStart", PlanetTile.Invalid);
            Scribe_Values.Look(ref reroutedFrom, "reroutedFrom", PlanetTile.Invalid);
            Scribe_Values.Look(ref landingTarget, "landingTarget", PlanetTile.Invalid);
            Scribe_References.Look(ref flightConsole, "flightConsole");
            Scribe_Values.Look(ref departTick, "departTick", -1);
            Scribe_Values.Look(ref burnTicks, "burnTicks");
            Scribe_Values.Look(ref coastTicks, "coastTicks");
            Scribe_Values.Look(ref arrivalBurnTicks, "arrivalBurnTicks");
            Scribe_Values.Look(ref arrivalStartTick, "arrivalStartTick", -1);
            Scribe_Values.Look(ref progressAtArrivalStart, "progressAtArrivalStart");
            Scribe_Values.Look(ref fuelSpent, "fuelSpent");
            Scribe_Values.Look(ref isImpact, "isImpact");
            Scribe_Values.Look(ref approachTile, "approachTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref impactFadeStarted, "impactFadeStarted");
        }

        /// <summary>預設船名：「轉移艦 N」，N 為目前存在的轉移艦數 + 1。</summary>
        public static string NextDefaultName()
        {
            int count = Find.WorldObjects.AllWorldObjects.Count(w => w is WorldObject_TransferShip);
            return "DMSE.Transfer.Ship.DefaultName".Translate(count + 1);
        }
    }
}
