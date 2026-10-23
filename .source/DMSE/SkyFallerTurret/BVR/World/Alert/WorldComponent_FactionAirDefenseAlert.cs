using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>一筆已入帳的事件權重（同航跡升格時補差額、齊射上限、攻擊波獎勵共用）。</summary>
    public class AirDefenseAlertRecord : IExposable
    {
        public string key;
        public float local, regional;
        public int tick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref local, "local");
            Scribe_Values.Look(ref regional, "regional");
            Scribe_Values.Look(ref tick, "tick");
        }
    }

    /// <summary>指向本地或支援區的攻擊波紀錄。</summary>
    public class AirDefenseWaveRecord : IExposable
    {
        public string id;
        public int tick;
        public bool local;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref local, "local");
        }
    }

    /// <summary>齊射分組：同來源、同目標區域、相隔不超過 <see cref="AirDefenseAlertTuningDef.salvoGroupTicks"/>。</summary>
    public class AirDefenseSalvoGroup : IExposable
    {
        public string id;
        public Faction attacker;
        public PlanetTile destination = PlanetTile.Invalid;
        public int lastTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_References.Look(ref attacker, "attacker");
            Scribe_Values.Look(ref destination, "destination", PlanetTile.Invalid);
            Scribe_Values.Look(ref lastTick, "lastTick");
        }
    }

    /// <summary>封鎖信件的合併紀錄（同陣營同區域冷卻）。</summary>
    public class AirDefenseNoticeRecord : IExposable
    {
        public Faction faction;
        public PlanetTile tile = PlanetTile.Invalid;
        public int tick;

        public void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref tile, "tile", PlanetTile.Invalid);
            Scribe_Values.Look(ref tick, "tick");
        }
    }

    /// <summary>
    /// 一個據點的警戒狀態（§5.1）。以穩定據點 ID＋陣營為鍵，不掛在每次重建的 <see cref="BVRNode"/> 上；
    /// 地圖生成／卸載不影響，據點改朝換代時重置。
    /// </summary>
    public class AirDefenseSiteAlert : IExposable
    {
        public int siteId = -1;
        public Faction faction;
        public int stage = 1;
        public int baseline = 1;
        public int stageSinceTick;
        /// <summary>本地壓力 L 與區域壓力 R（0–100）；以 <see cref="lastDecayTick"/> 惰性衰減。</summary>
        public float local, regional;
        public int lastDecayTick = -1;
        /// <summary>第一階段轉開機時的雷達暖機截止 tick。</summary>
        public int radarWarmUntil = -1;
        /// <summary>已確認的直接來襲威脅保底到此 tick（航跡消失或情報過期即失效）。</summary>
        public int directThreatUntil = -1;
        /// <summary>封鎖公告中：到此 tick 正式生效；-1 = 沒有待生效的封鎖。</summary>
        public int blockadeAt = -1;
        /// <summary>已對玩家發過「第 N 階段接戰」警告的最高階段。</summary>
        public int warnedStage;
        public List<AirDefenseAlertRecord> records = new List<AirDefenseAlertRecord>();
        public List<AirDefenseWaveRecord> waves = new List<AirDefenseWaveRecord>();

        [Unsaved] public MapParent parent;

        public bool BlockadePending => blockadeAt >= 0 && stage < (int)AirDefenseStage.Blockade;
        public bool BlockadeActive => stage >= (int)AirDefenseStage.Blockade;

        public float Threat => AirDefenseAlertPolicy.EffectiveThreat(local, regional);

        public int RecentWaves(int now, bool localOnly = false)
        {
            int window = AirDefenseAlertTuningDef.Current.attackWindowTicks;
            int count = 0;
            for (int i = 0; i < waves.Count; i++)
            {
                if (now - waves[i].tick <= window && (!localOnly || waves[i].local)) { count++; }
            }
            return count;
        }

        public void Decay(int now)
        {
            if (lastDecayTick >= 0 && now > lastDecayTick)
            {
                local = AirDefenseAlertPolicy.Decayed(local, now - lastDecayTick);
                regional = AirDefenseAlertPolicy.Decayed(regional, now - lastDecayTick);
            }
            lastDecayTick = now;
        }

        public AirDefenseAlertRecord Record(string key, int now)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].key == key) { records[i].tick = now; return records[i]; }
            }
            var record = new AirDefenseAlertRecord { key = key, tick = now };
            records.Add(record);
            return record;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref siteId, "siteId", -1);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref stage, "stage", 1);
            Scribe_Values.Look(ref baseline, "baseline", 1);
            Scribe_Values.Look(ref stageSinceTick, "stageSinceTick");
            Scribe_Values.Look(ref local, "local");
            Scribe_Values.Look(ref regional, "regional");
            Scribe_Values.Look(ref lastDecayTick, "lastDecayTick", -1);
            Scribe_Values.Look(ref radarWarmUntil, "radarWarmUntil", -1);
            Scribe_Values.Look(ref directThreatUntil, "directThreatUntil", -1);
            Scribe_Values.Look(ref blockadeAt, "blockadeAt", -1);
            Scribe_Values.Look(ref warnedStage, "warnedStage");
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            Scribe_Collections.Look(ref waves, "waves", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                records ??= new List<AirDefenseAlertRecord>();
                waves ??= new List<AirDefenseWaveRecord>();
            }
        }
    }

    /// <summary>
    /// NPC 防空警戒（docs/NPC防空警戒五階段_執行計畫.md）：陣營共享情報、據點分別升降級。
    /// 持久保存據點壓力、階段、時間、去重事件與公告狀態；接戰與封鎖由 <see cref="WorldComponent_BVRNetwork"/>
    /// 與 <see cref="AirspaceRestrictionUtility"/> 依這裡的階段執行。
    /// </summary>
    public class WorldComponent_FactionAirDefenseAlert : WorldComponent
    {
        public static WorldComponent_FactionAirDefenseAlert Current => Find.World?.GetComponent<WorldComponent_FactionAirDefenseAlert>();

        private const int EvaluateIntervalTicks = 250;

        private List<AirDefenseSiteAlert> siteList = new List<AirDefenseSiteAlert>();
        private List<AirDefenseSalvoGroup> salvoGroups = new List<AirDefenseSalvoGroup>();
        private List<AirDefenseNoticeRecord> notices = new List<AirDefenseNoticeRecord>();
        private List<Faction> neutralWarned = new List<Faction>();
        private int nextSalvoId;

        private readonly Dictionary<int, AirDefenseSiteAlert> sites = new Dictionary<int, AirDefenseSiteAlert>();
        private readonly Dictionary<int, MapParent> parentCache = new Dictionary<int, MapParent>();

        public WorldComponent_FactionAirDefenseAlert(World world) : base(world) { }

        private static AirDefenseAlertTuningDef Tuning => AirDefenseAlertTuningDef.Current;
        public static bool Enabled => Tuning.enabled;
        private static int Now => Find.TickManager.TicksGame;

        public IEnumerable<AirDefenseSiteAlert> AllStates => siteList;

        // ── 據點狀態 ──────────────────────────────────────────────────────

        /// <summary>NPC 據點的警戒狀態；玩家／無陣營據點回傳 null。據點換了陣營時清除前陣營命令。</summary>
        public AirDefenseSiteAlert StateFor(MapParent parent, bool create = true)
        {
            if (parent == null || parent.Destroyed) { return null; }
            Faction faction = parent.Faction;
            if (faction == null || faction.IsPlayer) { return null; }
            if (sites.TryGetValue(parent.ID, out AirDefenseSiteAlert state))
            {
                state.parent = parent;
                if (state.faction != faction) { Reset(state, parent); }
                return state;
            }
            if (!create) { return null; }
            state = new AirDefenseSiteAlert { siteId = parent.ID, parent = parent };
            Reset(state, parent);
            sites[parent.ID] = state;
            siteList.Add(state);
            return state;
        }

        private void Reset(AirDefenseSiteAlert state, MapParent parent)
        {
            int now = Now;
            state.faction = parent.Faction;
            state.baseline = BaselineFor(parent);
            state.stage = state.baseline;
            state.stageSinceTick = now;
            state.local = state.regional = 0f;
            state.lastDecayTick = now;
            state.radarWarmUntil = -1;
            state.directThreatUntil = -1;
            state.blockadeAt = -1;
            state.warnedStage = 0;
            state.records.Clear();
            state.waves.Clear();
        }

        /// <summary>現在的命令階段；玩家據點與無陣營據點為 0（不適用）。</summary>
        public int StageOf(MapParent parent) => StateFor(parent)?.stage ?? 0;

        /// <summary>雷達是否已完成暖機（第一階段轉開機要等 <see cref="AirDefenseAlertTuningDef.radarWarmupTicks"/>）。</summary>
        public bool RadarWarm(MapParent parent)
        {
            AirDefenseSiteAlert state = StateFor(parent);
            return state == null || Now >= state.radarWarmUntil;
        }

        /// <summary>
        /// 戰備基準（§4）：專業軍事／導彈基地（有 <see cref="WorldObjectComp_AirDefense"/>）第三階段；
        /// 具雷達的普通據點第二階段；無設備者第一階段。不看地圖是否生成，避免地圖生成／卸載改變基準。
        /// </summary>
        public static int BaselineFor(MapParent parent)
        {
            AirDefenseAlertTuningDef t = Tuning;
            if (parent?.GetComponent<WorldObjectComp_AirDefense>() != null) { return t.baselineMilitary; }
            return RadarCapable(parent) ? t.baselineRadar : t.baselineNoRadar;
        }

        /// <summary>據點是否具備可啟用的雷達：專屬防空元件、可合成防空的科技等級，或地圖上同陣營的搜索雷達。</summary>
        public static bool RadarCapable(MapParent parent)
        {
            if (parent == null || parent.Faction == null || parent.Faction.IsPlayer) { return false; }
            if (parent.GetComponent<WorldObjectComp_AirDefense>() != null) { return true; }
            if (parent.Faction.def != null && parent.Faction.def.techLevel >= TechLevel.Industrial) { return true; }
            MapComponent_BVRCombat local = parent.HasMap ? parent.Map.GetComponent<MapComponent_BVRCombat>() : null;
            return local != null && local.searchRadars.Any(r => r.parent.Faction == parent.Faction);
        }

        private MapParent ParentOf(AirDefenseSiteAlert state)
        {
            if (state.parent != null && !state.parent.Destroyed) { return state.parent; }
            if (!parentCache.TryGetValue(state.siteId, out MapParent parent) || parent == null || parent.Destroyed)
            {
                parentCache.Clear();
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<MapParent>()) { parentCache[p.ID] = p; }
                parentCache.TryGetValue(state.siteId, out parent);
            }
            state.parent = parent != null && !parent.Destroyed ? parent : null;
            return state.parent;
        }

        // ── 事件入帳（§5.2） ───────────────────────────────────────────────

        /// <summary>
        /// 本陣營節點看到航跡時呼叫（每次掃描）：首次過境、確認瞄準己方、直接威脅保底。
        /// 同一事件經多個錨點到達同一據點只計一次（每據點以航跡為鍵去重）。
        /// </summary>
        public void ReportObservation(BVRNode node, IBVRTrack track, AirDefenseObservation observation, int now)
        {
            if (!Enabled || node?.parent == null || node.faction == null || node.faction.IsPlayer) { return; }
            AirDefenseSiteAlert observer = StateFor(node.parent);
            if (observer == null) { return; }
            string waveId = WaveIdFor(track, now);
            string trackKey = TrackKey(track);

            if (!observation.reportedSites.Contains(node.parent.ID))
            {
                observation.reportedSites.Add(node.parent.ID);
                AddLocal(observer, trackKey, waveId, Tuning.transitLocal, Tuning.transitSalvoCapLocal, now);
                Propagate(node.parent, node.faction, s => AddRegional(s, trackKey, waveId, Tuning.transitRegional, Tuning.transitSalvoCapRegional, now));
                Evaluate(observer, now);
            }

            MapParent target = TargetSite(track);
            if (target == null || target.Faction != node.faction) { return; }
            AirDefenseSiteAlert targeted = StateFor(target);
            if (targeted == null) { return; }
            // 直接威脅保底：直到航跡消失或情報過期（最後一次看到 + 有效期限）。
            targeted.directThreatUntil = Mathf.Max(targeted.directThreatUntil, now + Tuning.intelExpiryTicks);
            if (observation.targetedReportedFor == track.Destination)
            {
                if (targeted.stage < (int)AirDefenseStage.Passive) { Evaluate(targeted, now); }
                return;
            }
            observation.targetedReportedFor = track.Destination;
            AddLocal(targeted, trackKey, null, Tuning.targetedLocal, 0f, now);
            RecordWave(targeted, waveId, true, now);
            Propagate(target, node.faction, s =>
            {
                AddRegional(s, trackKey, null, Tuning.targetedRegional, 0f, now);
                if (s != targeted) { RecordWave(s, waveId, false, now); }
            });
            if (node.parent != target)
            {
                Propagate(node.parent, node.faction, s => AddRegional(s, trackKey, null, Tuning.targetedRegional, 0f, now));
            }
            EvaluateAll(now);
        }

        /// <summary>
        /// 受襲通報（§4）：導彈抵達非盟友據點（含反輻射壓制）。即使第一階段也能事後獲知，只記錄實際可知的攻擊者。
        /// </summary>
        public void ReportHit(IBVRTrack track, MapParent site)
        {
            if (!Enabled || site == null) { return; }
            AirDefenseSiteAlert state = StateFor(site);
            if (state == null) { return; }
            if (track.Attacker != null && AirDefenseAlertPolicy.IsFriendly(state.faction, track.Attacker)) { return; }
            int now = Now;
            string waveId = WaveIdFor(track, now);
            string trackKey = TrackKey(track);
            ApplyAttack(state, site, trackKey, waveId, Tuning.hitLocal, Tuning.hitRegional, now);
            EvaluateAll(now);
        }

        /// <summary>攻擊事件（瞄準／命中）：受攻擊據點入帳本地壓力與攻擊波，支援區入帳區域壓力與支援區攻擊波。</summary>
        private void ApplyAttack(AirDefenseSiteAlert state, MapParent site, string trackKey, string waveId, float local, float regional, int now)
        {
            AddLocal(state, trackKey, null, local, 0f, now);
            RecordWave(state, waveId, true, now);
            Propagate(site, state.faction, s =>
            {
                AddRegional(s, trackKey, null, regional, 0f, now);
                if (s != state) { RecordWave(s, waveId, false, now); }
            });
        }

        /// <summary>開發工具用：直接注入事件（不需要真實航跡）。</summary>
        public void DevInject(MapParent site, string kind)
        {
            AirDefenseSiteAlert state = StateFor(site);
            if (state == null) { return; }
            int now = Now;
            string key = "dev" + now + ":" + Rand.Int;
            string wave = "devwave" + now + ":" + Rand.Int;
            switch (kind)
            {
                case "transit":
                    AddLocal(state, key, wave, Tuning.transitLocal, Tuning.transitSalvoCapLocal, now);
                    Propagate(site, state.faction, s => AddRegional(s, key, wave, Tuning.transitRegional, Tuning.transitSalvoCapRegional, now));
                    break;
                case "targeted":
                    state.directThreatUntil = now + Tuning.intelExpiryTicks;
                    ApplyAttack(state, site, key, wave, Tuning.targetedLocal, Tuning.targetedRegional, now);
                    break;
                case "hit":
                    ApplyAttack(state, site, key, wave, Tuning.hitLocal, Tuning.hitRegional, now);
                    break;
            }
            EvaluateAll(now);
        }

        public void DevAddPressure(MapParent site, float local, float regional)
        {
            AirDefenseSiteAlert state = StateFor(site);
            if (state == null) { return; }
            state.Decay(Now);
            state.local = Mathf.Clamp(state.local + local, 0f, 100f);
            state.regional = Mathf.Clamp(state.regional + regional, 0f, 100f);
            Evaluate(state, Now);
        }

        /// <summary>開發工具用：強制指定階段（第五階段立即生效，不經公告）。</summary>
        public void DevSetStage(MapParent site, int stage)
        {
            AirDefenseSiteAlert state = StateFor(site);
            if (state == null) { return; }
            int now = Now;
            SetStage(state, Mathf.Clamp(stage, 1, 5), now);
            state.blockadeAt = -1;
            if (stage >= 5 && state.RecentWaves(now) < Tuning.stage5MinWaves)
            {
                // 讓強制封鎖不會在下一次評估時因「沒有攻擊紀錄」立刻解除。
                for (int i = state.RecentWaves(now); i < Tuning.stage5MinWaves; i++)
                    state.waves.Add(new AirDefenseWaveRecord { id = "dev" + now + ":" + i, tick = now, local = true });
                state.local = Mathf.Max(state.local, Tuning.stage5Up);
            }
            state.stageSinceTick = now;
            AirspaceRestrictionUtility.Invalidate();
        }

        private static string TrackKey(IBVRTrack track) => "t" + (track.Self?.ID ?? 0);

        private void AddLocal(AirDefenseSiteAlert state, string trackKey, string salvoId, float weight, float salvoCap, int now)
        {
            state.Decay(now);
            AirDefenseAlertRecord record = state.Record("L:" + trackKey, now);
            float delta = AirDefenseAlertPolicy.TopUp(ref record.local, weight);
            if (delta > 0f && salvoCap > 0f && salvoId != null)
            {
                AirDefenseAlertRecord salvo = state.Record("SL:" + salvoId, now);
                delta = Mathf.Min(delta, Mathf.Max(0f, salvoCap - salvo.local));
                salvo.local += delta;
            }
            state.local = Mathf.Clamp(state.local + delta, 0f, 100f);
        }

        private void AddRegional(AirDefenseSiteAlert state, string trackKey, string salvoId, float weight, float salvoCap, int now)
        {
            state.Decay(now);
            AirDefenseAlertRecord record = state.Record("R:" + trackKey, now);
            float delta = AirDefenseAlertPolicy.TopUp(ref record.regional, weight);
            if (delta > 0f && salvoCap > 0f && salvoId != null)
            {
                AirDefenseAlertRecord salvo = state.Record("SR:" + salvoId, now);
                delta = Mathf.Min(delta, Mathf.Max(0f, salvoCap - salvo.regional));
                salvo.regional += delta;
            }
            state.regional = Mathf.Clamp(state.regional + delta, 0f, 100f);
        }

        /// <summary>
        /// 記錄指向本地／支援區的攻擊波；24 遊戲小時內已有另一個獨立攻擊波時，每新波給一次重複攻擊獎勵。
        /// </summary>
        private void RecordWave(AirDefenseSiteAlert state, string waveId, bool local, int now)
        {
            if (waveId == null) { return; }
            int window = Tuning.attackWindowTicks;
            AirDefenseWaveRecord existing = state.waves.FirstOrDefault(w => w.id == waveId);
            if (existing != null)
            {
                existing.tick = now;
                existing.local |= local;
                return;
            }
            bool repeat = state.waves.Any(w => now - w.tick <= window);
            state.waves.Add(new AirDefenseWaveRecord { id = waveId, tick = now, local = local });
            if (!repeat) { return; }
            AirDefenseAlertRecord bonus = state.Record("W:" + waveId, now);
            state.Decay(now);
            if (local)
            {
                state.local = Mathf.Clamp(state.local + AirDefenseAlertPolicy.TopUp(ref bonus.local, Tuning.repeatWaveLocal), 0f, 100f);
            }
            else
            {
                state.regional = Mathf.Clamp(state.regional + AirDefenseAlertPolicy.TopUp(ref bonus.regional, Tuning.repeatWaveRegional), 0f, 100f);
            }
        }

        /// <summary>警報傳播：同陣營、同圖層、在錨點支援半徑內的據點（含錨點本身）。</summary>
        private void Propagate(MapParent anchor, Faction faction, System.Action<AirDefenseSiteAlert> apply)
        {
            if (anchor == null || faction == null) { return; }
            float radius = Tuning.supportRadiusTiles;
            Vector3 center = MissileWorldUtility.TileCenter(anchor.Tile);
            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                if (!(all[i] is MapParent p) || p.Destroyed || p.Faction != faction) { continue; }
                if (!MissileWorldUtility.SameLayer(p.Tile, anchor.Tile)) { continue; }
                if (p != anchor && MissileWorldUtility.SurfaceTilesBetween(center, MissileWorldUtility.TileCenter(p.Tile)) > radius) { continue; }
                AirDefenseSiteAlert state = StateFor(p);
                if (state != null) { apply(state); }
            }
        }

        /// <summary>航跡所屬的攻擊波：已有任務／波次 ID 時沿用，否則依來源、目標區域與時間分組。</summary>
        private string WaveIdFor(IBVRTrack track, int now)
        {
            BVRTrackState state = track.State;
            if (state.alertWaveId != null) { return state.alertWaveId; }
            if (track is ScorerProjectile_WorldObject strike && strike.strikeMissionId >= 0)
            {
                return state.alertWaveId = "m" + strike.strikeMissionId + ":" + strike.strikeWave;
            }
            salvoGroups.RemoveAll(g => now - g.lastTick > Tuning.salvoGroupTicks * 4);
            PlanetTile destination = track.Destination;
            Vector3 destCenter = MissileWorldUtility.TileCenter(destination);
            foreach (AirDefenseSalvoGroup group in salvoGroups)
            {
                if (group.attacker != track.Attacker || now - group.lastTick > Tuning.salvoGroupTicks) { continue; }
                if (!MissileWorldUtility.SameLayer(group.destination, destination)) { continue; }
                if (MissileWorldUtility.SurfaceTilesBetween(MissileWorldUtility.TileCenter(group.destination), destCenter) > 2f) { continue; }
                group.lastTick = now;
                return state.alertWaveId = group.id;
            }
            var created = new AirDefenseSalvoGroup { id = "g" + nextSalvoId++, attacker = track.Attacker, destination = destination, lastTick = now };
            salvoGroups.Add(created);
            return state.alertWaveId = created.id;
        }

        public static MapParent TargetSite(IBVRTrack track)
        {
            if (track is MissileWorldObject missile && missile.targetParent != null && !missile.targetParent.Destroyed
                && missile.targetParent.Tile == track.Destination) { return missile.targetParent; }
            return Find.WorldObjects.MapParentAt(track.Destination);
        }

        // ── 升降級（§5.3） ─────────────────────────────────────────────────

        private void EvaluateAll(int now)
        {
            for (int i = 0; i < siteList.Count; i++) { Evaluate(siteList[i], now); }
        }

        private void Evaluate(AirDefenseSiteAlert state, int now)
        {
            MapParent parent = ParentOf(state);
            if (parent == null || state.faction == null) { return; }
            if (parent.Faction != state.faction) { Reset(state, parent); }
            state.Decay(now);
            state.baseline = BaselineFor(parent);
            AirDefenseAlertTuningDef t = Tuning;
            float threat = state.Threat;
            int waves = state.RecentWaves(now);
            var inputs = new AirDefenseStageInputs
            {
                current = state.stage,
                baseline = state.baseline,
                threat = threat,
                hasRadar = RadarCapable(parent),
                directThreat = now <= state.directThreatUntil,
                recentWaves = waves,
                holdElapsed = now - state.stageSinceTick >= t.minStageHoldTicks,
            };
            int next = AirDefenseAlertPolicy.NextStage(inputs);

            if (next >= (int)AirDefenseStage.Blockade && !state.BlockadeActive)
            {
                // 封鎖命令先公告，等待公告期後才生效；期間階段維持第四。
                if (state.blockadeAt < 0)
                {
                    state.blockadeAt = now + t.blockadeNoticeTicks;
                    NotifyBlockade(parent, state, "DMSE.AirAlert.BlockadeNoticeLabel", "DMSE.AirAlert.BlockadeNoticeText", LetterDefOf.ThreatBig);
                }
                next = now >= state.blockadeAt ? (int)AirDefenseStage.Blockade : Mathf.Max(state.stage, (int)AirDefenseStage.Active);
            }
            else if (state.BlockadePending && AirDefenseAlertPolicy.BlockadeShouldLift(threat, waves))
            {
                state.blockadeAt = -1;
                Messages.Message("DMSE.AirAlert.BlockadeCancelled".Translate(state.faction.Name, parent.LabelCap), parent,
                    MessageTypeDefOf.PositiveEvent, historical: false);
            }
            else if (state.BlockadePending)
            {
                next = now >= state.blockadeAt ? (int)AirDefenseStage.Blockade : Mathf.Max(next, (int)AirDefenseStage.Active);
            }

            if (next != state.stage) { SetStage(state, next, now); }
        }

        private void SetStage(AirDefenseSiteAlert state, int next, int now)
        {
            int previous = state.stage;
            MapParent parent = ParentOf(state);
            if (previous <= (int)AirDefenseStage.NoDetection && next > previous)
            {
                state.radarWarmUntil = now + Tuning.radarWarmupTicks;
            }
            state.stage = next;
            state.stageSinceTick = now;
            if (next < (int)AirDefenseStage.Passive) { state.warnedStage = 0; }
            if (next >= (int)AirDefenseStage.Blockade && previous < (int)AirDefenseStage.Blockade && parent != null)
            {
                state.blockadeAt = -1;
                NotifyBlockade(parent, state, "DMSE.AirAlert.BlockadeActiveLabel", "DMSE.AirAlert.BlockadeActiveText", LetterDefOf.NegativeEvent);
            }
            else if (previous >= (int)AirDefenseStage.Blockade && next < previous && parent != null)
            {
                state.blockadeAt = -1;
                Messages.Message("DMSE.AirAlert.BlockadeLifted".Translate(state.faction.Name, parent.LabelCap), parent,
                    MessageTypeDefOf.PositiveEvent, historical: false);
            }
            AirspaceRestrictionUtility.Invalidate();
        }

        // ── 玩家通知（§9） ─────────────────────────────────────────────────

        /// <summary>封鎖公告／生效：相同陣營同區域在冷卻內合併為訊息，避免洗版。</summary>
        private void NotifyBlockade(MapParent parent, AirDefenseSiteAlert state, string labelKey, string textKey, LetterDef letterDef)
        {
            int now = Now;
            AirDefenseAlertTuningDef t = Tuning;
            notices.RemoveAll(n => n.faction == null || now - n.tick > t.noticeCooldownTicks);
            Vector3 center = MissileWorldUtility.TileCenter(parent.Tile);
            bool merged = notices.Any(n => n.faction == state.faction && MissileWorldUtility.SameLayer(n.tile, parent.Tile)
                && MissileWorldUtility.SurfaceTilesBetween(MissileWorldUtility.TileCenter(n.tile), center) <= t.supportRadiusTiles
                && n.tick != now);
            float radius = AirspaceRestrictionUtility.NominalRadius(parent);
            string delay = Mathf.Max(0, state.blockadeAt - now).ToStringTicksToPeriod();
            TaggedString text = textKey.Translate(state.faction.Name, parent.LabelCap, radius.ToString("0.#"), delay);
            if (merged)
            {
                Messages.Message(text, parent, MessageTypeDefOf.ThreatSmall, historical: true);
            }
            else
            {
                Find.LetterStack.ReceiveLetter(labelKey.Translate(), text, letterDef, parent);
            }
            notices.Add(new AirDefenseNoticeRecord { faction = state.faction, tile = parent.Tile, tick = now });
        }

        /// <summary>
        /// 第三／第四階段首次影響玩家航跡時給一次警告；非敵對陣營首次攔截玩家導彈時另給一次說明（不改變外交狀態）。
        /// </summary>
        public void NotifyEngagingPlayer(BVRNode node, IBVRTrack track)
        {
            if (node?.faction == null || node.faction.IsPlayer || track.Attacker != Faction.OfPlayer) { return; }
            AirDefenseSiteAlert state = StateFor(node.parent);
            if (state == null || state.stage < (int)AirDefenseStage.Passive) { return; }
            if (!node.faction.HostileTo(Faction.OfPlayer) && !neutralWarned.Contains(node.faction))
            {
                neutralWarned.Add(node.faction);
                Find.LetterStack.ReceiveLetter("DMSE.AirAlert.NeutralInterceptLabel".Translate(),
                    "DMSE.AirAlert.NeutralInterceptText".Translate(node.faction.Name, node.parent.LabelCap, StageLabel(state.stage)),
                    LetterDefOf.NegativeEvent, new GlobalTargetInfo(track.Self));
                state.warnedStage = Mathf.Max(state.warnedStage, state.stage);
                return;
            }
            if (state.warnedStage >= state.stage) { return; }
            state.warnedStage = state.stage;
            Messages.Message("DMSE.AirAlert.EngagingPlayer".Translate(node.faction.Name, node.parent.LabelCap, StageLabel(state.stage)),
                new GlobalTargetInfo(track.Self), MessageTypeDefOf.ThreatSmall, historical: false);
        }

        public static string StageLabel(int stage) => ("DMSE.AirAlert.Stage" + Mathf.Clamp(stage, 1, 5)).Translate();

        // ── 生命週期 ─────────────────────────────────────────────────────

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int now = Now;
            if (now % EvaluateIntervalTicks != 0 || !Enabled) { return; }
            Prune(now);
            EvaluateAll(now);
        }

        /// <summary>摧毀據點、移除陣營、事件過期時清理紀錄。</summary>
        private void Prune(int now)
        {
            int keep = Mathf.Max(Tuning.attackWindowTicks, Tuning.salvoGroupTicks * 4) + GenDate.TicksPerDay;
            for (int i = siteList.Count - 1; i >= 0; i--)
            {
                AirDefenseSiteAlert state = siteList[i];
                MapParent parent = ParentOf(state);
                if (parent == null || state.faction == null || parent.Faction == null || parent.Faction.IsPlayer)
                {
                    sites.Remove(state.siteId);
                    siteList.RemoveAt(i);
                    continue;
                }
                state.records.RemoveAll(r => now - r.tick > keep);
                state.waves.RemoveAll(w => now - w.tick > Tuning.attackWindowTicks);
            }
            neutralWarned.RemoveAll(f => f == null);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref siteList, "sites", LookMode.Deep);
            Scribe_Collections.Look(ref salvoGroups, "salvoGroups", LookMode.Deep);
            Scribe_Collections.Look(ref notices, "notices", LookMode.Deep);
            Scribe_Collections.Look(ref neutralWarned, "neutralWarned", LookMode.Reference);
            Scribe_Values.Look(ref nextSalvoId, "nextSalvoId");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                siteList ??= new List<AirDefenseSiteAlert>();
                salvoGroups ??= new List<AirDefenseSalvoGroup>();
                notices ??= new List<AirDefenseNoticeRecord>();
                neutralWarned ??= new List<Faction>();
                siteList.RemoveAll(s => s == null || s.faction == null || s.siteId < 0);
                salvoGroups.RemoveAll(g => g == null);
                sites.Clear();
                foreach (AirDefenseSiteAlert state in siteList) { sites[state.siteId] = state; }
                AirspaceRestrictionUtility.Invalidate();
            }
        }
    }
}
