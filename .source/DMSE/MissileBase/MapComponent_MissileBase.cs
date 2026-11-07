using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DMSE
{
    /// <summary>
    /// 導彈陣地的地圖元件。當站點地圖處於生成狀態時接管世界層元件的職責：<br/>
    /// 1. 以地圖內雷達的<b>火力通道</b>為驅動，向射程內玩家殖民地發射導彈。<br/>
    ///    ‧ 通道總數 = 地圖上所有活躍敵方 <see cref="CompFireControlRadar.Props.maxTargets"/> 之和。<br/>
    ///    ‧ 每個通道同時最多追蹤一枚飛行中的 <see cref="WorldObject_IncomingMissile"/>；<br/>
    ///      導彈落地或被攔截（<see cref="WorldObject.Destroyed"/>）後通道自動釋放。<br/>
    ///    ‧ 彈藥來源：地圖上敵方 <see cref="Building_MissileRack"/>；耗盡後停止射擊直到補給。<br/>
    ///    ‧ 導彈從地圖上的敵方世界選靶發射建築升空，飛出地圖邊緣後才進入世界航跡（同玩家發射）。<br/>
    /// 2. 指派 <see cref="LordJob_MissileCrewDuty"/> 給地圖內的敵方 Pawn。<br/>
    /// 3. 偵測「所有敵方雷達（<see cref="IsObjectiveRadar"/>）與世界選靶發射器
    ///    （<see cref="CompMissileLauncher_WorldTargeting"/>：Rail / Ballistic / Scorer）皆已摧毀」→ 哨站摧毀；
    ///    玩家單位仍在圖上時，等撤離後才移除站點。雷達尚存時撤退，站點保留並封鎖再進入（<see cref="SitePartWorker_MissileBase"/>）。
    /// </summary>
    public class MapComponent_MissileBase : MapComponent
    {
        private bool baseSiteDestroyed = false;
        // 曾有玩家單位進入此地圖（地面突擊）；只有地面突擊後撤退才啟動再進入封鎖，遠程打擊的觀察地圖不算。
        private bool playerVisited = false;
        private Lord crewLord;

        // ── 火力通道 ──────────────────────────────────────────────────────────
        // 每個元素為一枚飛行中的導彈世界物件引用；Destroyed 或 null → 通道已釋放。
        private List<WorldObject_IncomingMissile> engagedChannels
            = new List<WorldObject_IncomingMissile>();

        // ── 建築快取 ──────────────────────────────────────────────────────────
        // 「曾見過陣地目標」標記：只有在至少一次 cache 時看到雷達或發射器，
        // 才啟動「全滅即摧毀」邏輯，避免 FFF 佔位符（0 建築）在地圖進入後立刻觸發摧毀。
        private bool hasEverHadLaunchers = false;

        // 快取：每次 rare-tick 更新一次
        private List<Building> cachedEnemyLaunchers;
        private List<Building> cachedEnemyRadars;
        private int lastLauncherCacheTick = -1;

        // 輪流發射用的建築索引（不存檔）
        private int nextPadIndex;

        // ── 定時補給空投（CompProperties_MissileBase.supplyDrop）──────────────
        // -1 = 尚未排程；地圖進入後的第一次檢查只排程不投放。
        private int nextSupplyDropTick = -1;

        // ── 彈架補彈（CompProperties_MissileBase.rackSupply）────────────────
        private int nextRackSupplyTick = -1;
        // 空投已落地、等守軍親自搬上彈架的導彈（不再自動裝填）。
        private List<PendingRackLoad> pendingRackLoads = new List<PendingRackLoad>();

        // ── 砲塔彈藥補給（CompProperties_MissileBase.turretAmmo）─────────────
        private int nextTurretAmmoTick = -1;
        // 空投已落地、等守軍搬去砲塔裝填的彈藥。
        private List<PendingTurretRefuel> pendingTurretRefuels = new List<PendingTurretRefuel>();

        // ── 此地圖是否為導彈陣地地圖 ──────────────────────────────────────────
        // null = 尚未確認；true/false = 已快取結果
        private bool? isMissileBaseMapCached = null;

        private bool IsMissileBaseMap
        {
            get
            {
                if (isMissileBaseMapCached == null)
                {
                    // 讀檔時 MapInfo.parent 要到解析引用階段才有值：這時不能快取，否則陣地讀檔後永遠被當成一般地圖。
                    MapParent parent = map.Parent;
                    if (parent == null) return false;
                    isMissileBaseMapCached = parent is Site s
                        && s.GetComponent<WorldObjectComp_MissileBase>() != null;
                }
                return isMissileBaseMapCached.Value;
            }
        }

        public MapComponent_MissileBase(Map map) : base(map) { }

        // ──────────────── 陣地目標 API ────────────────

        /// <summary>陣地已判定摧毀（雷達與世界選靶發射器全滅）；地圖移除時站點一併刪除。</summary>
        public bool BaseSiteDestroyed => baseSiteDestroyed;

        /// <summary>曾有玩家單位進入此地圖（地面突擊）。</summary>
        public bool PlayerVisited => playerVisited;

        /// <summary>此圖是導彈陣地、尚未摧毀且已見過陣地目標：顯示剩餘目標的警示。</summary>
        public bool ShowsObjectives => IsMissileBaseMap && !baseSiteDestroyed && hasEverHadLaunchers;

        /// <summary>尚存的敵方雷達（<see cref="IsObjectiveRadar"/>）。</summary>
        public IEnumerable<Building> RemainingRadars => GetEnemyRadarsCached().Where(StillEnemy);

        /// <summary>尚存的敵方世界選靶發射器。</summary>
        public IEnumerable<Building> RemainingLaunchers => GetEnemyLaunchersCached().Where(StillEnemy);

        /// <summary>陣地目標雷達：搜索或火控雷達建築；自帶雷達的防空砲塔（如野戰防空系統）不算。</summary>
        public static bool IsObjectiveRadar(Building b)
            => !(b is Building_Turret)
            && (b.TryGetComp<CompSearchRadar>() != null || b.TryGetComp<CompFireControlRadar>() != null);

        // 快取最多晚 500 tick：期間被摧毀、拆除或被玩家佔領的建築不算尚存。
        private bool StillEnemy(Building b) => b.Spawned && !b.Destroyed && b.Faction == map.ParentFaction;

        // ──────────────── 存讀 ────────────────

        public override void ExposeData()
        {
            // 讀檔的 LoadingVars 階段還不知道 map.Parent，一律照讀；一般地圖的存檔沒有這些節點，讀到的都是預設值。
            if (Scribe.mode == LoadSaveMode.Saving && !IsMissileBaseMap) return;
            base.ExposeData();
            Scribe_Values.Look(ref baseSiteDestroyed, "baseSiteDestroyed", false);
            Scribe_Values.Look(ref playerVisited, "playerVisited", false);
            Scribe_Values.Look(ref hasEverHadLaunchers, "hasEverHadLaunchers", false);
            Scribe_Values.Look(ref nextSupplyDropTick, "nextSupplyDropTick", -1);
            Scribe_Values.Look(ref nextRackSupplyTick, "nextRackSupplyTick", -1);
            Scribe_Values.Look(ref nextTurretAmmoTick, "nextTurretAmmoTick", -1);
            // 已被搶走／摧毀的導彈不存引用，避免存檔時引用已銷毀的物件。
            if (Scribe.mode == LoadSaveMode.Saving)
                pendingRackLoads.RemoveAll(e => e.missile == null || e.missile.Destroyed || e.rack == null || e.rack.Destroyed);
            Scribe_Collections.Look(ref pendingRackLoads, "pendingRackLoads", LookMode.Deep);
            // 同樣只存還在的彈藥堆；被搬走／打爛的引用不留在存檔裡。
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                pendingTurretRefuels.RemoveAll(e => e.turret == null || e.turret.Destroyed);
                foreach (PendingTurretRefuel e in pendingTurretRefuels)
                    e.fuel?.RemoveAll(t => t == null || t.Destroyed);
                pendingTurretRefuels.RemoveAll(e => e.fuel.NullOrEmpty());
            }
            Scribe_Collections.Look(ref pendingTurretRefuels, "pendingTurretRefuels", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pendingRackLoads ??= new List<PendingRackLoad>();
                pendingRackLoads.RemoveAll(e => e.missile == null || e.rack == null);
                pendingTurretRefuels ??= new List<PendingTurretRefuel>();
                pendingTurretRefuels.RemoveAll(e => e.turret == null || e.fuel.NullOrEmpty());
            }
            Scribe_References.Look(ref crewLord, "crewLord");
            // WorldObject 引用：依賴世界物件存檔中對應的引用解析。
            // 若導彈在儲存前已落地（Destroyed），載入後解析為 null，清理邏輯自動跳過。
            Scribe_Collections.Look(ref engagedChannels, "engagedChannels", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && engagedChannels == null)
                engagedChannels = new List<WorldObject_IncomingMissile>();
        }

        // ──────────────── Tick 主邏輯（500 tick 週期）────────────────

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (!IsMissileBaseMap) return;
            if (baseSiteDestroyed) return;
            if (Find.TickManager.TicksGame % 500 != 0) return;

            if (!playerVisited && HasPlayerPawns(map)) playerVisited = true;
            EnsureCrewLord();
            TickGeneratorFuel();
            TickTurretAmmoSupply();
            CheckRadarDestruction();
            TryFireFromOnMap();
            TickSupplyDrop();
            TickRackSupply();
            TickCrewHaul();
            TickCrewReload();
        }

        // ──────────────── 發電機燃料 ────────────────

        /// <summary>
        /// <see cref="CompProperties_MissileBase.refuelGenerators"/>：守軍尚在時把燃料發電機維持在半滿以上。
        /// 舊式陣地的火控雷達靠燒木發電機供電；不補的話發電機從空槽開始，雷達永遠開不了機，陣地就打不出導彈。
        /// </summary>
        private void TickGeneratorFuel()
        {
            if (WorldComp?.Props.refuelGenerators != true) return;
            Faction enemy = map.ParentFaction;
            if (enemy == null || ActiveCrewCount(enemy) <= 0) return;

            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed || b.TryGetComp<CompPowerPlant>() == null) continue;
                CompRefuelable fuel = b.TryGetComp<CompRefuelable>();
                if (fuel != null && fuel.Fuel < fuel.Props.fuelCapacity * 0.5f)
                    fuel.Refuel(fuel.Props.fuelCapacity - fuel.Fuel);
            }
        }

        // ──────────────── 砲塔彈藥 ────────────────

        private MissileBaseTurretAmmoSupply TurretAmmoSupply => WorldComp?.Props.turretAmmo;

        /// <summary>
        /// <see cref="MissileBaseTurretAmmoSupply"/>：砲塔彈藥（<see cref="CompRefuelable"/>）低於門檻時，
        /// 在砲塔附近空投實體彈藥。<b>不會</b>直接把燃料塞進砲塔——落地後由守軍搬過去裝填
        /// （見 <see cref="TickCrewHaul"/>）；守軍全滅後彈藥只會躺在地上，砲塔打完就是打完。
        /// </summary>
        private void TickTurretAmmoSupply()
        {
            MissileBaseTurretAmmoSupply supply = TurretAmmoSupply;
            if (supply == null || !supply.enabled) return;

            Faction enemy = map.ParentFaction;
            if (enemy == null) return;

            int now = Find.TickManager.TicksGame;
            if (nextTurretAmmoTick < 0 || now >= nextTurretAmmoTick)
            {
                bool due = nextTurretAmmoTick >= 0;
                nextTurretAmmoTick = now + Mathf.RoundToInt(supply.intervalDays * GenDate.TicksPerDay);
                if (due) ResupplyTurretAmmo(supply, enemy, now);
            }
        }

        private void ResupplyTurretAmmo(MissileBaseTurretAmmoSupply supply, Faction enemy, int now)
        {
            if (ActiveCrewCount(enemy) <= 0) return;

            int turrets = 0, rounds = 0;
            IntVec3 lookAt = IntVec3.Invalid;
            ThingDef lastFuel = null;

            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed || !IsAmmoTurret(b)) continue;
                CompRefuelable ammo = b.TryGetComp<CompRefuelable>();
                if (ammo?.Props.fuelFilter == null || ammo.Props.fuelFilter.AllowedDefCount == 0) continue;
                if (ammo.IsFull || ammo.FuelPercentOfMax >= supply.refillBelowPercent) continue;
                // 上一批補給還在等守軍搬／搬運中：不要重複空投。
                if (pendingTurretRefuels.Any(e => e.turret == b)) continue;

                ThingDef fuelDef = ammo.Props.fuelFilter.AnyAllowedDef;
                int count = FuelItemsToRefill(ammo, supply.refillToPercent);
                if (fuelDef == null || count <= 0) continue;

                List<Thing> dropped = new List<Thing>();
                IntVec3 cell = MissileBaseSupplyUtility.Drop(map, b.Position, fuelDef, count, enemy, dropped);
                if (!cell.IsValid || dropped.Count == 0) continue;

                pendingTurretRefuels.Add(new PendingTurretRefuel
                {
                    turret = b,
                    fuel = dropped,
                    readyTick = now + Mathf.Max(0, supply.haulDelayTicks)
                });

                turrets++;
                rounds += count;
                lastFuel = fuelDef;
                if (!lookAt.IsValid) lookAt = cell;
            }

            if (turrets == 0) return;
            if (IsPlayerObserving())
                Messages.Message("DMSE.MissileBase.TurretAmmoDrop".Translate(lastFuel?.label ?? "?", rounds, turrets),
                    new LookTargets(lookAt, map), MessageTypeDefOf.ThreatSmall);
            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 砲塔彈藥空投：{turrets} 座砲塔，共 {rounds} 個 {lastFuel?.defName}");
        }

        /// <summary>要補幾個彈藥物品（不是燃料單位）：依 <paramref name="fillPercent"/> 補到目標量。</summary>
        private static int FuelItemsToRefill(CompRefuelable ammo, float fillPercent)
        {
            float multiplier = Mathf.Max(0.001f, ammo.Props.FuelMultiplierCurrentDifficulty);
            float target = ammo.Props.fuelCapacity * Mathf.Clamp01(fillPercent);
            return Mathf.Max(1, Mathf.CeilToInt((target - ammo.Fuel) / multiplier));
        }

        /// <summary>會吃 ammo 的砲塔／終端攔截平台（有 <see cref="CompRefuelable"/> 才有彈藥可補）。</summary>
        private static bool IsAmmoTurret(Building b)
            => b is Building_Turret || b.TryGetComp<CompTerminalCIWS>() != null;

        // ──────────────── 守軍搬運 ────────────────

        /// <summary>
        /// 守軍親自搬空投落地的補給：導彈 → 彈架（<c>DMSE_HaulMissileToRack</c>）、
        /// 砲塔彈藥 → 砲塔（原版 <see cref="JobDefOf.Refuel"/>）。<br/>
        /// 搬運是實體流程：彈藥在地上、在 pawn 手上都看得到，玩家可以搶走或打爛；沒有守軍就沒人搬。
        /// </summary>
        private void TickCrewHaul()
        {
            Faction enemy = map.ParentFaction;
            if (enemy == null) return;
            MissileBaseRackSupply rackSupply = WorldComp?.Props.rackSupply;
            MissileBaseTurretAmmoSupply ammoSupply = TurretAmmoSupply;
            if (pendingRackLoads.Count == 0 && pendingTurretRefuels.Count == 0) return;

            PrunePendingRackLoads(rackSupply);
            PrunePendingTurretRefuels();

            if (pendingRackLoads.Count == 0 && pendingTurretRefuels.Count == 0) return;

            List<Pawn> crew = CrewHaulers(enemy);
            if (crew.Count == 0) return;
            int now = Find.TickManager.TicksGame;

            // ── 導彈：地上的補給彈 → 彈架 ──
            if (rackSupply != null && rackSupply.maxCrewLoaders > 0)
            {
                int busy = crew.Count(p => IsLoadJob(p.CurJobDef));
                foreach (PendingRackLoad e in pendingRackLoads)
                {
                    if (busy >= rackSupply.maxCrewLoaders) break;
                    if (now < e.loadTick) continue;   // 剛落地：留給玩家一點搶奪／摧毀的時間

                    IMissileRack rack = MissileRackUtil.Of(e.rack);
                    if (rack == null || rack.Full || !rack.Accepts(e.missile)) continue;
                    if (!e.missile.Spawned) continue;

                    Pawn p = FindHauler(crew, e.missile, e.rack, PathEndMode.ClosestTouch, PathEndMode.Touch);
                    if (p == null) continue;

                    Job job = JobMaker.MakeJob(DMSE_MissileJobDefOf.DMSE_HaulMissileToRack, e.rack, e.missile);
                    job.count = MissileRackUtil.LoadCount(e.missile, rack);
                    if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc)) continue;

                    busy++;
                    if (Prefs.DevMode)
                        Log.Message($"[DMSE MissileBase] {p.LabelShort} 把地上的 {e.missile.def.defName} 搬進 {e.rack.def.defName}");
                }
            }

            // ── 砲塔彈藥：地上的補給彈藥 → 砲塔 ──
            if (ammoSupply != null && ammoSupply.enabled && ammoSupply.maxHaulers > 0)
            {
                int busy = crew.Count(p => IsRefuelJob(p.CurJobDef));
                foreach (PendingTurretRefuel e in pendingTurretRefuels)
                {
                    if (busy >= ammoSupply.maxHaulers) break;
                    if (now < e.readyTick) continue;

                    CompRefuelable ammo = e.turret?.TryGetComp<CompRefuelable>();
                    if (ammo == null || ammo.IsFull) continue;
                    Thing fuel = e.fuel?.FirstOrDefault(t => t != null && !t.Destroyed && t.Spawned && t.Map == map);
                    if (fuel == null) continue;

                    Pawn p = FindHauler(crew, fuel, e.turret, PathEndMode.ClosestTouch, PathEndMode.Touch);
                    if (p == null) continue;

                    // 原版裝填工作：撿起彈藥 → 走到砲塔 → 讀條 → CompRefuelable.Refuel。
                    // playerForced 讓它在砲塔被關閉／指定拆卸時也照常裝填（這是守軍的例行補給）。
                    Job job = JobMaker.MakeJob(JobDefOf.Refuel, e.turret, fuel);
                    job.playerForced = true;
                    if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc)) continue;

                    busy++;
                    if (Prefs.DevMode)
                        Log.Message($"[DMSE MissileBase] {p.LabelShort} 把地上的 {fuel.def.defName} 搬去 {e.turret.def.defName} 裝填");
                }
            }
        }

        /// <summary>防守中的陣地班（<see cref="LordJob_MissileCrewDuty"/>）；炮手要顧崗位，不叫去搬貨。</summary>
        private List<Pawn> CrewHaulers(Faction enemy)
            => map.mapPawns.SpawnedPawnsInFaction(enemy)
                .Where(p => p.RaceProps.Humanlike && p.GetLord()?.LordJob is LordJob_MissileCrewDuty)
                .ToList();

        private static bool IsLoadJob(JobDef def)
            => def == DMSE_MissileJobDefOf.DMSE_LoadMissileLauncher
            || def == DMSE_MissileJobDefOf.DMSE_HaulMissileToRack;

        private static bool IsRefuelJob(JobDef def)
            => def == JobDefOf.Refuel || def == JobDefOf.RefuelAtomic;

        /// <summary>在 <paramref name="crew"/> 裡找一個能現在去搬的人（可預約、可到達兩個目標）。</summary>
        private static Pawn FindHauler(List<Pawn> crew, Thing carryTarget, Thing destination,
            PathEndMode carryPath, PathEndMode destinationPath)
        {
            foreach (Pawn p in crew)
            {
                if (!CanHaulNow(p)) continue;
                if (!p.CanReserveAndReach(carryTarget, carryPath, Danger.Some)) continue;
                if (destination != null && !p.CanReserveAndReach(destination, destinationPath, Danger.Some)) continue;
                return p;
            }
            return null;
        }

        /// <summary>
        /// 讓防守中的守軍把彈藥庫／儲存架裡的導彈搬到未滿的發射架（沿用殖民者的 <c>DMSE_LoadMissileLauncher</c> 工作）。
        /// 同時最多 <see cref="MissileBaseRackSupply.maxCrewLoaders"/> 人（與空投搬運共用額度）；守軍轉入進攻狀態時不搬。
        /// </summary>
        private void TickCrewReload()
        {
            MissileBaseRackSupply supply = WorldComp?.Props.rackSupply;
            Faction enemy = map.ParentFaction;
            if (supply == null || supply.maxCrewLoaders <= 0 || enemy == null) return;

            JobDef loadJob = DMSE_MissileJobDefOf.DMSE_LoadMissileLauncher;
            List<Pawn> crew = CrewHaulers(enemy);
            if (crew.Count == 0) return;
            int busy = crew.Count(p => IsLoadJob(p.CurJobDef));
            if (busy >= supply.maxCrewLoaders) return;

            List<IMissileRack> racks = MissileBaseRackSupplyUtility.RacksOf(map, enemy);
            List<IMissileRack> sources = racks.Where(r => !MissileBaseRackSupplyUtility.IsLauncher(r) && r.StoredCount > 0).ToList();
            if (sources.Count == 0) return;

            foreach (IMissileRack launcher in racks.Where(r => MissileBaseRackSupplyUtility.IsLauncher(r) && !r.Full))
            {
                if (busy >= supply.maxCrewLoaders) return;
                foreach (Pawn p in crew)
                {
                    if (!CanHaulNow(p)) continue;
                    if (!p.CanReserveAndReach(launcher.RackThing, PathEndMode.Touch, Danger.Some)) continue;
                    IMissileRack source = sources
                        .Where(s => MissileBaseRackSupplyUtility.HasMissileFor(s, launcher)
                                 && p.CanReserveAndReach(s.RackThing, PathEndMode.Touch, Danger.Some))
                        .OrderBy(s => s.RackThing.Position.DistanceToSquared(launcher.RackThing.Position))
                        .FirstOrDefault();
                    if (source == null) break;   // 沒有彈藥庫存著這座發射架能用的彈

                    Job job = JobMaker.MakeJob(loadJob, launcher.RackThing, source.RackThing);
                    if (p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                    {
                        busy++;
                        if (Prefs.DevMode)
                            Log.Message($"[DMSE MissileBase] {p.LabelShort} 從 {source.RackThing.def.defName} 搬彈裝填 {launcher.RackThing.def.defName}");
                        break;
                    }
                }
            }
        }

        /// <summary>防守狀態中、沒在戰鬥或搬運的守軍。</summary>
        private static bool CanHaulNow(Pawn p)
        {
            if (!p.Spawned || p.Downed || p.InMentalState || p.carryTracker.CarriedThing != null) return false;
            if (IsLoadJob(p.CurJobDef) || IsRefuelJob(p.CurJobDef)) return false;
            if (!(p.GetLord()?.CurLordToil is LordToil_MissileCrewDefend)) return false;
            if (p.mindState.enemyTarget != null) return false;
            return true;
        }

        // ──────────────── 彈架補彈 ────────────────

        /// <summary>
        /// 每 <see cref="MissileBaseRackSupply.intervalDays"/> 天，為每座空彈架空投一艙導彈（彈種與數量依彈架類型）。
        /// 導彈落地後只在原地等，<see cref="MissileBaseRackSupply.loadDelayTicks"/> 過後由守軍親自搬進彈架
        /// （見 <see cref="TickCrewHaul"/>）；沒有守軍就沒人裝填。
        /// </summary>
        private void TickRackSupply()
        {
            MissileBaseRackSupply supply = WorldComp?.Props.rackSupply;
            if (supply == null) return;

            int now = Find.TickManager.TicksGame;
            if (nextRackSupplyTick < 0 || now >= nextRackSupplyTick)
            {
                bool due = nextRackSupplyTick >= 0;
                nextRackSupplyTick = now + Mathf.RoundToInt(supply.intervalDays * GenDate.TicksPerDay);
                if (due) ResupplyEmptyRacks(supply);
            }
        }

        private int ActiveCrewCount(Faction enemy)
            => map.mapPawns.SpawnedPawnsInFaction(enemy).Count(p => p.RaceProps.Humanlike && !p.Dead && !p.Downed);

        private void ResupplyEmptyRacks(MissileBaseRackSupply supply)
        {
            Faction enemy = map.ParentFaction;
            if (enemy == null || ActiveCrewCount(enemy) <= 0) return;

            int now = Find.TickManager.TicksGame;
            int racks = 0, missiles = 0;
            IntVec3 lookAt = IntVec3.Invalid;
            foreach (IMissileRack rack in MissileBaseRackSupplyUtility.RacksOf(map, enemy))
            {
                if (rack.StoredCount > 0) continue;
                if (supply.maxCrewLoaders > 0 && MissileBaseRackSupplyUtility.IsLauncher(rack)
                    && MissileBaseRackSupplyUtility.RacksOf(map, enemy).Any(s => !MissileBaseRackSupplyUtility.IsLauncher(s)
                        && MissileBaseRackSupplyUtility.HasMissileFor(s, rack)))
                    continue;
                if (pendingRackLoads.Any(e => e.rack == rack.RackThing && e.missile != null && !e.missile.Destroyed)) continue;

                List<Thing> load = MissileBaseRackSupplyUtility.MakeLoad(rack, supply);
                if (load.Count == 0) continue;

                DropPodUtility.DropThingGroupsNear(rack.RackThing.Position, map, new List<List<Thing>> { load },
                    openDelay: 110, instaDrop: false, leaveSlag: false, canRoofPunch: false, forbid: false,
                    allowFogged: true, canTransfer: false, faction: enemy);
                foreach (Thing m in load)
                    pendingRackLoads.Add(new PendingRackLoad { missile = m, rack = rack.RackThing, loadTick = now + supply.loadDelayTicks });

                racks++;
                missiles += load.Count;
                if (!lookAt.IsValid) lookAt = rack.RackThing.Position;
            }

            if (racks == 0) return;
            if (IsPlayerObserving())
                Messages.Message("DMSE.MissileBase.RackResupply".Translate(racks, missiles),
                    new LookTargets(lookAt, map), MessageTypeDefOf.ThreatSmall);
            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 彈架補給空投：{racks} 座彈架，共 {missiles} 發");
        }

        /// <summary>
        /// 清掉已無意義的「等搬導彈」條目：導彈被搬進平台、被玩家拿走、被打爛，或平台已不再收這枚彈。
        /// 清掉後導彈留在原地（如果還在地上），不會憑空消失。
        /// </summary>
        private void PrunePendingRackLoads(MissileBaseRackSupply supply)
        {
            for (int i = pendingRackLoads.Count - 1; i >= 0; i--)
            {
                PendingRackLoad e = pendingRackLoads[i];
                if (e.missile == null || e.missile.Destroyed || e.rack == null || e.rack.Destroyed || !e.rack.Spawned)
                {
                    pendingRackLoads.RemoveAt(i);
                    continue;
                }

                IMissileRack rack = MissileRackUtil.Of(e.rack);
                if (rack == null) { pendingRackLoads.RemoveAt(i); continue; }

                // 已經在平台裡 → 這條補給完成。
                if (e.missile.ParentHolder == rack.GetDirectlyHeldThings()) { pendingRackLoads.RemoveAt(i); continue; }

                if (!e.missile.Spawned)
                {
                    // 還在補給艙裡，或正被本陣營的守軍搬著 → 繼續追蹤；
                    // 被玩家拿走、存進別的容器 → 不關我們的事了。
                    if (e.missile.ParentHolder is ActiveTransporterInfo) continue;
                    if (e.missile.ParentHolder is Pawn_CarryTracker carry && carry.pawn?.Faction == map.ParentFaction) continue;
                    pendingRackLoads.RemoveAt(i);
                    continue;
                }

                if (e.missile.Map != map || rack.Full || !rack.Accepts(e.missile)
                    || e.missile.Position.DistanceTo(e.rack.Position) > (supply?.loadRadius ?? 12f))
                {
                    pendingRackLoads.RemoveAt(i);
                }
            }
        }

        /// <summary>清掉已無意義的「等搬砲塔彈藥」條目：砲塔已滿、被毀，或彈藥堆都不在了。</summary>
        private void PrunePendingTurretRefuels()
        {
            for (int i = pendingTurretRefuels.Count - 1; i >= 0; i--)
            {
                PendingTurretRefuel e = pendingTurretRefuels[i];
                if (e.turret == null || e.turret.Destroyed || !e.turret.Spawned)
                {
                    pendingTurretRefuels.RemoveAt(i);
                    continue;
                }
                e.fuel ??= new List<Thing>();
                e.fuel.RemoveAll(t => t == null || t.Destroyed);
                CompRefuelable ammo = e.turret.TryGetComp<CompRefuelable>();
                if (ammo == null) { pendingTurretRefuels.RemoveAt(i); continue; }
                if (ammo.IsFull || e.fuel.Count == 0) { pendingTurretRefuels.RemoveAt(i); continue; }
                // 全部被搬離這張圖（存進容器、被玩家帶走）→ 不再追蹤。
                if (!e.fuel.Any(t => t.Spawned && t.Map == map)) pendingTurretRefuels.RemoveAt(i);
            }
        }

        // ──────────────── 定時補給空投 ────────────────

        /// <summary>
        /// 依 <see cref="CompProperties_MissileBase.supplyDrop"/> 定時向守軍空投生存食品包。<br/>
        /// 地圖上沒有任何運作中的敵方雷達（搜索或火控；被摧毀、斷電、關閉、故障或 EMP）時不投放；
        /// 雷達恢復運作後下一次排程照常投放。
        /// </summary>
        private void TickSupplyDrop()
        {
            MissileBaseSupplyDrop drop = WorldComp?.Props.supplyDrop;
            if (drop == null) return;

            int now = Find.TickManager.TicksGame;
            if (nextSupplyDropTick < 0 || now >= nextSupplyDropTick)
            {
                bool due = nextSupplyDropTick >= 0;
                nextSupplyDropTick = now + Mathf.RoundToInt(drop.intervalDays.RandomInRange * GenDate.TicksPerDay);
                if (due) TryDropSupplies(drop);
            }
        }

        private void TryDropSupplies(MissileBaseSupplyDrop drop)
        {
            Faction enemy = map.ParentFaction;
            if (enemy == null) return;

            Building radar = FirstActiveRadar(enemy);
            if (radar == null) return;

            int crew = map.mapPawns.SpawnedPawnsInFaction(enemy).Count(p => p.RaceProps.Humanlike && !p.Dead && !p.Downed);
            if (crew <= 0) return;

            int room = crew * drop.maxStockPerPawn - MissileBaseSupplyUtility.CountFood(map, enemy);
            int amount = Mathf.Min(crew * drop.perPawn, room);
            if (amount <= 0) return;

            IntVec3 near = (crewLord?.LordJob as LordJob_MissileCrewDuty)?.stagingPoint ?? radar.Position;
            IntVec3 cell = MissileBaseSupplyUtility.Drop(map, near, drop.ThingDef, amount, enemy);
            if (!cell.IsValid) return;

            if (IsPlayerObserving())
                Messages.Message("DMSE.MissileBase.SupplyDrop".Translate(drop.ThingDef.label, amount),
                    new LookTargets(cell, map), MessageTypeDefOf.NeutralEvent);
            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 定時補給 {drop.ThingDef.defName} ×{amount} 至 {cell}（守軍 {crew}）");
        }

        /// <summary>地圖上第一座屬於 <paramref name="enemy"/> 且運作中的搜索／火控雷達；沒有則為 null。</summary>
        private Building FirstActiveRadar(Faction enemy)
        {
            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed) continue;
                if (b.TryGetComp<CompSearchRadar>()?.Active == true || b.TryGetComp<CompFireControlRadar>()?.Active == true)
                    return b;
            }
            return null;
        }

        // ──────────────── 雷達摧毀偵測 ────────────────

        /// <summary>雷達與世界選靶發射器都是必要目標：兩者皆全滅才判定陣地摧毀。</summary>
        private void CheckRadarDestruction()
        {
            // 不看玩家有沒有在看這張地圖：遠程打擊時地圖常在畫面外，等到地圖被移除才發現就來不及，站點會保留並完整重建。
            if (RemainingLaunchers.Any() || RemainingRadars.Any())
            {
                hasEverHadLaunchers = true;
                return;
            }

            // 從未見過陣地目標（FFF 佔位符生成 0 建築）→ 跳過，避免誤判摧毀
            if (!hasEverHadLaunchers) return;

            OnBaseDestroyed();
        }

        private bool IsPlayerObserving()
        {
            if (map == Find.CurrentMap) return true;
            return map.mapPawns.FreeColonistsSpawnedCount > 0;
        }

        private void OnBaseDestroyed()
        {
            if (baseSiteDestroyed) return;
            baseSiteDestroyed = true;

            MapParent mp = map.Parent;
            if (mp != null && !mp.Destroyed)
            {
                Find.LetterStack.ReceiveLetter(
                    "DMSE.MissileBase.Destroyed".Translate(),
                    "DMSE.MissileBase.DestroyedDesc".Translate(),
                    LetterDefOf.PositiveEvent,
                    new GlobalTargetInfo(mp.Tile));
                // 玩家單位還在圖上時不能直接摧毀站點：原版關圖（MapDeiniter.PassPawnsToWorld）會把殖民者
                // 當成被敵對派系俘虜、其餘玩家單位當成失蹤。改等玩家撤離後由 Site.ShouldRemoveMapNow 連同站點一起移除。
                if (!AnyPlayerUnitAtMap()) mp.Destroy();
            }
        }

        /// <summary>地圖（含子口袋地圖）上是否還有關圖時會失蹤的玩家單位，或正飛往此圖的玩家運輸艙。</summary>
        private bool AnyPlayerUnitAtMap()
            => TransporterUtility.IncomingTransporterPreventingMapRemoval(map) || HasPlayerPawns(map);

        private static bool HasPlayerPawns(Map m)
        {
            foreach (Pawn p in m.mapPawns.AllPawns)
            {
                if (p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer) return true;
            }
            foreach (Map pocket in m.ChildPocketMaps)
            {
                if (HasPlayerPawns(pocket)) return true;
            }
            return false;
        }

        // ──────────────── 地圖內射擊（火力通道驅動）────────────────

        /// <summary>
        /// 核心射擊邏輯：<br/>
        /// 1. 清除已落地或被攔截的通道（<see cref="WorldObject.Destroyed"/> 或 null）。<br/>
        /// 2. 統計地圖上活躍敵方雷達的火力通道總數。<br/>
        /// 3. 對每個空閒通道：從 <see cref="Building_MissileRack"/> 取一枚導彈，選定玩家地圖，發射並占用通道。
        /// </summary>
        private void TryFireFromOnMap()
        {
            // 1. 釋放已結束的通道
            CleanEngagedChannels();

            // 波次（P4）：上一波的導彈全部結束（抵達或被攔截）才發射下一整波
            if ((WorldComp?.Props.fireInWaves ?? true) && engagedChannels.Count > 0) return;

            // 2. 計算可用通道
            int totalChannels = CountFireChannels();
            if (totalChannels <= 0) return;

            int freeChannels = totalChannels - engagedChannels.Count;
            if (freeChannels <= 0) return;

            // 3. 準備射擊所需資訊
            MapParent source = map.Parent;
            if (source == null) return;

            WorldObjectComp_MissileBase wc = WorldComp;
            int range = wc?.Props.range ?? 20;

            // 導彈必須從地圖上的發射建築升空、飛出地圖邊緣後才進入世界航跡；發射建築全滅則停火
            List<CompMissileLauncher_WorldTargeting> pads = GetEnemyLaunchersCached()
                .Where(b => b.Spawned && !b.Destroyed)
                .Where(b => !NeedsOperator(b))
                .Select(b => b.TryGetComp<CompMissileLauncher_WorldTargeting>())
                .Where(c => c != null)
                .ToList();
            if (pads.Count == 0) return;

            // 4. 為每個空閒通道射出一枚導彈（輪流使用各發射建築）
            for (int i = 0; i < freeChannels; i++)
            {
                IMissileRack rack = FindRackWithMissiles(out Thing missile);
                if (rack == null)
                {
                    // 無可用導彈，停止嘗試
                    if (Prefs.DevMode)
                        Log.Message("[DMSE MissileBase] 所有 MissileRack 均無可發射導彈，等待補給。");
                    break;
                }

                // 先看這枚導彈打得到哪些目標（跨圖層需導引能力），再決定是否消耗
                MissileConfig cfg = ExtractConfigFromMissile(missile, wc);

                if (cfg == null) break; // 無有效彈體定義，跳出

                List<MapParent> targets = FindPlayerTargetsInRange(source, range, cfg);
                if (targets.NullOrEmpty()) break; // 這枚導彈沒有可打的目標，保留在架上

                // 彈架本身就是發射平台（如發射軌）時從原處升空，否則輪流交給其他發射建築
                CompMissileLauncher_WorldTargeting pad = rack.RackThing.TryGetComp<CompMissileLauncher_WorldTargeting>()
                    ?? pads[nextPadIndex++ % pads.Count];
                ThingDef launchDef = pad.LaunchDefFor(cfg);
                if (launchDef == null) break;

                MissileRackUtil.ConsumeOne(missile);

                // 選擇目標（隨機；未來可擴充為優先級排序）
                MapParent target = MissileDoctrine.Choose(source, source.Faction, cfg, targets);

                // 世界航跡物件先建立、不加入世界：升空彈飛出地圖時才加入（同玩家發射）
                WorldObject_IncomingMissile wo = IncomingMissileUtility.Make(
                    source.Tile,
                    target.Tile,
                    cfg,
                    source.Faction,
                    worldObjectDef: wc?.Props.incomingWorldObjectDef,
                    target: target);

                if (wo != null)
                {
                    pad.LaunchDeparture(wo, launchDef, target.Tile);
                    engagedChannels.Add(wo);
                    if (Prefs.DevMode)
                        Log.Message($"[DMSE MissileBase] 通道射擊：{source.Tile} → {target.Tile}"
                            + $"（已用 {engagedChannels.Count}/{totalChannels} 通道）");
                }
            }
        }

        // ──────────────── 火力通道工具 ────────────────

        /// <summary>移除已落地（Destroyed）或引用失效（null）的通道條目。</summary>
        private void CleanEngagedChannels()
        {
            engagedChannels.RemoveAll(wo => wo == null || wo.Destroyed);
        }

        /// <summary>
        /// 統計地圖上所有屬於敵方且活躍中的 <see cref="CompFireControlRadar"/>
        /// 的火力通道數（<see cref="CompProperties_FireControlRadar.maxTargets"/> 之和）。
        /// </summary>
        private int CountFireChannels()
        {
            Faction enemy = map.ParentFaction;
            if (enemy == null) return 0;

            int total = 0;
            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed) continue;
                CompFireControlRadar fcr = b.TryGetComp<CompFireControlRadar>();
                if (fcr != null && fcr.Active)
                    total += fcr.Props.maxTargets;
            }
            return total;
        }

        /// <summary>需人操作的設備（手搖旋轉發射架）目前沒人操作。</summary>
        private static bool NeedsOperator(Building b)
        {
            CompMannable mannable = b.TryGetComp<CompMannable>();
            return mannable != null && !mannable.MannedNow;
        }

        /// <summary>
        /// 找到第一個屬於敵方、未摧毀且存有對地彈（<see cref="MissileBaseRackSupplyUtility.IsStrikeRound"/>）的
        /// <see cref="Building_MissileRack"/>，並以 <paramref name="missile"/> 回傳該枚導彈。<br/>
        /// 純防空平台、只存放攔截彈的彈架、以及沒人操作的手動發射架都會被跳過，不消耗任何東西。<br/>
        /// 全圖優先取純對地彈；沒有時才動用兩用彈，盡量把它們留給防空。
        /// </summary>
        private IMissileRack FindRackWithMissiles(out Thing missile)
        {
            missile = null;
            Faction enemy = map.ParentFaction;
            if (enemy == null) return null;

            Thing dualRound = null;
            IMissileRack dualRack = null;

            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed) continue;
                IMissileRack rack = MissileRackUtil.Of(b);
                if (rack == null || rack.StoredCount <= 0) continue;
                if (MissileBaseRackSupplyUtility.IsAirDefenseOnly(b)) continue;
                if (b.TryGetComp<CompMissileLauncher_WorldTargeting>() != null && NeedsOperator(b)) continue;

                foreach (Thing round in rack.GetDirectlyHeldThings())
                {
                    if (!MissileBaseRackSupplyUtility.IsStrikeRound(round)) continue;
                    if (MissileRoleExtension.RoleOf(round.def) == MissileRole.Strike)
                    {
                        missile = round;
                        return rack;
                    }
                    if (dualRound == null)
                    {
                        dualRound = round;
                        dualRack = rack;
                    }
                }
            }
            missile = dualRound;
            return dualRack;
        }

        /// <summary>
        /// 從導彈物品的 <see cref="CompMissileConfig"/> 提取配置；
        /// 若物品無 comp 或 config 無效，則退回 WorldComp 預設配置。
        /// </summary>
        private static MissileConfig ExtractConfigFromMissile(Thing missile, WorldObjectComp_MissileBase wc)
        {
            CompMissileConfig mcc = missile.TryGetComp<CompMissileConfig>();
            if (mcc != null)
            {
                mcc.EnsureInit();
                if (mcc.config != null && mcc.config.Valid)
                    return mcc.config.Clone();
            }

            // 固定構型導彈（沒有 comp，彈體寫在 MissileItemExtension）
            MissileConfig fixedConfig = MissileItemExtension.ConfigFor(missile);
            if (fixedConfig != null) return fixedConfig;

            // 退回 WorldComp 預設（選取 CompProperties_MissileBase.missileBody）
            if (wc != null) return wc.BuildFireConfig();

            // 最終退回：取第一個 MissileBodyDef
            MissileBodyDef body = DefDatabase<MissileBodyDef>.AllDefsListForReading.FirstOrDefault();
            return body != null ? new MissileConfig(body) : null;
        }

        /// <summary>
        /// 這枚導彈打得到的目標：誰算目標由陣營準則決定（<see cref="MissileDoctrineDef.targetWorker"/>，預設只打玩家地圖）；
        /// 射程不超過陣地射程與導彈射程，跨圖層需導彈具備導引能力。
        /// </summary>
        private List<MapParent> FindPlayerTargetsInRange(MapParent source, int range, MissileConfig cfg)
            => MissileDoctrine.Targets(source, source.Faction, cfg, mp => MissileWorldUtility.CanEngage(cfg, source.Tile, mp.Tile, range, out _));

        // ──────────────── Crew LordJob ────────────────

        private void EnsureCrewLord()
        {
            // Lord 已記錄且有 pawn → 不重新生成
            if (crewLord != null && !crewLord.ownedPawns.NullOrEmpty()) return;

            // GenStep_MissileBaseCrew 可能已在地圖生成時建立 LordJob_MissileCrewDuty。
            if (crewLord == null)
            {
                foreach (Lord l in map.lordManager.lords)
                {
                    if (l.LordJob is LordJob_MissileCrewDuty)
                    {
                        crewLord = l;
                        return;
                    }
                }
            }

            // 沒有現有 LordJob_MissileCrewDuty → 把所有無主的敵方人形 Pawn 統一歸入新建的 Lord
            Faction enemy = map.ParentFaction;
            if (enemy == null) return;

            List<Pawn> unLorded = map.mapPawns.SpawnedPawnsInFaction(enemy)
                .Where(p => p.GetLord() == null && p.RaceProps.Humanlike)
                .ToList();
            if (unLorded.Count == 0) return;

            IntVec3 stagingPoint = map.Center;
            List<Building> launchers = GetEnemyLaunchersCached();
            if (launchers.Count > 0)
                stagingPoint = launchers[0].Position;

            crewLord = LordMaker.MakeNewLord(
                enemy,
                new LordJob_MissileCrewDuty(stagingPoint),
                map,
                unLorded);

            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 已為 {unLorded.Count} 個 pawn 建立 LordJob_MissileCrewDuty");
        }

        // ──────────────── 快取工具 ────────────────

        private List<Building> GetEnemyLaunchersCached()
        {
            RefreshObjectiveCache();
            return cachedEnemyLaunchers;
        }

        private List<Building> GetEnemyRadarsCached()
        {
            RefreshObjectiveCache();
            return cachedEnemyRadars;
        }

        private void RefreshObjectiveCache()
        {
            int now = Find.TickManager.TicksGame;
            if (cachedEnemyLaunchers != null && now - lastLauncherCacheTick < 500) return;

            Faction enemy = map.ParentFaction;
            cachedEnemyLaunchers = new List<Building>();
            cachedEnemyRadars = new List<Building>();

            if (enemy != null)
            {
                foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
                {
                    if (b.Faction != enemy) continue;
                    if (b.Destroyed) continue;
                    // 以世界選靶基類辨識：1.7 的 DMSE_Building_Scorer 掛的是 _Rail 而非 _Scorer。
                    if (b.TryGetComp<CompMissileLauncher_WorldTargeting>() != null) cachedEnemyLaunchers.Add(b);
                    else if (IsObjectiveRadar(b)) cachedEnemyRadars.Add(b);
                }
            }

            lastLauncherCacheTick = now;
        }

        // ──────────────── 提前結束觀察 ────────────────

        /// <summary>
        /// 玩家選擇提前結束觀察時由 <see cref="WorldObjectComp_MissileBase.Notify_ObservationEndedEarly"/> 呼叫。<br/>
        /// 重置「曾見過雷達」旗標與摧毀狀態，使陣地可在之後重新被正常偵測，
        /// 模擬敵人趁觀察中斷時重新整頓（補充人員、重置防線）。
        /// </summary>
        public void ResetForRegroup()
        {
            baseSiteDestroyed = false;
            hasEverHadLaunchers = false;
            cachedEnemyLaunchers = null;  // 清除建築快取，下次重新掃描
            cachedEnemyRadars = null;
            CleanEngagedChannels();        // 釋放殘留的射擊通道引用
        }

        // ──────────────── WorldComp 快捷存取 ────────────────

        private WorldObjectComp_MissileBase WorldComp
            => (map.Parent as Site)?.GetComponent<WorldObjectComp_MissileBase>();
    }

    /// <summary>補給艙送來、等待守軍搬去砲塔裝填的一批彈藥（一個空投可能分成好幾堆）。</summary>
    public class PendingTurretRefuel : IExposable
    {
        public Thing turret;
        public List<Thing> fuel;
        public int readyTick;

        public void ExposeData()
        {
            Scribe_References.Look(ref turret, "turret");
            Scribe_Collections.Look(ref fuel, "fuel", LookMode.Reference);
            Scribe_Values.Look(ref readyTick, "readyTick");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                fuel ??= new List<Thing>();
        }
    }
}
