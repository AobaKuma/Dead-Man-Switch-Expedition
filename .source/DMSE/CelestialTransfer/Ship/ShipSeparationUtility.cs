using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 分離（設计稿 §7）：把結構網路的船體範圍以「相同座標」搬到一張新建的同尺寸虛空地圖，
    /// 新地圖的父物件是 <see cref="WorldObject_TransferShip"/>。整段在一個 LongEvent 內同步完成，
    /// 中途不會存檔，所以快照不需要 IExposable。
    ///
    /// 順序對照原版 GravshipUtility.GenerateGravship（擷取、移除）與
    /// GravshipPlacementUtility.PlaceGravshipInMap（生成），但不旋轉、不位移，並且只帶走船體範圍。
    /// </summary>
    public static class ShipSeparationUtility
    {
        // ── 快照 ─────────────────────────────────────────────────────────────

        private class CellData
        {
            public TerrainDef foundation;

            public TerrainDef top;

            public TerrainDef under;

            public TerrainDef temp;

            public ColorDef color;

            public RoofDef roof;

            public uint gas;

            public bool fogged;

            public bool polluted;
        }

        private class ZoneData
        {
            public Zone source;

            public List<IntVec3> cells = new List<IntVec3>();
        }

        private class AreaData
        {
            public Area source;

            public List<IntVec3> cells = new List<IntVec3>();
        }

        private class PawnData
        {
            public Pawn pawn;

            public IntVec3 pos;

            public Rot4 rot;

            public bool drafted;

            public Area areaRestriction;
        }

        private class StorageGroupData
        {
            public string label;

            public StorageSettings settings;

            public List<IStorageGroupMember> members = new List<IStorageGroupMember>();
        }

        private class RoomData
        {
            public IntVec3 cell;

            public float temperature;

            public float vacuum;
        }

        private class Snapshot
        {
            public readonly Dictionary<IntVec3, CellData> cells = new Dictionary<IntVec3, CellData>();

            public readonly List<ZoneData> zones = new List<ZoneData>();

            public readonly List<AreaData> areas = new List<AreaData>();

            public readonly List<Designation> cellDesignations = new List<Designation>();

            public readonly List<Designation> thingDesignations = new List<Designation>();

            public readonly List<Thing> things = new List<Thing>();

            public readonly Dictionary<Thing, IntVec3> thingPositions = new Dictionary<Thing, IntVec3>();

            public readonly Dictionary<Thing, Rot4> thingRotations = new Dictionary<Thing, Rot4>();

            public readonly List<PawnData> pawns = new List<PawnData>();

            public readonly HashSet<Thing> moving = new HashSet<Thing>();

            public readonly Dictionary<Thing, bool> powerOn = new Dictionary<Thing, bool>();

            public readonly Dictionary<Thing, Thing> connectParents = new Dictionary<Thing, Thing>();

            public readonly List<StorageGroupData> storageGroups = new List<StorageGroupData>();

            public readonly List<RoomData> rooms = new List<RoomData>();

            public readonly List<Thing> notCarried = new List<Thing>();

            /// <summary>所掛的牆被帶走、自己不能上重力船而留在範圍外的牆面附掛物（見 <see cref="DetachLeftBehindAttachments"/>）。</summary>
            public readonly List<Thing> detachedAttachments = new List<Thing>();
        }

        /// <summary>分離 / 降落後信件的內容。</summary>
        internal class Report
        {
            public readonly List<string> destroyed = new List<string>();

            public int brokenLinks;

            public int cancelledOrders;

            public int lostThings;

            public readonly List<Pawn> leftBehind = new List<Pawn>();

            public bool originAbandoned;
        }

        // ── 入口 ─────────────────────────────────────────────────────────────

        /// <summary>倒數結束：正式起飛。分離後船從航線第一格開始離軌點火（<see cref="WorldObject_TransferShip.BeginFlight"/>）。</summary>
        public static void Launch(CompTransferConsole console)
        {
            LongEventHandler.QueueLongEvent(delegate { Separate(console, debugPark: false); }, "DMSE.Transfer.Separating", doAsynchronously: false, HandleException);
        }

        /// <summary>開發者指令：不檢查整備、不耗燃料，把船分離後停在起點旁的空格。</summary>
        public static void DebugSeparateNow(CompTransferConsole console)
        {
            LongEventHandler.QueueLongEvent(delegate { Separate(console, debugPark: true); }, "DMSE.Transfer.Separating", doAsynchronously: false, HandleException);
        }

        private static void HandleException(Exception ex)
        {
            Log.Error("[DMSE] Exception during transfer ship separation: " + ex);
        }

        // ── 主流程 ───────────────────────────────────────────────────────────

        private static void Separate(CompTransferConsole console, bool debugPark)
        {
            Thing consoleThing = console.parent;
            Map origin = consoleThing.MapHeld;
            if (origin == null || !consoleThing.Spawned)
            {
                console.AbortPlan();
                return;
            }
            if (!debugPark && console.State != TransferConsoleState.Countdown)
            {
                // 倒數在 LongEvent 執行前已被中止
                return;
            }
            MapComponent_PileNetwork netComp = MapComponent_PileNetwork.For(origin);
            netComp.MarkDirty();
            PileNetwork net = netComp.NetworkContaining(consoleThing);
            if (net == null)
            {
                Abort(console, "DMSE.Transfer.Issue.ConsoleNotOnShip".Translate());
                return;
            }
            net.RecalculateFootprint();
            net.InvalidateSurvey();

            // ── 步驟 1：最後一次整備檢查、決定目的地與起飛後第一格、扣燃料、換算點火/滑行時間
            PlanetTile targetTile;
            PlanetTile startTile;
            float fuel = 0f;
            int burnTicks = 0;
            int arrivalBurnTicks = 0;
            int coastTicks = 0;
            bool impact = false;
            PlanetTile approachTile = PlanetTile.Invalid;
            TransferShipSurvey survey = net.Survey;
            if (debugPark)
            {
                targetTile = FindParkingTile(origin.Tile);
                startTile = targetTile;
            }
            else
            {
                TransferPlan plan = console.plan;
                if (plan == null)
                {
                    Abort(console, "DMSE.Transfer.Abort.NoPlan".Translate());
                    return;
                }
                List<TransferIssue> issues = TransferReadiness.Check(console, plan, includeWarnings: false);
                TransferIssue error = issues.FirstOrDefault(i => i.IsError);
                if (error != null)
                {
                    Abort(console, error.text);
                    return;
                }
                targetTile = plan.destination;
                impact = plan.isImpact;
                if (impact)
                {
                    // 撞擊（設計稿 §9）：目標是地表格，船在太空層沿航線飛到撞擊點上方
                    approachTile = plan.approachTile;
                }
                else if (!targetTile.Valid || Find.WorldObjects.AnyWorldObjectAt(targetTile))
                {
                    targetTile = TransferPlanningUtility.FindFreeNeighbor(origin.Tile, plan.destination, int.MaxValue);
                }
                fuel = TransferFlightMath.FuelRequired(survey.MassWeight, survey.Eta, plan.distance);
                burnTicks = TransferFlightMath.HoursToTicks(TransferFlightMath.BurnHours(survey.TWR));
                arrivalBurnTicks = TransferFlightMath.HoursToTicks(TransferFlightMath.BurnHours(survey.ArrivalTWR));
                coastTicks = TransferFlightMath.HoursToTicks(TransferFlightMath.CoastHours(plan.distance));
                PlanetTile flightTarget = impact ? approachTile : targetTile;
                startTile = flightTarget.Valid ? FindStartTile(origin.Tile, flightTarget) : PlanetTile.Invalid;
            }
            if (!targetTile.Valid || !startTile.Valid)
            {
                Abort(console, "DMSE.Transfer.Abort.NoFreeTile".Translate());
                return;
            }

            Report report = new Report();
            HashSet<IntVec3> footprint = new HashSet<IntVec3>(net.Footprint);
            // 船名在分離前決定（控制台仍在原地圖、勘查可用），並存回同船所有控制台，下次起飛沿用
            string shipName = console.TakeShipNameForLaunch();

            // ── 步驟 1（續）：處理邊界——摧毀跨界建築、切斷跨界連結與施工單、取消跨界的安裝藍圖
            DestroyTornApart(net.TornApartThings.ToList(), report);
            CutBoundaryLinks(netComp, footprint, report);
            CancelCrossBoundaryInstalls(origin, footprint);
            if (fuel > 0f)
            {
                TransferFuelSources.DrawProportionally(survey.fuelSources, fuel);
            }
            if (!debugPark)
            {
                // 點燃可用的一次性助推器：離軌點火結束後燒毀
                for (int i = 0; i < survey.engines.Count; i++)
                {
                    TransferShipSurvey.EngineEntry e = survey.engines[i];
                    if (e.engine.Props.expendable && e.Operational)
                    {
                        e.engine.MarkFired();
                    }
                }
            }
            CollectLeftBehind(origin, footprint, report);

            // ── 步驟 2、3：世界物件與同尺寸虛空地圖
            // 分離當下船不與原地圖同格：放在航線的第一格（設計稿 §6-4），DrawPos 仍從起點插值，看不出跳格
            WorldObject_TransferShip ship = (WorldObject_TransferShip)WorldObjectMaker.MakeWorldObject(DMSE_DefOf.DMSE_TransferShip);
            ship.Tile = startTile;
            ship.SetFaction(Faction.OfPlayer);
            ship.Name = shipName;
            ship.origin = origin.Tile;
            ship.destination = targetTile;
            ship.phase = TransferPhase.Arrived;
            if (!debugPark && console.plan != null && console.plan.landOnSite)
            {
                // 降落：船飛往目的地旁的空格，抵達後選位降落到目的地的地圖
                ship.landingTarget = console.plan.destination;
            }
            Find.WorldObjects.Add(ship);
            Map shipMap = MapGenerator.GenerateMap(origin.Size, ship, DMSE_DefOf.DMSE_ShipVoid);

            // ── 步驟 4–6：擷取並從原地圖移除
            Snapshot snap = new Snapshot();
            GravshipUtility.generatingGravship = true;
            try
            {
                CaptureCells(origin, footprint, snap);
                CaptureThingsAndPawns(origin, footprint, snap);
                CaptureZonesAreasDesignations(origin, footprint, snap);
                CaptureStorageGroupsPowerRooms(origin, footprint, snap);
                RemoveFromOrigin(origin, snap);
            }
            finally
            {
                GravshipUtility.generatingGravship = false;
            }
            DetachLeftBehindAttachments(origin, snap, report);
            // 先處理原地圖排隊中的電網變動：拆掉被搬走的導線/電池所屬的舊電網，並重設它們的 transNet / connectChildren。
            // 否則搬到船上的發送端仍掛著原地圖的舊電網，重新接線時會重複登記（PowerNet registered connector it already had）。
            // 原版逆重船在起飛與降落之間隔了很多 tick，原地圖早已處理完，所以不會遇到。
            origin.powerNetManager.UpdatePowerNetsAndConnections_First();

            // ── 步驟 7：原地圖清理
            ClearOrigin(origin, footprint, snap, report);

            // ── 步驟 8、9：船體地圖生成與收尾（同座標：位移為零）
            GravshipPlacementUtility.placingGravship = true;
            List<Thing> spawned;
            try
            {
                spawned = PlaceOnShip(shipMap, origin, snap, IntVec3.Zero);
            }
            finally
            {
                GravshipPlacementUtility.placingGravship = false;
            }
            FinalizeShip(shipMap, origin, snap, spawned, IntVec3.Zero, newMap: true);

            // ── 步驟 10：口袋地圖改掛到船上
            RelinkPocketMaps(origin, shipMap, snap);

            // ── 步驟 12：離軌點火開始（開發者分離則直接停泊）；計畫完成；相機切到船上
            if (!debugPark)
            {
                ship.BeginFlight(origin.Tile, targetTile, burnTicks, arrivalBurnTicks, coastTicks, fuel, consoleThing);
                if (impact)
                {
                    ship.SetImpactCourse(approachTile);
                }
            }
            console.AbortPlan();
            Current.Game.CurrentMap = shipMap;
            // 起飛演出只顯示傳入的格子，船體外側的附掛物也要一起顯示
            HashSet<IntVec3> shownCells = new HashSet<IntVec3>(footprint);
            shownCells.UnionWith(OuterAttachmentCells(shipMap, footprint));

            // ── 步驟 11：原地圖去留
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                Action finish = delegate
                {
                    Current.Game.CurrentMap = shipMap;
                    HandleOrigin(origin, report);
                    SendLetter(ship, report);
                    if (consoleThing.Spawned) CameraJumper.TryJump(consoleThing);
                };
                if (!debugPark)
                {
                    SoundDefOf.Gravship_Launch.PlayOneShotOnCamera();
                }
                Find.WindowStack.Add(new TransferTakeoffCutscene(origin,
                    CellRect.FromCellList(footprint).CenterVector3, finish, shipMap, shownCells));
            });
        }

        private static void Abort(CompTransferConsole console, string reason)
        {
            if (console.InLaunchSequence)
            {
                // 倒數結束時才失敗：退回 Planned，計畫保留
                console.AbortLaunch(reason);
                return;
            }
            Messages.Message(reason, console.parent, MessageTypeDefOf.RejectInput, historical: false);
        }

        /// <summary>航線上第一個沒有其他地圖的格子；航線第一格被占用時退回起點旁任一空格。</summary>
        private static PlanetTile FindStartTile(PlanetTile origin, PlanetTile destination)
        {
            List<PlanetTile> path = WorldObject_TransferShip.BuildPath(origin, destination);
            for (int i = 1; i < path.Count; i++)
            {
                if (!Find.WorldObjects.AnyMapParentAt(path[i]))
                {
                    return path[i];
                }
            }
            return FindParkingTile(origin);
        }

        private static PlanetTile FindParkingTile(PlanetTile origin)
        {
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(origin, neighbors);
            for (int i = 0; i < neighbors.Count; i++)
            {
                if (!Find.WorldObjects.AnyWorldObjectAt(neighbors[i]))
                {
                    return neighbors[i];
                }
            }
            return PlanetTile.Invalid;
        }

        // ── 步驟 1：邊界 ─────────────────────────────────────────────────────

        /// <summary>設計稿 §3-5：建築以原版 KillFinalize 摧毀；藍圖與施工框架取消（已送達材料全額退回）。</summary>
        private static void DestroyTornApart(List<Thing> things, Report report)
        {
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.Destroyed || !t.Spawned)
                {
                    continue;
                }
                report.destroyed.Add(t.LabelCap);
                if (t is Blueprint || t is Frame)
                {
                    t.Destroy(DestroyMode.Cancel);
                }
                else
                {
                    t.Destroy(DestroyMode.KillFinalize);
                }
            }
        }

        /// <summary>
        /// 被帶走的樁與留下的樁之間的連結解除（不返還材料）；一端在船內、一端在船外的施工單取消
        /// （已送達的材料丟在施工端樁旁）。
        /// </summary>
        private static void CutBoundaryLinks(MapComponent_PileNetwork netComp, HashSet<IntVec3> footprint, Report report)
        {
            List<CompStructuralPile> piles = netComp.Piles.ToList();
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile p = piles[i];
                bool pMoves = PileNetwork.FullyInside(p.parent, footprint);
                for (int j = p.Links.Count - 1; j >= 0; j--)
                {
                    Thing other = p.Links[j];
                    if (other == null || PileNetwork.FullyInside(other, footprint) == pMoves)
                    {
                        continue;
                    }
                    p.RemoveLinkRaw(other);
                    other.TryGetComp<CompStructuralPile>()?.RemoveLinkRaw(p.parent);
                    report.brokenLinks++;
                }
                for (int j = p.Orders.Count - 1; j >= 0; j--)
                {
                    Thing target = p.Orders[j].target;
                    if (target == null || !target.Spawned || PileNetwork.FullyInside(target, footprint) != pMoves)
                    {
                        p.CancelOrder(p.Orders[j]);
                        report.cancelledOrders++;
                    }
                }
            }
            netComp.MarkDirty();
        }

        /// <summary>安裝藍圖與待安裝物件分在船內外時，取消藍圖（否則安裝工作會跨地圖）。</summary>
        private static void CancelCrossBoundaryInstalls(Map map, HashSet<IntVec3> footprint)
        {
            List<Thing> blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).ToList();
            for (int i = 0; i < blueprints.Count; i++)
            {
                Blueprint_Install install = blueprints[i] as Blueprint_Install;
                Thing toInstall = install?.MiniToInstallOrBuildingToReinstall;
                Thing anchor = toInstall?.SpawnedParentOrMe;
                if (anchor == null || install.Destroyed)
                {
                    continue;
                }
                if (footprint.Contains(install.Position) != footprint.Contains(anchor.Position))
                {
                    install.Destroy(DestroyMode.Cancel);
                }
            }
        }

        private static void CollectLeftBehind(Map map, HashSet<IntVec3> footprint, Report report)
        {
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if ((p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony) && !footprint.Contains(p.Position))
                {
                    report.leftBehind.Add(p);
                }
            }
        }

        // ── 步驟 4：擷取 ─────────────────────────────────────────────────────

        private static void CaptureCells(Map map, HashSet<IntVec3> footprint, Snapshot snap)
        {
            TerrainGrid tg = map.terrainGrid;
            bool biotech = ModsConfig.BiotechActive && map.pollutionGrid != null;
            foreach (IntVec3 c in footprint)
            {
                snap.cells[c] = new CellData
                {
                    foundation = tg.FoundationAt(c),
                    top = tg.TopTerrainAt(c),
                    under = tg.UnderTerrainAt(c),
                    temp = tg.TempTerrainAt(c),
                    color = tg.ColorAt(c),
                    roof = map.roofGrid.RoofAt(c),
                    gas = map.gasGrid.GetDirect(c),
                    fogged = map.fogGrid.IsFogged(c),
                    polluted = biotech && map.pollutionGrid.IsPolluted(c)
                };
            }
        }

        /// <summary>設計稿 §3-5 船載物納入規則。</summary>
        private static bool ShouldMove(Thing t, IntVec3 cell)
        {
            if (t is Pawn || t is Mote || t is Skyfaller || t is PawnFlyer || t is Projectile)
            {
                return false;
            }
            if (t.Position != cell || !t.def.bringAlongOnGravship)
            {
                return false;
            }
            if (t.def.category == ThingCategory.Ethereal && !(t is Blueprint) && !(t is Frame))
            {
                return false;
            }
            if (t.def.building != null && t.def.building.isNaturalRock && !TransferFlightMath.Tuning.carryNaturalRock)
            {
                return false;
            }
            return true;
        }

        private static void CaptureThingsAndPawns(Map map, HashSet<IntVec3> footprint, Snapshot snap)
        {
            // 先處理 pawn：停止工作、放下搬運物（放下的東西若落在範圍內也一起帶走）。
            // 放下的可能是被搬運的倒地 pawn，所以放下後才重新收集名單。
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (!footprint.Contains(p.Position))
                {
                    continue;
                }
                p.jobs?.StopAll();
                p.pather?.StopDead();
                if (p.carryTracker?.CarriedThing != null)
                {
                    Thing dropped;
                    p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out dropped);
                }
            }
            List<Pawn> pawns = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (footprint.Contains(p.Position))
                {
                    pawns.Add(p);
                }
            }
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                snap.pawns.Add(new PawnData
                {
                    pawn = p,
                    pos = p.Position,
                    rot = p.Rotation,
                    drafted = p.Drafted,
                    areaRestriction = p.playerSettings?.AreaRestrictionInPawnCurrentMap
                });
                snap.moving.Add(p);
            }

            foreach (IntVec3 c in footprint)
            {
                List<Thing> list = map.thingGrid.ThingsListAt(c);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Thing t = list[i];
                    if (t is Pawn || snap.moving.Contains(t) || IsWallAttachment(t))
                    {
                        continue;
                    }
                    if (ShouldMove(t, c))
                    {
                        snap.things.Add(t);
                        snap.moving.Add(t);
                        snap.thingPositions[t] = t.Position;
                        snap.thingRotations[t] = t.Rotation;
                    }
                    else if (t.Position == c && t.def.category != ThingCategory.Ethereal && !(t is Mote) && !(t is Skyfaller) && !(t is PawnFlyer) && !(t is Projectile))
                    {
                        // bringAlongOnGravship = false、或不帶走的天然岩：分離後會懸在虛空，直接移除（設計稿 §3-5）
                        snap.notCarried.Add(t);
                    }
                }
            }
            // 牆面附掛物只跟著所掛的牆走（同原版 Gravship.CopyCellContents），不看自己的格子：
            // 牆被帶走時附掛物一起帶走，掛在船體外側、格子不在範圍內的也一樣（船上會懸在虛空格，降落時見 OuterAttachmentCells）；
            // 牆沒被帶走的附掛物整個留在原地。否則會留下沒有牆的附掛物，原版重新接電網時找不到牆而 NullReferenceException。
            // 本身不能上重力船的附掛物：範圍內照一般規則移除，範圍外的留在原地圖，等牆移除後比照原版拆牆處理。
            int carried = snap.things.Count;
            for (int i = 0; i < carried; i++)
            {
                foreach (Thing a in GenConstruct.GetAttachedBuildings(snap.things[i]))
                {
                    if (snap.moving.Contains(a))
                    {
                        continue;
                    }
                    if (ShouldMove(a, a.Position))
                    {
                        snap.things.Add(a);
                        snap.moving.Add(a);
                        snap.thingPositions[a] = a.Position;
                        snap.thingRotations[a] = a.Rotation;
                    }
                    else if (footprint.Contains(a.Position))
                    {
                        snap.notCarried.Add(a);
                    }
                    else
                    {
                        snap.detachedAttachments.Add(a);
                    }
                }
            }
            // 與原版相同的優先順序：擷取時由高到低移除，生成時由低到高（建築先、物品與床後）
            snap.things.SortByDescending(GravshipUtility.ThingSpawnPriority);
        }

        private static bool IsWallAttachment(Thing t)
        {
            return GenConstruct.BuiltDefOf(t.def) is ThingDef def && def.building != null && def.building.isAttachment;
        }

        /// <summary>
        /// 掛在 cells 內的牆上、自己的格子卻在 cells 外的牆面附掛物格子（船體外側的附掛物）。
        /// 這些附掛物隨牆移動，降落時要一併檢查與清空落點，但它們底下沒有船體地形。
        /// </summary>
        internal static List<IntVec3> OuterAttachmentCells(Map map, ICollection<IntVec3> cells)
        {
            HashSet<IntVec3> result = new HashSet<IntVec3>();
            foreach (IntVec3 c in cells)
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    foreach (Thing a in GenConstruct.GetAttachedBuildings(things[i]))
                    {
                        if (!cells.Contains(a.Position))
                        {
                            result.Add(a.Position);
                        }
                    }
                }
            }
            return result.ToList();
        }

        /// <summary>
        /// 牆被帶走、自己不能上重力船而留在原地圖的附掛物：比照原版拆牆（Building.DeSpawn 在 WillReplace 以外的模式會處理附掛物），
        /// 可打包的打包成物品放回原處，否則拆除。須在移除船上物件之後、處理原地圖電網之前呼叫。
        /// </summary>
        private static void DetachLeftBehindAttachments(Map map, Snapshot snap, Report report)
        {
            for (int i = 0; i < snap.detachedAttachments.Count; i++)
            {
                Thing a = snap.detachedAttachments[i];
                if (a.Destroyed || !a.Spawned)
                {
                    continue;
                }
                IntVec3 pos = a.Position;
                if (a.def.Minifiable)
                {
                    GenSpawn.Spawn(a.MakeMinified(), pos, map);
                    continue;
                }
                report.destroyed.Add(a.LabelCap);
                a.Destroy(a is Blueprint || a is Frame ? DestroyMode.Cancel : DestroyMode.Deconstruct);
            }
        }

        private static void CaptureZonesAreasDesignations(Map map, HashSet<IntVec3> footprint, Snapshot snap)
        {
            foreach (Zone zone in map.zoneManager.AllZones.ToList())
            {
                ZoneData data = null;
                foreach (IntVec3 c in zone.Cells)
                {
                    if (footprint.Contains(c))
                    {
                        if (data == null)
                        {
                            data = new ZoneData { source = zone };
                        }
                        data.cells.Add(c);
                    }
                }
                if (data != null)
                {
                    snap.zones.Add(data);
                }
            }

            foreach (Area area in map.areaManager.AllAreas)
            {
                AreaData data = null;
                foreach (IntVec3 c in footprint)
                {
                    if (area[c])
                    {
                        if (data == null)
                        {
                            data = new AreaData { source = area };
                        }
                        data.cells.Add(c);
                    }
                }
                if (data != null)
                {
                    snap.areas.Add(data);
                }
            }

            DesignationManager dm = map.designationManager;
            foreach (Designation des in dm.AllDesignations.ToList())
            {
                if (des.def.targetType == TargetType.Cell)
                {
                    if (footprint.Contains(des.target.Cell))
                    {
                        snap.cellDesignations.Add(des);
                    }
                }
                else if (des.target.HasThing && snap.moving.Contains(des.target.Thing))
                {
                    snap.thingDesignations.Add(des);
                }
            }
        }

        private static void CaptureStorageGroupsPowerRooms(Map map, HashSet<IntVec3> footprint, Snapshot snap)
        {
            foreach (StorageGroup group in map.storageGroups.StorageGroupsForReading.ToList())
            {
                StorageGroupData data = null;
                foreach (IStorageGroupMember member in group.members)
                {
                    Thing t = member as Thing;
                    if (t != null && snap.moving.Contains(t))
                    {
                        if (data == null)
                        {
                            data = new StorageGroupData { label = group.RenamableLabel, settings = new StorageSettings() };
                            data.settings.CopyFrom(group.GetStoreSettings());
                        }
                        data.members.Add(member);
                    }
                }
                if (data != null)
                {
                    snap.storageGroups.Add(data);
                }
            }

            for (int i = 0; i < snap.things.Count; i++)
            {
                Thing t = snap.things[i];
                CompPowerTrader trader = t.TryGetComp<CompPowerTrader>();
                if (trader != null)
                {
                    snap.powerOn[t] = trader.PowerOn;
                }
                CompPower power = t.TryGetComp<CompPower>();
                Thing parent = power?.connectParent?.parent;
                if (parent != null && snap.moving.Contains(parent))
                {
                    snap.connectParents[t] = parent;
                }
            }

            HashSet<Room> seen = new HashSet<Room>();
            foreach (IntVec3 c in footprint)
            {
                Room room = c.GetRoom(map);
                if (room != null && seen.Add(room) && !room.UsesOutdoorTemperature)
                {
                    snap.rooms.Add(new RoomData { cell = c, temperature = room.Temperature, vacuum = room.Vacuum });
                }
            }
        }

        // ── 步驟 5、6：移除 ──────────────────────────────────────────────────

        private static void RemoveFromOrigin(Map map, Snapshot snap)
        {
            // 區域：把格子從原區域移出（原區域格子清空時會自行刪除），船上另建新區域
            for (int i = 0; i < snap.zones.Count; i++)
            {
                ZoneData z = snap.zones[i];
                for (int j = 0; j < z.cells.Count; j++)
                {
                    // 最後一格移出時原區域會自行 Deregister
                    if (z.source.ContainsCell(z.cells[j]))
                    {
                        z.source.RemoveCell(z.cells[j]);
                    }
                }
            }
            for (int i = 0; i < snap.storageGroups.Count; i++)
            {
                for (int j = 0; j < snap.storageGroups[i].members.Count; j++)
                {
                    snap.storageGroups[i].members[j].SetStorageGroup(null);
                }
            }
            DesignationManager dm = map.designationManager;
            for (int i = 0; i < snap.cellDesignations.Count; i++)
            {
                dm.RemoveDesignation(snap.cellDesignations[i]);
            }
            for (int i = 0; i < snap.thingDesignations.Count; i++)
            {
                dm.RemoveDesignation(snap.thingDesignations[i]);
            }

            for (int i = 0; i < snap.things.Count; i++)
            {
                snap.things[i].PreSwapMap();
            }
            for (int i = 0; i < snap.things.Count; i++)
            {
                if (snap.things[i].Spawned)
                {
                    snap.things[i].DeSpawn(DestroyMode.WillReplace);
                }
            }
            for (int i = 0; i < snap.pawns.Count; i++)
            {
                Pawn p = snap.pawns[i].pawn;
                Lord lord = p.GetLord();
                if (lord != null && p.Faction != Faction.OfPlayer)
                {
                    // 非玩家 pawn 的 Lord 屬於原地圖，帶到船上會指揮到錯的地圖
                    lord.Notify_PawnLost(p, PawnLostCondition.ExitedMap);
                }
                p.PreSwapMap();
                if (p.Spawned)
                {
                    p.DeSpawn(DestroyMode.WillReplace);
                }
            }
        }

        // ── 步驟 7：原地圖清理 ───────────────────────────────────────────────

        /// <summary>
        /// 船體範圍各格回到太空虛空。不能用 TerrainGrid.RemoveGravshipTerrainUnsafe：它會還原成 under 層，
        /// 小行星岩地這類沒有 under 層的格子不會變成虛空。
        /// </summary>
        private static void ClearOrigin(Map map, HashSet<IntVec3> footprint, Snapshot snap, Report report)
        {
            for (int i = 0; i < snap.notCarried.Count; i++)
            {
                Thing t = snap.notCarried[i];
                if (!t.Destroyed)
                {
                    t.Destroy(DestroyMode.Vanish);
                    report.lostThings++;
                }
            }

            TerrainGrid tg = map.terrainGrid;
            List<Area> areas = map.areaManager.AllAreas;
            bool biotech = ModsConfig.BiotechActive && map.pollutionGrid != null;
            foreach (IntVec3 c in footprint)
            {
                if (tg.TempTerrainAt(c) != null)
                {
                    tg.RemoveTempTerrain(c, doLeavings: false, preventDestroyEffects: true);
                }
                if (tg.FoundationAt(c) != null)
                {
                    tg.RemoveFoundation(c, doLeavings: false);
                }
                tg.SetTerrain(c, TerrainDefOf.Space);
                map.roofGrid.SetRoof(c, null);
                for (int i = 0; i < areas.Count; i++)
                {
                    if (areas[i][c])
                    {
                        areas[i][c] = false;
                    }
                }
                map.gasGrid.ClearCellUnsafe(c);
                map.snowGrid.SetDepth(c, 0f);
                map.sandGrid?.SetDepth(c, 0f);
                if (biotech && map.pollutionGrid.IsPolluted(c))
                {
                    map.pollutionGrid.SetPolluted(c, false, silent: true);
                }
                foreach (Designation des in map.designationManager.AllDesignationsAt(c).ToList())
                {
                    map.designationManager.RemoveDesignation(des);
                }
                map.mapDrawer.MapMeshDirty(c, (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.Gas, regenAdjacentCells: true, regenAdjacentSections: false);
            }
            // 船體範圍原本可能有迷霧（例如未開採的岩石內部），搬走後是開放的太空，迷霧要清掉；
            // 因此外露、會產生迷霧的相鄰岩壁也一併揭開（比照原版採礦後的 FloodUnfogAdjacent）
            FogGrid fog = map.fogGrid;
            foreach (IntVec3 c in footprint)
            {
                if (fog.IsFogged(c))
                {
                    fog.Unfog(c);
                }
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 n = c + GenAdj.AdjacentCells[i];
                    if (n.InBounds(map) && fog.IsFogged(n) && !footprint.Contains(n))
                    {
                        Building edifice = n.GetEdifice(map);
                        if (edifice != null && edifice.def.MakeFog)
                        {
                            fog.Unfog(n);
                        }
                    }
                }
            }
            RoofCollapseCellsFinder.CheckAndRemoveCollpsingRoofs(map);
            map.roofGrid.Drawer.SetDirty();
        }

        // ── 步驟 8：船體地圖生成 ─────────────────────────────────────────────

        /// <param name="offset">目的地座標 = 原座標 + offset（分離為零；降落到其他地圖時為選定的位移）。</param>
        private static List<Thing> PlaceOnShip(Map ship, Map origin, Snapshot snap, IntVec3 offset)
        {
            ship.regionAndRoomUpdater.Enabled = true;
            TerrainGrid tg = ship.terrainGrid;
            bool biotech = ModsConfig.BiotechActive && ship.pollutionGrid != null;

            // 地形：底層 → 地基 → 頂層（layerable 時會把底層推入 under） → 臨時地形 → 顏色
            foreach (KeyValuePair<IntVec3, CellData> kv in snap.cells)
            {
                IntVec3 c = kv.Key + offset;
                CellData d = kv.Value;
                TerrainDef baseTerrain = d.under ?? d.top;
                if (baseTerrain != null && baseTerrain != TerrainDefOf.Space)
                {
                    tg.SetTerrain(c, baseTerrain);
                }
                if (d.foundation != null)
                {
                    tg.SetFoundation(c, d.foundation);
                }
                if (d.under != null && d.top != null)
                {
                    tg.SetTerrain(c, d.top);
                }
                if (d.temp != null)
                {
                    tg.SetTempTerrain(c, d.temp);
                }
                if (d.color != null)
                {
                    tg.SetTerrainColor(c, d.color);
                }
                if (d.gas != 0)
                {
                    ship.gasGrid.SetDirect(c, d.gas);
                    ship.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Gas);
                }
                if (biotech && d.polluted)
                {
                    ship.pollutionGrid.SetPolluted(c, true, silent: true);
                }
            }

            // 區域（物品生成前建好，物品才會落在儲存區內）
            for (int i = 0; i < snap.zones.Count; i++)
            {
                CreateZone(ship, snap.zones[i], offset);
            }
            for (int i = 0; i < snap.cellDesignations.Count; i++)
            {
                Designation des = snap.cellDesignations[i];
                des.target = new LocalTargetInfo(des.target.Cell + offset);
                if (ship.designationManager.DesignationAt(des.target.Cell, des.def) == null)
                {
                    ship.designationManager.AddDesignation(des);
                }
            }

            // 非 pawn 物件：與原版相同，由低優先順序開始生成（建築先、附掛物、最後物品與床）
            List<Thing> spawned = new List<Thing>();
            for (int i = snap.things.Count - 1; i >= 0; i--)
            {
                Thing t = snap.things[i];
                if (t.Destroyed)
                {
                    continue;
                }
                try
                {
                    GenSpawn.Spawn(t, snap.thingPositions[t] + offset, ship, snap.thingRotations[t]);
                    spawned.Add(t);
                }
                catch (Exception ex)
                {
                    Log.Error("[DMSE] Failed to move " + t + " to the transfer ship: " + ex);
                }
            }

            // pawn：恢復徵召；倒地者送回床上
            for (int i = 0; i < snap.pawns.Count; i++)
            {
                PawnData pd = snap.pawns[i];
                Pawn p = pd.pawn;
                if (p.Destroyed || p.Discarded)
                {
                    continue;
                }
                try
                {
                    GenSpawn.Spawn(p, pd.pos + offset, ship, pd.rot);
                    if (p.drafter != null)
                    {
                        p.drafter.Drafted = pd.drafted;
                    }
                    if (p.Downed || p.Deathresting)
                    {
                        Building_Bed bed = ship.thingGrid.ThingAt<Building_Bed>(p.Position);
                        if (bed != null)
                        {
                            RestUtility.TuckIntoBed(bed, p, p, rescued: false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[DMSE] Failed to move " + p + " to the transfer ship: " + ex);
                }
            }

            // 屋頂最後
            foreach (KeyValuePair<IntVec3, CellData> kv in snap.cells)
            {
                if (kv.Value.roof != null)
                {
                    ship.roofGrid.SetRoof(kv.Key + offset, kv.Value.roof);
                }
            }

            // 一般區域、pawn 區域限制、物件 designation、儲存群組
            Dictionary<Area, Area> areaMap = CopyAreas(ship, origin, snap, offset);
            for (int i = 0; i < snap.pawns.Count; i++)
            {
                PawnData pd = snap.pawns[i];
                Area mapped;
                if (pd.areaRestriction != null && pd.pawn.Spawned && pd.pawn.playerSettings != null && areaMap.TryGetValue(pd.areaRestriction, out mapped))
                {
                    pd.pawn.playerSettings.AreaRestrictionInPawnCurrentMap = mapped;
                }
            }
            for (int i = 0; i < snap.thingDesignations.Count; i++)
            {
                Designation des = snap.thingDesignations[i];
                Thing t = des.target.Thing;
                if (t != null && t.Spawned && t.Map == ship && ship.designationManager.DesignationOn(t, des.def) == null)
                {
                    ship.designationManager.AddDesignation(des);
                }
            }
            for (int i = 0; i < snap.storageGroups.Count; i++)
            {
                StorageGroupData g = snap.storageGroups[i];
                StorageGroup group = ship.storageGroups.NewGroup(g.label);
                group.GetStoreSettings().CopyFrom(g.settings);
                for (int j = 0; j < g.members.Count; j++)
                {
                    g.members[j].SetStorageGroup(group);
                }
            }
            return spawned;
        }

        private static void CreateZone(Map ship, ZoneData data, IntVec3 offset)
        {
            Zone_Stockpile stockpile = data.source as Zone_Stockpile;
            Zone_Growing growing = data.source as Zone_Growing;
            Zone zone;
            if (stockpile != null)
            {
                Zone_Stockpile z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, ship.zoneManager)
                {
                    label = stockpile.label,
                    color = stockpile.color,
                    Hidden = stockpile.Hidden
                };
                z.settings = new StorageSettings(z);
                z.settings.CopyFrom(stockpile.settings);
                zone = z;
            }
            else if (growing != null)
            {
                Zone_Growing z = new Zone_Growing(ship.zoneManager)
                {
                    label = growing.label,
                    color = growing.color
                };
                z.SetPlantDefToGrow(growing.PlantDefToGrow);
                z.allowSow = growing.allowSow;
                z.allowCut = growing.allowCut;
                zone = z;
            }
            else
            {
                // 其他 mod 的區域類型：不搬，避免錯誤建構
                return;
            }
            ship.zoneManager.RegisterZone(zone);
            for (int i = 0; i < data.cells.Count; i++)
            {
                zone.AddCell(data.cells[i] + offset);
            }
        }

        /// <summary>回傳原區域 → 船上區域的對照（給 pawn 區域限制用）。</summary>
        private static Dictionary<Area, Area> CopyAreas(Map ship, Map origin, Snapshot snap, IntVec3 offset)
        {
            Dictionary<Area, Area> result = new Dictionary<Area, Area>();
            AreaManager src = origin.areaManager;
            AreaManager dst = ship.areaManager;
            for (int i = 0; i < snap.areas.Count; i++)
            {
                AreaData data = snap.areas[i];
                Area target = null;
                if (data.source == src.Home)
                {
                    target = dst.Home;
                }
                else if (data.source == src.BuildRoof)
                {
                    target = dst.BuildRoof;
                }
                else if (data.source == src.NoRoof)
                {
                    target = dst.NoRoof;
                }
                else if (data.source == src.SnowOrSandClear)
                {
                    target = dst.SnowOrSandClear;
                }
                else if (ModsConfig.BiotechActive && data.source == src.PollutionClear)
                {
                    target = dst.PollutionClear;
                }
                else if (data.source is Area_Allowed allowed)
                {
                    Area_Allowed created = dst.GetLabeled(allowed.Label) as Area_Allowed;
                    if (created == null && !dst.TryMakeNewAllowed(out created))
                    {
                        continue;
                    }
                    created.SetLabel(allowed.Label);
                    created.SetColor(allowed.Color);
                    created.RenamableLabel = allowed.RenamableLabel;
                    target = created;
                }
                if (target == null)
                {
                    continue;
                }
                for (int j = 0; j < data.cells.Count; j++)
                {
                    target[data.cells[j] + offset] = true;
                }
                result[data.source] = target;
            }
            // 限制在家園區/允許區、但該區沒有任何格子隨船的 pawn：對應到船上的家園區或同名允許區
            if (!result.ContainsKey(src.Home))
            {
                result[src.Home] = dst.Home;
            }
            return result;
        }

        // ── 步驟 9：收尾 ─────────────────────────────────────────────────────

        /// <param name="newMap">新建的船體地圖：複製原地圖的故事狀態與自動屠宰設定（降落到既有地圖時保留對方的設定）。</param>
        private static void FinalizeShip(Map ship, Map origin, Snapshot snap, List<Thing> spawned, IntVec3 offset, bool newMap)
        {
            GravshipUtility.UpdateBillDestinations(ship);
            GravshipUtility.UpdateBillDestinations(origin);

            foreach (KeyValuePair<Thing, Thing> kv in snap.connectParents)
            {
                if (kv.Key.Spawned && kv.Value.Spawned && kv.Key.Map == ship && kv.Value.Map == ship)
                {
                    kv.Key.TryGetComp<CompPower>()?.ConnectToTransmitter(kv.Value.TryGetComp<CompPower>());
                }
            }
            foreach (KeyValuePair<Thing, bool> kv in snap.powerOn)
            {
                CompPowerTrader trader = kv.Key.TryGetComp<CompPowerTrader>();
                if (trader != null && kv.Key.Spawned)
                {
                    trader.PowerOn = kv.Value;
                }
            }

            // 迷霧逐格複製
            foreach (KeyValuePair<IntVec3, CellData> kv in snap.cells)
            {
                if (kv.Value.fogged)
                {
                    ship.fogGrid.Refog(CellRect.SingleCell(kv.Key + offset));
                }
            }

            if (newMap)
            {
                CopyColonySettings(ship, origin);
            }

            ship.listerFilthInHomeArea.RebuildAll();
            ship.resourceCounter.UpdateResourceCounts();
            ship.wealthWatcher.ForceRecount(allowDuringInit: true);
            ship.powerNetManager.UpdatePowerNetsAndConnections_First();
            origin.powerNetManager.UpdatePowerNetsAndConnections_First();
            origin.resourceCounter.UpdateResourceCounts();

            for (int i = 0; i < spawned.Count; i++)
            {
                if (!spawned[i].Destroyed)
                {
                    spawned[i].PostSwapMap();
                }
            }
            for (int i = 0; i < snap.pawns.Count; i++)
            {
                Pawn p = snap.pawns[i].pawn;
                if (!p.Destroyed)
                {
                    p.PostSwapMap();
                }
            }

            // 房間溫度與真空度（原版 ApplyTemperatureVacuumFromBase 需要 Gravship 物件，這裡自寫同等邏輯）
            for (int i = 0; i < snap.rooms.Count; i++)
            {
                RoomData rd = snap.rooms[i];
                Room room = (rd.cell + offset).GetRoom(ship);
                if (room != null && !room.UsesOutdoorTemperature)
                {
                    room.Temperature = rd.temperature;
                    room.Vacuum = room.ExposedToSpace ? 1f : rd.vacuum;
                }
            }
            ship.GetComponent<VacuumComponent>()?.RebuildData();
            origin.GetComponent<VacuumComponent>()?.RebuildData();
            // 船上若載著原版逆重船（GravEngine + 底板），比照原版放置流程重算底板
            ship.substructureGrid?.MarkDirty();
            origin.substructureGrid?.MarkDirty();
        }

        private static void CopyColonySettings(Map ship, Map origin)
        {
            origin.storyState.CopyTo(ship.storyState);
            List<AutoSlaughterConfig> configs = new List<AutoSlaughterConfig>();
            foreach (AutoSlaughterConfig src in origin.autoSlaughterManager.configs)
            {
                configs.Add(new AutoSlaughterConfig
                {
                    animal = src.animal,
                    maxTotal = src.maxTotal,
                    maxMales = src.maxMales,
                    maxMalesYoung = src.maxMalesYoung,
                    maxFemales = src.maxFemales,
                    maxFemalesYoung = src.maxFemalesYoung,
                    allowSlaughterPregnant = src.allowSlaughterPregnant,
                    allowSlaughterBonded = src.allowSlaughterBonded
                });
            }
            ship.autoSlaughterManager.configs = configs;
            ship.autoSlaughterManager.Notify_ConfigChanged();
        }

        // ── 降落：船體地圖 → 目的地地圖（加位移） ─────────────────────────────

        /// <summary>
        /// 降落（<see cref="TransferLandingUtility"/>）：把 from 地圖上的 cells 搬到 to 地圖、座標加 offset。
        /// 與分離共用擷取 / 移除 / 生成流程；降落區先清空（植物、污物、可降落其上的建築移除，物品與 pawn 移到旁邊）。
        /// </summary>
        internal static Report MoveHull(Map from, Map to, HashSet<IntVec3> cells, IntVec3 offset)
        {
            Report report = new Report();
            PrepareLandingCells(to, cells, OuterAttachmentCells(from, cells), offset);
            Snapshot snap = new Snapshot();
            GravshipUtility.generatingGravship = true;
            try
            {
                CaptureCells(from, cells, snap);
                CaptureThingsAndPawns(from, cells, snap);
                CaptureZonesAreasDesignations(from, cells, snap);
                CaptureStorageGroupsPowerRooms(from, cells, snap);
                RemoveFromOrigin(from, snap);
            }
            finally
            {
                GravshipUtility.generatingGravship = false;
            }
            DetachLeftBehindAttachments(from, snap, report);
            from.powerNetManager.UpdatePowerNetsAndConnections_First();
            ClearOrigin(from, cells, snap, report);
            GravshipPlacementUtility.placingGravship = true;
            List<Thing> spawned;
            try
            {
                spawned = PlaceOnShip(to, from, snap, offset);
            }
            finally
            {
                GravshipPlacementUtility.placingGravship = false;
            }
            FinalizeShip(to, from, snap, spawned, offset, newMap: false);
            RelinkPocketMaps(from, to, snap);
            return report;
        }

        /// <param name="attachmentCells">船體外側附掛物的格子（<see cref="OuterAttachmentCells"/>）：只清出物件，不動地形與區域。</param>
        private static void PrepareLandingCells(Map map, HashSet<IntVec3> cells, List<IntVec3> attachmentCells, IntVec3 offset)
        {
            HashSet<IntVec3> target = new HashSet<IntVec3>();
            foreach (IntVec3 c in cells)
            {
                target.Add(c + offset);
            }
            List<IntVec3> attachmentTargets = attachmentCells.Select(c => c + offset).ToList();
            HashSet<IntVec3> occupied = new HashSet<IntVec3>(target);
            occupied.UnionWith(attachmentTargets);
            Predicate<IntVec3> outside = x => !occupied.Contains(x) && x.Standable(map) && x.GetTerrain(map) != TerrainDefOf.Space;
            for (int i = 0; i < attachmentTargets.Count; i++)
            {
                ClearThingsForLanding(map, attachmentTargets[i], outside);
            }
            TerrainGrid tg = map.terrainGrid;
            foreach (IntVec3 d in target)
            {
                map.zoneManager.ZoneAt(d)?.RemoveCell(d);
                foreach (Designation des in map.designationManager.AllDesignationsAt(d).ToList())
                {
                    map.designationManager.RemoveDesignation(des);
                }
                ClearThingsForLanding(map, d, outside);
                if (tg.TempTerrainAt(d) != null)
                {
                    tg.RemoveTempTerrain(d, doLeavings: false, preventDestroyEffects: true);
                }
                if (tg.FoundationAt(d) != null)
                {
                    tg.RemoveFoundation(d, doLeavings: false);
                }
                map.roofGrid.SetRoof(d, null);
            }
        }

        /// <summary>落點上的 pawn 與物品移到旁邊（outside 格），可摧毀的建築、植物等移除。</summary>
        private static void ClearThingsForLanding(Map map, IntVec3 d, Predicate<IntVec3> outside)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(d).ToList();
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.Destroyed || !t.Spawned)
                {
                    continue;
                }
                if (t is Pawn pawn)
                {
                    if (CellFinder.TryFindRandomCellNear(d, map, 12, outside, out IntVec3 cell))
                    {
                        pawn.Position = cell;
                        pawn.Notify_Teleported();
                    }
                }
                else if (t.def.category == ThingCategory.Item)
                {
                    t.DeSpawn();
                    if (!GenPlace.TryPlaceThing(t, d, map, ThingPlaceMode.Near, null, outside))
                    {
                        t.Destroy();
                    }
                }
                else if (t.def.destroyable)
                {
                    t.Destroy();
                }
            }
        }

        // ── 步驟 10：口袋地圖 ────────────────────────────────────────────────

        /// <summary>
        /// 被搬走的地圖入口（例：DMSE 真空密封室）：把口袋地圖的 sourceMap 改指船體地圖，
        /// 否則口袋地圖會在原地圖棄置時被一起銷毀。
        /// </summary>
        private static void RelinkPocketMaps(Map origin, Map ship, Snapshot snap)
        {
            for (int i = 0; i < snap.things.Count; i++)
            {
                MapPortal portal = snap.things[i] as MapPortal;
                PocketMapParent pocket = portal?.PocketMap?.Parent as PocketMapParent;
                if (pocket != null && pocket.sourceMap == origin)
                {
                    pocket.sourceMap = ship;
                }
            }
        }

        // ── 步驟 11：原地圖去留 ──────────────────────────────────────────────

        /// <summary>
        /// 臨時太空據點交給它自己的 ShouldRemoveMapNow；玩家據點若已無殖民者與玩家建築則棄置，否則保留
        /// （玩家得到兩個據點）。
        /// </summary>
        private static void HandleOrigin(Map origin, Report report)
        {
            if (!(origin.Parent is Settlement) || origin.Parent.Faction != Faction.OfPlayer)
            {
                return;
            }
            if (origin.mapPawns.FreeColonistsSpawnedCount > 0 || origin.listerBuildings.allBuildingsColonist.Count > 0)
            {
                return;
            }
            report.originAbandoned = true;
            GravshipUtility.AbandonMap(origin);
        }

        // ── 信件 ─────────────────────────────────────────────────────────────

        private static void SendLetter(WorldObject_TransferShip ship, Report report)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("DMSE.Transfer.Letter.SeparatedText".Translate(ship.Label));
            if (report.destroyed.Count > 0)
            {
                sb.AppendLine().AppendLine();
                sb.Append("DMSE.Transfer.Letter.Destroyed".Translate()).AppendLine();
                sb.Append(report.destroyed.ToLineList("  - "));
            }
            if (report.leftBehind.Count > 0)
            {
                sb.AppendLine().AppendLine();
                sb.Append("DMSE.Transfer.Letter.LeftBehind".Translate()).AppendLine();
                sb.Append(report.leftBehind.Select(p => p.LabelShortCap).ToLineList("  - "));
            }
            if (report.brokenLinks > 0 || report.cancelledOrders > 0)
            {
                sb.AppendLine().AppendLine();
                sb.Append("DMSE.Transfer.Letter.BrokenLinks".Translate(report.brokenLinks, report.cancelledOrders));
            }
            if (report.lostThings > 0)
            {
                sb.AppendLine().AppendLine();
                sb.Append("DMSE.Transfer.Letter.LostThings".Translate(report.lostThings));
            }
            if (report.originAbandoned)
            {
                sb.AppendLine().AppendLine();
                sb.Append("DMSE.Transfer.Letter.OriginAbandoned".Translate());
            }
            Find.LetterStack.ReceiveLetter("DMSE.Transfer.Letter.SeparatedLabel".Translate(), sb.ToString(), LetterDefOf.PositiveEvent, ship);
        }
    }
}
