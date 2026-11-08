using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_MissileBase : WorldObjectCompProperties
    {
        public CompProperties_MissileBase() => compClass = typeof(WorldObjectComp_MissileBase);

        /// <summary>每次發射間隔（天數）。未生成地圖時使用；地圖生成後由 MapComponent_MissileBase 接管。</summary>
        public IntRange missileFireIntervalDays = new IntRange(1, 2);

        /// <summary>波次（P4）：地圖未生成時每次開火同時射向同一目標的枚數。</summary>
        public IntRange missilesPerSalvo = new IntRange(1, 1);

        /// <summary>波次（P4）：地圖已生成時，等上一波全部結束（通道全空）才整波發射，而不是有空通道就補射。</summary>
        public bool fireInWaves = true;

        /// <summary>射程（世界 tile）。0 = 無限。</summary>
        public int range = 20;

        /// <summary>陣地轉移倒計時（天數範圍）。僅在未生成地圖時倒計時。</summary>
        public IntRange relocationDays = new IntRange(15, 30);

        /// <summary>
        /// 玩家地面突擊後撤退時，若敵方雷達仍在，陣地封鎖再進入的持續時間（天數範圍）。<br/>
        /// 封鎖期間，玩家車隊與運輸艙無法進入陣地（模擬敵方重新整備防線，見 <see cref="Patch_MissileBaseReentry"/>）。
        /// </summary>
        public FloatRange reentryBlockedDays = new FloatRange(0.5f, 1.5f);

        /// <summary>發射時使用的彈體（null = 取第一個 MissileBodyDef）。</summary>
        public MissileBodyDef missileBody;

        /// <summary>發射使用的 WorldObjectDef（預設 DMSE_WorldObject_IncomingMissile）。</summary>
        public WorldObjectDef incomingWorldObjectDef;

        /// <summary>
        /// 地圖生成後的定時補給空投（<see cref="MapComponent_MissileBase"/>）；只在地圖上仍有運作中的敵方雷達時投放。<br/>
        /// null = 沿用守軍 <see cref="LordJob_MissileCrewDuty"/> 的低庫存補給。
        /// </summary>
        public MissileBaseSupplyDrop supplyDrop;

        /// <summary>彈架的生成裝填表與定時空投補彈（<see cref="MissileBaseRackSupply"/>）；null = 只靠建築自身的自動填充。</summary>
        public MissileBaseRackSupply rackSupply;

        /// <summary>
        /// 砲塔彈藥（<see cref="CompRefuelable"/>，例如 37 mm／35 mm 砲彈、裝甲砲管）的定時空投與守軍搬運。<br/>
        /// 空投落地後由守軍親自搬到砲塔旁裝填；守軍全滅後不再補給。預設啟用，可在 XML 關閉。
        /// </summary>
        public MissileBaseTurretAmmoSupply turretAmmo = new MissileBaseTurretAmmoSupply();

        /// <summary>
        /// 燃料發電機由守軍抽象補給（NPC 不會自己加燃料）：守軍尚在就維持燃料，生成時也會加滿。
        /// 給靠燒木發電機供電的舊式陣地用；守軍全滅後發電機才會燒盡。
        /// </summary>
        public bool refuelGenerators = false;

        // ── Inspect 字串鍵 ─────────────────────────────────────────────────────

        /// <summary>Inspect 字串：顯示下次發射冷卻倒計時。</summary>
        public string nextFireInspectKey = "DMSE.MissileBase.NextFireIn";

        /// <summary>Inspect 字串：地圖觀察狀態下顯示（取代倒計時）。</summary>
        public string localMonitoringInspectKey = "DMSE.MissileBase.LocalMonitoring";

        /// <summary>Inspect 字串：顯示陣地轉移倒計時。</summary>
        public string relocationInspectKey = "DMSE.MissileBase.RelocationIn";

        /// <summary>Inspect 字串：顯示再進入封鎖剩餘時間。</summary>
        public string reentryBlockedInspectKey = "DMSE.MissileBase.ReentryBlockedIn";
    }

    /// <summary>導彈陣地的定時補給空投設定。</summary>
    public class MissileBaseSupplyDrop
    {
        /// <summary>兩次空投的間隔（天）。</summary>
        public FloatRange intervalDays = new FloatRange(2f, 4f);

        /// <summary>投放物品（null = 生存食品包）。</summary>
        public ThingDef thingDef;

        /// <summary>每名守軍每次投放的數量（生存食品包約一天兩份，間隔最長 4 天）。</summary>
        public int perPawn = 8;

        /// <summary>地圖上已有的存量達到「守軍數 × 此值」時跳過該次投放，避免堆積。</summary>
        public int maxStockPerPawn = 12;

        public ThingDef ThingDef => thingDef ?? ThingDefOf.MealSurvivalPack;
    }

    /// <summary>
    /// 砲塔彈藥的定時空投設定（<see cref="CompProperties_MissileBase.turretAmmo"/>）。<br/>
    /// 彈藥本身不是抽象數字：空投艙會在砲塔附近投下 <see cref="CompRefuelable"/> 接受的實體彈藥
    /// （例如 37 mm 砲彈用的鋼、裝甲砲管用的強化砲管），守軍再親自搬過去裝填
    /// （<see cref="JobDriver_Refuel"/>）；守軍全滅後彈藥落地也不會自己進砲塔。
    /// </summary>
    public class MissileBaseTurretAmmoSupply
    {
        /// <summary>是否啟用砲塔彈藥補給。</summary>
        public bool enabled = true;

        /// <summary>多久檢查一次並空投彈藥（天）。</summary>
        public FloatRange intervalDays = new FloatRange(2f, 4f);

        /// <summary>砲塔彈藥低於最大量這個比例時才補（0.5 = 半個基數）。</summary>
        public float refillBelowPercent = 0.5f;

        /// <summary>空投時把砲塔補到最大量的這個比例（1 = 補滿）。</summary>
        public float refillToPercent = 1f;

        /// <summary>空投落地後多久，守軍才會開始搬（tick）；期間玩家可以搶走或打爛。</summary>
        public int haulDelayTicks = 300;

        /// <summary>同時最多幾名守軍在搬砲塔彈藥。0 = 只空投、不搬。</summary>
        public int maxHaulers = 1;
    }

    /// <summary>
    /// 導彈陣地世界物件元件。<br/>
    /// ‧ 未生成地圖：定期對射程內的玩家殖民地發射導彈；同時倒計時「陣地轉移」（模擬發射陣地易位）。<br/>
    /// ‧ 地圖已生成：暫停本元件，改由 <see cref="MapComponent_MissileBase"/> 以地圖內的建築繼續打擊。<br/>
    /// ‧ 地圖移除後（玩家撤退）：陣地未摧毀時站點保留；若玩家曾地面突擊且敵方雷達尚存，
    ///   啟動再進入封鎖計時（<see cref="reentryBlockedUntil"/>），阻止玩家立即重回陣地；轉移與開火計時同時重設。
    /// </summary>
    public class WorldObjectComp_MissileBase : WorldObjectComp
    {
        private int nextFireTick = -1;
        private int relocationTick = -1;

        /// <summary>
        /// 玩家車隊可再次進入陣地的最早 tick。<br/>
        /// -1 = 無封鎖；> TicksGame = 封鎖中；≤ TicksGame = 封鎖已解除。
        /// </summary>
        private int reentryBlockedUntil = -1;

        public CompProperties_MissileBase Props => (CompProperties_MissileBase)props;

        private bool MapIsGenerated => parent is MapParent mp && mp.HasMap;

        // ── 再進入封鎖 API ────────────────────────────────────────────────────

        /// <summary>玩家車隊目前是否被禁止進入此陣地。</summary>
        public bool IsReentryBlocked
            => reentryBlockedUntil > 0 && Find.TickManager.TicksGame < reentryBlockedUntil;

        /// <summary>封鎖剩餘 tick（已解除則為 0）。</summary>
        public int ReentryBlockedFor
            => IsReentryBlocked ? reentryBlockedUntil - Find.TickManager.TicksGame : 0;

        /// <summary>
        /// 啟動再進入封鎖（由 <see cref="SitePartWorker_MissileBase"/> 在玩家地面突擊後撤退且敵方雷達尚存時呼叫）。
        /// </summary>
        public void StartReentryCooldown()
        {
            int ticks = Mathf.RoundToInt(Props.reentryBlockedDays.RandomInRange * GenDate.TicksPerDay);
            reentryBlockedUntil = Find.TickManager.TicksGame + ticks;
            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 再進入封鎖啟動，持續 {ticks.ToStringTicksToPeriod()}。");
        }

        // ──────────────── 初始化 ────────────────

        public override void Initialize(WorldObjectCompProperties p)
        {
            base.Initialize(p);
            ScheduleFirstFire();
            ScheduleRelocation();
        }

        private void ScheduleFirstFire()
        {
            if (nextFireTick < 0)
                nextFireTick = Find.TickManager.TicksGame
                    + Mathf.RoundToInt(Props.missileFireIntervalDays.RandomInRange * GenDate.TicksPerDay);
        }

        private void ScheduleRelocation()
        {
            relocationTick = Find.TickManager.TicksGame
                + Mathf.RoundToInt(Props.relocationDays.RandomInRange * GenDate.TicksPerDay);
        }

        // ──────────────── Tick ────────────────

        public override void CompTick()
        {
            base.CompTick();
            if (parent.Destroyed || MapIsGenerated) return;

            int now = Find.TickManager.TicksGame;

            // 陣地轉移計時到 → 摧毀站點
            if (relocationTick > 0 && now >= relocationTick)
            {
                OnRelocation();
                return;
            }

            // 補償時間加速：while 迴圈
            while (nextFireTick > 0 && now >= nextFireTick)
            {
                TryFireAtPlayerColonies();
                nextFireTick = now + Mathf.RoundToInt(Props.missileFireIntervalDays.RandomInRange * GenDate.TicksPerDay);
            }
        }

        // ──────────────── 射擊 ────────────────

        /// <summary>對一個打得到的玩家據點同時發射一波（<see cref="CompProperties_MissileBase.missilesPerSalvo"/> 枚）；回傳發射數。</summary>
        public int TryFireAtPlayerColonies()
        {
            MissileConfig cfg = BuildFireConfig();
            if (cfg == null) return 0;

            List<MapParent> targets = FindTargetsInRange(cfg);
            if (targets.NullOrEmpty()) return 0;

            MapParent target = MissileDoctrine.Choose(parent as MapParent, parent.Faction, cfg, targets);
            int count = Mathf.Max(1, Props.missilesPerSalvo.RandomInRange);
            for (int i = 0; i < count; i++)
            {
                IncomingMissileUtility.Launch(
                    parent.Tile,
                    target.Tile,
                    BuildFireConfig(),
                    parent.Faction,
                    worldObjectDef: Props.incomingWorldObjectDef,
                    target: target);
            }

            // 敵方導彈陣地第一次向玩家殖民地發射：教學「敵方導彈陣地」。
            if (count > 0)
            {
                DMSE_Concepts.TeachEnemyMissileBases();
            }

            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 世界層齊射 {count} 枚：{parent.Tile} → {target.Tile}");
            return count;
        }

        /// <summary>
        /// 找出這枚導彈打得到的玩家地圖：射程不超過陣地射程與導彈射程；
        /// 跨圖層（例：軌道上的據點或轉移飛行中的船）需導彈具備導引能力。
        /// </summary>
        private List<MapParent> FindTargetsInRange(MissileConfig cfg)
            => MissileDoctrine.Targets(parent as MapParent, parent.Faction, cfg,
                mp => MissileWorldUtility.CanEngage(cfg, parent.Tile, mp.Tile, Props.range, out _));

        public MissileConfig BuildFireConfig()
        {
            MissileBodyDef body = Props.missileBody
                ?? DefDatabase<MissileBodyDef>.AllDefsListForReading.FirstOrDefault();
            if (body == null) return null;
            return new MissileConfig(body);
        }

        // ──────────────── 陣地轉移 ────────────────

        private void OnRelocation()
        {
            if (FindTargetsInRange(BuildFireConfig()).Any())
            {
                Find.LetterStack.ReceiveLetter(
                    "DMSE.MissileBase.Relocated".Translate(),
                    "DMSE.MissileBase.RelocatedDesc".Translate(),
                    LetterDefOf.NeutralEvent,
                    new GlobalTargetInfo(parent.Tile));
            }
            parent.Destroy();
        }

        /// <summary>
        /// 地圖移除前由 <see cref="SitePartWorker_MissileBase"/> 呼叫：重設轉移計時（模擬重新部署），
        /// 並從現在重新排程世界層開火——地圖存在期間不計時，過期的計時會讓陣地一撤離就立刻齊射。
        /// </summary>
        public void Notify_SiteMapRemoved()
        {
            ScheduleRelocation();
            nextFireTick = Find.TickManager.TicksGame
                + Mathf.RoundToInt(Props.missileFireIntervalDays.RandomInRange * GenDate.TicksPerDay);
        }

        // ──────────────── Inspect ────────────────

        public override string CompInspectStringExtra()
        {
            if (parent.Destroyed) return null;

            if (MapIsGenerated)
                return Props.localMonitoringInspectKey.Translate();

            int now = Find.TickManager.TicksGame;
            var sb = new System.Text.StringBuilder();

            // 再進入封鎖
            if (IsReentryBlocked)
            {
                sb.AppendLine(Props.reentryBlockedInspectKey.Translate(
                    ReentryBlockedFor.ToStringTicksToPeriod()));
            }

            // 下次發射倒計時
            int fireLeft = nextFireTick - now;
            if (fireLeft > 0)
                sb.AppendLine(Props.nextFireInspectKey.Translate(fireLeft.ToStringTicksToPeriod()));

            // 陣地轉移倒計時
            int relocLeft = relocationTick - now;
            if (relocLeft > 0)
                sb.Append(Props.relocationInspectKey.Translate(relocLeft.ToStringTicksToPeriod()));

            return sb.ToString().TrimEnd('\n');
        }

        // ──────────────── 存讀 ────────────────

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref nextFireTick, "nextFireTick", -1);
            Scribe_Values.Look(ref relocationTick, "relocationTick", -1);
            Scribe_Values.Look(ref reentryBlockedUntil, "reentryBlockedUntil", -1);
        }
    }
}
