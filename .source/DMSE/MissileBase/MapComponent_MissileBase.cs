using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
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
    /// 2. 指派 <see cref="LordJob_MissileCrewDuty"/> 給地圖內的敵方 Pawn。<br/>
    /// 3. 偵測「保留觀察狀態 + 所有 NPC 雷達（CompScorer）不可用」→ 哨站摧毀。
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

            List<MapParent> targets = FindPlayerTargetsInRange(source, range);
            if (targets.NullOrEmpty()) return;

            // 4. 為每個空閒通道射出一枚導彈
            for (int i = 0; i < freeChannels; i++)
            {
                Building_MissileRack rack = FindRackWithMissiles();
                if (rack == null)
                {
                    // 無可用導彈，停止嘗試
                    if (Prefs.DevMode)
                        Log.Message("[DMSE MissileBase] 所有 MissileRack 均已耗盡，等待補給。");
                    break;
                }

                // 取出並消耗第一枚導彈
                ThingOwner container = rack.GetDirectlyHeldThings();
                Thing missile = container.FirstOrDefault(t => t.def.HasComp(typeof(CompMissileConfig)));
                MissileConfig cfg = ExtractConfigFromMissile(missile, wc);

                if (cfg == null) break; // 無有效彈體定義，跳出

                container.Remove(missile);
                missile.Destroy();

                // 選擇目標（隨機；未來可擴充為優先級排序）
                MapParent target = targets.RandomElement();

                WorldObject_IncomingMissile wo = IncomingMissileUtility.Launch(
                    source.Tile,
                    target.Tile,
                    cfg,
                    source.Faction,
                    worldObjectDef: wc?.Props.incomingWorldObjectDef);

                if (wo != null)
                {
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
        /// 找到第一個屬於敵方、未摧毀且有導彈庫存的 <see cref="Building_MissileRack"/>。
        /// </summary>
        private Building_MissileRack FindRackWithMissiles()
        {
            Faction enemy = map.ParentFaction;
            if (enemy == null) return null;

            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed) continue;
                if (b is Building_MissileRack rack && rack.StoredCount > 0)
                    return rack;
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

        /// <summary>找出射程內所有具有地圖且屬於玩家陣營的 <see cref="MapParent"/>。</summary>
        private List<MapParent> FindPlayerTargetsInRange(MapParent source, int range)
        {
            List<MapParent> result = new List<MapParent>();
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (!(wo is MapParent mp)) continue;
                if (!mp.HasMap) continue;
                if (mp.Faction == null || !mp.Faction.IsPlayer) continue;
                if (range > 0)
                {
                    float dist = Find.WorldGrid.ApproxDistanceInTiles(source.Tile, mp.Tile);
                    if (dist > range) continue;
                }
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
                    if (b.TryGetComp<CompScorer>() == null) continue;
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
