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

        /// <summary>射程（世界 tile）。0 = 無限。</summary>
        public int range = 20;

        /// <summary>陣地轉移倒計時（天數範圍）。僅在未生成地圖時倒計時。</summary>
        public IntRange relocationDays = new IntRange(15, 30);

        /// <summary>
        /// 玩家撤退時，若敵方火控雷達仍存活，陣地封鎖地面再進入的持續時間（天數範圍）。<br/>
        /// 封鎖期間，玩家車隊無法從地面進入陣地（模擬敵方重新整備防線）。
        /// </summary>
        public FloatRange reentryBlockedDays = new FloatRange(0.5f, 1.5f);

        /// <summary>發射時使用的彈體（null = 取第一個 MissileBodyDef）。</summary>
        public MissileBodyDef missileBody;

        /// <summary>發射使用的 WorldObjectDef（預設 DMSE_WorldObject_IncomingMissile）。</summary>
        public WorldObjectDef incomingWorldObjectDef;

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

    /// <summary>
    /// 導彈陣地世界物件元件。<br/>
    /// ‧ 未生成地圖：定期對射程內的玩家殖民地發射導彈；同時倒計時「陣地轉移」（模擬發射陣地易位）。<br/>
    /// ‧ 地圖已生成：暫停本元件，改由 <see cref="MapComponent_MissileBase"/> 以地圖內的建築繼續打擊。<br/>
    /// ‧ 地圖移除後（玩家撤退）：若敵方火控雷達尚存，啟動再進入封鎖計時（<see cref="reentryBlockedUntil"/>），
    ///   阻止玩家立即重回陣地；轉移計時同時重設。
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
        /// 啟動再進入封鎖（由 <see cref="SitePartWorker_MissileBase"/> 在玩家撤退且敵方雷達尚存時呼叫）。
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

        private void TryFireAtPlayerColonies()
        {
            List<MapParent> targets = FindTargetsInRange();
            if (targets.NullOrEmpty()) return;

            MapParent target = targets.RandomElement();
            MissileConfig cfg = BuildFireConfig();
            if (cfg == null) return;

            IncomingMissileUtility.Launch(
                parent.Tile,
                target.Tile,
                cfg,
                parent.Faction,
                worldObjectDef: Props.incomingWorldObjectDef);

            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 世界層發射：{parent.Tile} → {target.Tile}");
        }

        private List<MapParent> FindTargetsInRange()
        {
            List<MapParent> result = new List<MapParent>();
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (!(wo is MapParent mp)) continue;
                if (!mp.HasMap) continue;
                if (mp.Faction == null || !mp.Faction.IsPlayer) continue;
                if (Props.range > 0)
                {
                    float dist = Find.WorldGrid.ApproxDistanceInTiles(parent.Tile, mp.Tile);
                    if (dist > Props.range) continue;
                }
                result.Add(mp);
            }
            return result;
        }

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
            if (FindTargetsInRange().Any())
            {
                Find.LetterStack.ReceiveLetter(
                    "DMSE.MissileBase.Relocated".Translate(),
                    "DMSE.MissileBase.RelocatedDesc".Translate(),
                    LetterDefOf.NeutralEvent,
                    new GlobalTargetInfo(parent.Tile));
            }
            parent.Destroy();
        }

        /// <summary>地圖移除後由 <see cref="SitePartWorker_MissileBase"/> 呼叫，重設轉移計時（模擬重新部署）。</summary>
        public void ResetRelocationTimer() => ScheduleRelocation();

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
