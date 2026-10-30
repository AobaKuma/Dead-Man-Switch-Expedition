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
    /// 3. 偵測「保留觀察狀態 + 所有敵方世界選靶發射器（<see cref="CompMissileLauncher_WorldTargeting"/>：
    ///    Rail / Ballistic / Scorer）皆已摧毀」→ 哨站摧毀。
    /// </summary>
    public class MapComponent_MissileBase : MapComponent
    {
        private bool baseSiteDestroyed = false;
        private Lord crewLord;

        // ── 火力通道 ──────────────────────────────────────────────────────────
        // 每個元素為一枚飛行中的導彈世界物件引用；Destroyed 或 null → 通道已釋放。
        private List<WorldObject_IncomingMissile> engagedChannels
            = new List<WorldObject_IncomingMissile>();

        // ── 建築快取 ──────────────────────────────────────────────────────────
        // 「曾見過雷達」標記：只有在至少一次 cache 時看到 launcher，
        // 才啟動「全滅即摧毀」邏輯，避免 FFF 佔位符（0 建築）在地圖進入後立刻觸發摧毀。
        private bool hasEverHadLaunchers = false;

        // 快取：每次 rare-tick 更新一次
        private List<Building> cachedEnemyLaunchers;
        private int lastLauncherCacheTick = -1;

        // 輪流發射用的建築索引（不存檔）
        private int nextPadIndex;

        // ── 定時補給空投（CompProperties_MissileBase.supplyDrop）──────────────
        // -1 = 尚未排程；地圖進入後的第一次檢查只排程不投放。
        private int nextSupplyDropTick = -1;

        // ── 彈架補彈（CompProperties_MissileBase.rackSupply）────────────────
        private int nextRackSupplyTick = -1;
        private List<PendingRackLoad> pendingRackLoads = new List<PendingRackLoad>();

        // ── 此地圖是否為導彈陣地地圖 ──────────────────────────────────────────
        // null = 尚未確認；true/false = 已快取結果
        private bool? isMissileBaseMapCached = null;

        private bool IsMissileBaseMap
        {
            get
            {
                if (isMissileBaseMapCached == null)
                    isMissileBaseMapCached = map.Parent is Site s
                        && s.GetComponent<WorldObjectComp_MissileBase>() != null;
                return isMissileBaseMapCached.Value;
            }
        }

        public MapComponent_MissileBase(Map map) : base(map) { }

        // ──────────────── 存讀 ────────────────

        public override void ExposeData()
        {
            if (!IsMissileBaseMap) return;
            base.ExposeData();
            Scribe_Values.Look(ref baseSiteDestroyed, "baseSiteDestroyed", false);
            Scribe_Values.Look(ref hasEverHadLaunchers, "hasEverHadLaunchers", false);
            Scribe_Values.Look(ref nextSupplyDropTick, "nextSupplyDropTick", -1);
            Scribe_Values.Look(ref nextRackSupplyTick, "nextRackSupplyTick", -1);
            // 已被搶走／摧毀的導彈不存引用，避免存檔時引用已銷毀的物件。
            if (Scribe.mode == LoadSaveMode.Saving)
                pendingRackLoads.RemoveAll(e => e.missile == null || e.missile.Destroyed || e.rack == null || e.rack.Destroyed);
            Scribe_Collections.Look(ref pendingRackLoads, "pendingRackLoads", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pendingRackLoads ??= new List<PendingRackLoad>();
                pendingRackLoads.RemoveAll(e => e.missile == null || e.rack == null);
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

            EnsureCrewLord();
            CheckRadarDestruction();
            TryFireFromOnMap();
            TickSupplyDrop();
            TickRackSupply();
            TickCrewReload();
        }

        // ──────────────── 守軍搬彈裝填 ────────────────

        /// <summary>
        /// 讓防守中的守軍把彈藥庫／儲存架裡的導彈搬到未滿的發射架（沿用殖民者的 <c>DMSE_LoadMissileLauncher</c> 工作）。
        /// 同時最多 <see cref="MissileBaseRackSupply.maxCrewLoaders"/> 人；守軍轉入進攻狀態時不搬。
        /// </summary>
        private void TickCrewReload()
        {
            MissileBaseRackSupply supply = WorldComp?.Props.rackSupply;
            Faction enemy = map.ParentFaction;
            if (supply == null || supply.maxCrewLoaders <= 0 || enemy == null) return;

            JobDef loadJob = DMSE_MissileJobDefOf.DMSE_LoadMissileLauncher;
            List<Pawn> crew = map.mapPawns.SpawnedPawnsInFaction(enemy)
                .Where(p => p.RaceProps.Humanlike && p.GetLord()?.LordJob is LordJob_MissileCrewDuty).ToList();
            int busy = crew.Count(p => p.CurJobDef == loadJob);
            if (busy >= supply.maxCrewLoaders) return;

            List<IMissileRack> racks = MissileBaseRackSupplyUtility.RacksOf(map, enemy);
            List<IMissileRack> sources = racks.Where(r => !MissileBaseRackSupplyUtility.IsLauncher(r) && r.StoredCount > 0).ToList();
            if (sources.Count == 0) return;

            foreach (IMissileRack launcher in racks.Where(r => MissileBaseRackSupplyUtility.IsLauncher(r) && !r.Full))
            {
                if (busy >= supply.maxCrewLoaders) return;
                foreach (Pawn p in crew)
                {
                    if (!CanHaulNow(p, loadJob)) continue;
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
        private static bool CanHaulNow(Pawn p, JobDef loadJob)
        {
            if (!p.Spawned || p.Downed || p.InMentalState || p.carryTracker.CarriedThing != null) return false;
            if (p.CurJobDef == loadJob) return false;
            if (!(p.GetLord()?.CurLordToil is LordToil_MissileCrewDefend)) return false;
            if (p.mindState.enemyTarget != null) return false;
            return true;
        }

        // ──────────────── 彈架補彈 ────────────────

        /// <summary>
        /// 每 <see cref="MissileBaseRackSupply.intervalDays"/> 天，為每座空彈架空投一艙導彈（彈種與數量依彈架類型）。
        /// 導彈開艙後等 <see cref="MissileBaseRackSupply.loadDelayTicks"/>，仍在彈架附近且守軍尚在時才裝入。
        /// </summary>
        private void TickRackSupply()
        {
            MissileBaseRackSupply supply = WorldComp?.Props.rackSupply;
            if (supply == null) return;

            ProcessPendingRackLoads(supply);

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

        private void ProcessPendingRackLoads(MissileBaseRackSupply supply)
        {
            if (pendingRackLoads.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            bool crew = map.ParentFaction != null && ActiveCrewCount(map.ParentFaction) > 0;

            for (int i = pendingRackLoads.Count - 1; i >= 0; i--)
            {
                PendingRackLoad e = pendingRackLoads[i];
                if (e.missile == null || e.missile.Destroyed || e.rack == null || e.rack.Destroyed || !e.rack.Spawned)
                {
                    pendingRackLoads.RemoveAt(i);
                    continue;
                }
                if (!e.missile.Spawned)
                {
                    // 還在補給艙裡就繼續等；被人拿走（揹包、運送中）則放棄。
                    if (!(e.missile.ParentHolder is ActiveTransporterInfo)) pendingRackLoads.RemoveAt(i);
                    continue;
                }
                if (now < e.loadTick || !crew) continue;

                IMissileRack rack = MissileRackUtil.Of(e.rack);
                if (rack == null || e.missile.Map != map || e.missile.Position.DistanceTo(e.rack.Position) > supply.loadRadius
                    || rack.Full || !rack.Accepts(e.missile))
                {
                    pendingRackLoads.RemoveAt(i);
                    continue;
                }

                e.missile.DeSpawn();
                if (!rack.GetDirectlyHeldThings().TryAdd(e.missile))
                    GenPlace.TryPlaceThing(e.missile, e.rack.Position, map, ThingPlaceMode.Near);
                pendingRackLoads.RemoveAt(i);
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

        private void CheckRadarDestruction()
        {
            if (!IsPlayerObserving()) return;

            List<Building> launchers = GetEnemyLaunchersCached();
            if (launchers.Count > 0)
            {
                hasEverHadLaunchers = true;
                return;
            }

            // 從未見過雷達（FFF 佔位符生成 0 建築）→ 跳過，避免誤判摧毀
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
                mp.Destroy();
            }
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
                ThingOwner container = rack.GetDirectlyHeldThings();
                MissileConfig cfg = ExtractConfigFromMissile(missile, wc);

                if (cfg == null) break; // 無有效彈體定義，跳出

                List<MapParent> targets = FindPlayerTargetsInRange(source, range, cfg);
                if (targets.NullOrEmpty()) break; // 這枚導彈沒有可打的目標，保留在架上

                // 彈架本身就是發射平台（如發射軌）時從原處升空，否則輪流交給其他發射建築
                CompMissileLauncher_WorldTargeting pad = rack.RackThing.TryGetComp<CompMissileLauncher_WorldTargeting>()
                    ?? pads[nextPadIndex++ % pads.Count];
                ThingDef launchDef = pad.LaunchDefFor(cfg);
                if (launchDef == null) break;

                container.Remove(missile);
                missile.Destroy();

                // 選擇目標（隨機；未來可擴充為優先級排序）
                MapParent target = targets.RandomElement();

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

        /// <summary>
        /// 找到第一個屬於敵方、未摧毀且存有可發射導彈（帶 <see cref="CompMissileConfig"/>）的
        /// <see cref="Building_MissileRack"/>，並以 <paramref name="missile"/> 回傳該枚導彈。<br/>
        /// 只存放不帶此 comp 之彈（如攔截彈）的彈架會被跳過，不消耗任何東西。
        /// </summary>
        private IMissileRack FindRackWithMissiles(out Thing missile)
        {
            missile = null;
            Faction enemy = map.ParentFaction;
            if (enemy == null) return null;

            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed) continue;
                IMissileRack rack = MissileRackUtil.Of(b);
                if (rack == null || rack.StoredCount <= 0) continue;

                missile = rack.GetDirectlyHeldThings()
                    .FirstOrDefault(t => t.def.HasComp(typeof(CompMissileConfig)));
                if (missile != null) return rack;
            }
            return null;
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

            // 退回 WorldComp 預設（選取 CompProperties_MissileBase.missileBody）
            if (wc != null) return wc.BuildFireConfig();

            // 最終退回：取第一個 MissileBodyDef
            MissileBodyDef body = DefDatabase<MissileBodyDef>.AllDefsListForReading.FirstOrDefault();
            return body != null ? new MissileConfig(body) : null;
        }

        /// <summary>
        /// 找出這枚導彈打得到、具有地圖且屬於玩家陣營的 <see cref="MapParent"/>：
        /// 射程不超過陣地射程與導彈射程；跨圖層需導彈具備導引能力。
        /// </summary>
        private List<MapParent> FindPlayerTargetsInRange(MapParent source, int range, MissileConfig cfg)
        {
            List<MapParent> result = new List<MapParent>();
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (!(wo is MapParent mp)) continue;
                if (!mp.HasMap) continue;
                if (mp.Faction == null || !mp.Faction.IsPlayer) continue;
                if (!MissileWorldUtility.CanEngage(cfg, source.Tile, mp.Tile, range, out _)) continue;
                result.Add(mp);
            }
            return result;
        }

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
            int now = Find.TickManager.TicksGame;
            if (cachedEnemyLaunchers != null && now - lastLauncherCacheTick < 500)
                return cachedEnemyLaunchers;

            Faction enemy = map.ParentFaction;
            cachedEnemyLaunchers = new List<Building>();

            if (enemy != null)
            {
                foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
                {
                    if (b.Faction != enemy) continue;
                    if (b.Destroyed) continue;
                    // 以世界選靶基類辨識：1.7 的 DMSE_Building_Scorer 掛的是 _Rail 而非 _Scorer。
                    if (b.TryGetComp<CompMissileLauncher_WorldTargeting>() == null) continue;
                    cachedEnemyLaunchers.Add(b);
                }
            }

            lastLauncherCacheTick = now;
            return cachedEnemyLaunchers;
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
            CleanEngagedChannels();        // 釋放殘留的射擊通道引用
        }

        // ──────────────── WorldComp 快捷存取 ────────────────

        private WorldObjectComp_MissileBase WorldComp
            => (map.Parent as Site)?.GetComponent<WorldObjectComp_MissileBase>();
    }
}
