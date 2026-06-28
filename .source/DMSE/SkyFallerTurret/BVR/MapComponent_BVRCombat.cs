using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DMSE
{
    /// <summary>
    /// 超視距戰鬥系統的中央管理器。負責：
    /// 1. 保存所有被偵測到的目標波次（<see cref="BVRWave"/> / <see cref="BVRTarget"/>）。
    /// 2. 推進每個目標的階段：搜索 → 中過程（火控雷達指派 + 導彈發射裝置攔截）→ 末端（CIWS）。
    /// 3. 維護各類裝置 Comp 的註冊表。
    /// </summary>
    public class MapComponent_BVRCombat : MapComponent
    {
        public MapComponent_BVRCombat(Map map) : base(map) { }

        public List<BVRWave> Waves
        {
            get
            {
                if (waves == null) { waves = new List<BVRWave>(); }
                return waves;
            }
        }

        // ---- 裝置註冊表 ----
        public readonly HashSet<CompSearchRadar> searchRadars = new HashSet<CompSearchRadar>();
        public readonly HashSet<CompFireControlRadar> fireControlRadars = new HashSet<CompFireControlRadar>();
        public readonly HashSet<CompMissileLauncher> launchers = new HashSet<CompMissileLauncher>();
        public readonly HashSet<CompTerminalCIWS> ciwsTurrets = new HashSet<CompTerminalCIWS>();

        /// <summary>地圖上是否存在任何運作中的搜索雷達（決定是否啟用攔截流程）。</summary>
        /// <summary>末端攔截窗口：取防禦方所有 CIWS 中最大的窗口；沒有則為 0。</summary>
        public int TerminalWindowTicks(Faction defender)
        {
            int max = 0;
            foreach (CompTerminalCIWS c in ciwsTurrets)
            {
                if (c.Active && c.parent.Faction == defender && c.Props.terminalWindowTicks > max)
                {
                    max = c.Props.terminalWindowTicks;
                }
            }
            return max;
        }

        /// <summary>
        /// 決定有資格攔截此空投的「防禦方」：擁有運作中搜索雷達、且與空投內容物敵對的陣營。
        /// 玩家優先；找不到回傳 null（不攔截）。
        /// </summary>
        public Faction ResolveDefender(Faction dropFaction)
        {
            if (dropFaction == null) { return null; }
            Faction best = null;
            foreach (CompSearchRadar r in searchRadars)
            {
                if (!r.Active) { continue; }
                Faction f = r.parent.Faction;
                if (f == null || !f.HostileTo(dropFaction)) { continue; }
                if (f.IsPlayer) { return f; }
                if (best == null) { best = f; }
            }
            return best;
        }

        // ====================================================================
        //  目標登記
        // ====================================================================

        /// <summary>
        /// 由 <see cref="Patch_MakeDropPodAt"/> 呼叫：登記一個被偵測到的敵對空投，並依搜索雷達計算預警窗口。
        /// </summary>
        public void RegisterIncoming(Skyfaller faller, IntVec3 pos, Faction defender)
        {
            int window = ComputeWarningWindow(faller, 1, defender);
            if (window < 0)
            {
                return; // 防禦方無運作中的搜索雷達，不攔截。
            }

            int impactTick = Find.TickManager.TicksGame + window;

            BVRTarget target = new BVRTarget(faller, pos, map, nextTargetId++);

            // 短時間內（MergeWindowTicks 容差）抵達的目標合併到同一個攔截窗口（同一波次）。
            BVRWave wave = Waves.Find(w => w.defenderFaction == defender
                && Mathf.Abs(w.tickToImpact - impactTick) <= BVRTuning.MergeWindowTicks);
            if (wave == null)
            {
                wave = new BVRWave(impactTick, new List<BVRTarget> { target });
                wave.defenderFaction = defender;
                Waves.Add(wave);
            }
            else
            {
                wave.targets.Add(target);
                // 合併後窗口取最早抵達者；目標數量越多，整體窗口再略為縮短。
                wave.tickToImpact = Mathf.Min(wave.tickToImpact, impactTick);
                wave.tickToImpact = Mathf.Max(
                    Find.TickManager.TicksGame + 1,
                    wave.tickToImpact - BVRTuning.WindowPenaltyPerExtraTarget);
            }
        }

        /// <summary>
        /// 直接生成一個含 count 枚導彈的單一攔截波次（測試用，繞過世界航跡）。
        /// 需要有運作中的防禦方搜索雷達；成功回傳 true。
        /// </summary>
        public bool RegisterSalvo(ThingDef incomingDef, int count, MissileConfig config, Faction attacker, Faction defender)
        {
            if (incomingDef == null || count <= 0 || defender == null || map == null) { return false; }

            // 以一枚樣本（帶 def 的 BVRTargetProps）計算單枚窗口。
            MissileIncoming first = SkyfallerMaker.MakeSkyfaller(incomingDef) as MissileIncoming;
            if (first == null) { return false; }

            int window = ComputeWarningWindow(first, 1, defender);
            if (window < 0) { return false; } // 防禦方無運作中搜索雷達（first 交由 GC）。

            // 目標數量越多，整體窗口越短（同合併規則）。
            int nowTick = Find.TickManager.TicksGame;
            int impactTick = Mathf.Max(nowTick + 1, nowTick + window - BVRTuning.WindowPenaltyPerExtraTarget * (count - 1));

            BVRWave wave = new BVRWave(impactTick, new List<BVRTarget>());
            wave.defenderFaction = defender;

            for (int i = 0; i < count; i++)
            {
                MissileIncoming mi = i == 0 ? first : SkyfallerMaker.MakeSkyfaller(incomingDef) as MissileIncoming;
                if (mi == null) { continue; }
                mi.config = config != null ? config.Clone() : null;
                mi.attacker = attacker;
                mi.bvrHandled = true; // 重投時不再重複登記。
                IntVec3 pos = DropCellFinder.RandomDropSpot(map);
                wave.targets.Add(new BVRTarget(mi, pos, map, nextTargetId++));
            }

            Waves.Add(wave);
            return true;
        }

        /// <summary>
        /// 計算預警/攔截窗口（ticks）。窗口 ∝ Σ(搜索距離 × 功率)，再依目標綜合距離等級放大、目標數量縮短，
        /// 並在反隱身不足時打折。回傳 -1 表示沒有運作中的搜索雷達。
        /// </summary>
        public int ComputeWarningWindow(Skyfaller faller, int targetCount, Faction defender)
        {
            List<CompSearchRadar> active = searchRadars.Where(r => r.Active && r.parent.Faction == defender).ToList();
            if (active.Count == 0)
            {
                return -1;
            }

            BVRTargetProps tp = (faller != null && faller.def != null
                ? faller.def.GetModExtension<BVRTargetProps>() : null) ?? BVRTargetProps.Default;

            float window = 0f;
            foreach (CompSearchRadar radar in active)
            {
                // 功率對窗口的影響改為超線性（指數放大）：高功率雷達顯著拉長預警/攔截窗口。
                float powerFactor = Mathf.Pow(Mathf.Max(1, radar.Props.powerLevel), BVRTuning.PowerWindowExponent);
                float eff = radar.Props.searchDistance * radar.Props.ticksPerDistance * powerFactor;

                // 反隱身強度：高出目標隱身越多，窗口加成越大；不足則大幅縮短（僅能局部偵測）。
                int antiGap = radar.Props.antiStealthLevel - tp.stealthLevel;
                if (antiGap < 0)
                {
                    eff *= BVRTuning.StealthDetectFactor;
                }
                else
                {
                    eff *= 1f + BVRTuning.AntiStealthBonusPerLevel * antiGap;
                }
                window += eff;
            }

            window *= Mathf.Max(0.01f, tp.distanceLevel);
            window /= Mathf.Max(1, targetCount);
            return Mathf.RoundToInt(window);
        }

        // ====================================================================
        //  目標查詢 / 移除（供攔截彈回呼）
        // ====================================================================

        public BVRTarget GetTarget(int id)
        {
            for (int i = 0; i < Waves.Count; i++)
            {
                BVRTarget t = Waves[i].targets.Find(x => x.id == id);
                if (t != null) { return t; }
            }
            return null;
        }

        /// <summary>乾淨移除一個目標（中過程攔截成功用，不生成殘骸）。</summary>
        public void RemoveTarget(int id)
        {
            for (int i = Waves.Count - 1; i >= 0; i--)
            {
                BVRTarget t = Waves[i].targets.Find(x => x.id == id);
                if (t != null)
                {
                    t.Discard();
                    Waves[i].targets.Remove(t);
                    if (Waves[i].targets.Count == 0) { Waves.RemoveAt(i); }
                    return;
                }
            }
        }

        // ====================================================================
        //  每 tick 推進
        // ====================================================================

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (Waves.Count == 0) { return; }

            int now = Find.TickManager.TicksGame;

            for (int i = Waves.Count - 1; i >= 0; i--)
            {
                BVRWave wave = Waves[i];
                int timeLeft = wave.tickToImpact - now;

                if (timeLeft <= 0)
                {
                    ResolveWave(wave);
                    Waves.RemoveAt(i);
                    continue;
                }

                int terminalWindow = TerminalWindowTicks(wave.defenderFaction);
                if (timeLeft <= terminalWindow)
                {
                    TerminalDefense(wave, now);
                }
                else
                {
                    MidcourseDefense(wave, timeLeft, terminalWindow, now);
                }

                if (wave.targets.Count == 0)
                {
                    Waves.RemoveAt(i);
                }
            }
        }

        // ---- 階段二：火控雷達 + 導彈發射裝置 ----
        private void MidcourseDefense(BVRWave wave, int timeLeft, int terminalWindow, int now)
        {
            Faction defender = wave.defenderFaction;

            // 火力通道：防禦方所有運作中火控雷達的最大目標數總和（共用池）。
            int capacity = 0;
            foreach (CompFireControlRadar fc in fireControlRadars)
            {
                if (fc.Active && fc.parent.Faction == defender && timeLeft <= fc.Props.maxRangeTicks)
                {
                    capacity += fc.Props.maxTargets;
                }
            }
            if (capacity <= 0) { return; }

            int engaged = CountEngaged();

            foreach (BVRTarget target in wave.targets)
            {
                if (target.midcourseEngagedUntil > now) { continue; } // 已有攔截彈在飛。
                if (target.lockUntil > now) { continue; }             // 鎖定中（已占用通道）。

                if (target.lockUntil >= 0)
                {
                    // 鎖定完成 → 嘗試發射（已占用通道，不受 capacity 限制）。
                    CompFireControlRadar fcReady = SelectBestRadar(target, timeLeft, defender);
                    CompMissileLauncher launcher = SelectReadyLauncher(now, defender);
                    if (fcReady != null && launcher != null)
                    {
                        // 基礎命中率
                        float hitChance = fcReady.ComputeHitChance(target, timeLeft - terminalWindow);

                        // 讀取攔截彈的戰鬥部加成（ContinuousRod / Airburst 等）
                        MissileConfig interceptorConfig = launcher.GetLoadedMissileConfig();
                        float N = interceptorConfig?.PayloadCapacity ?? 0f;
                        MissilePartDef wh = interceptorConfig?.PartFor(MissilePartCategory.Warhead);
                        if (wh?.warheadEffect != null)
                        {
                            hitChance = Mathf.Clamp01(hitChance + wh.warheadEffect.InterceptBonus(N));
                        }

                        launcher.FireInterceptor(target, hitChance, now);
                        target.midcourseEngagedUntil = now + launcher.Props.interceptorTravelTicks;
                        target.lockUntil = -1;

                        // 空爆：額外對同波次其他目標進行獨立攔截擲骰
                        if (wh?.warheadEffect != null)
                        {
                            int extraRolls = wh.warheadEffect.ExtraInterceptRolls(N);
                            if (extraRolls > 0)
                            {
                                ApplyExtraInterceptRolls(wave, target, hitChance, extraRolls);
                            }
                        }
                    }
                    continue; // 此通道已被占用（無論是否成功發射）。
                }

                // 未鎖定的新目標：受火力通道容量限制才開始鎖定。
                if (engaged >= capacity) { continue; }
                CompFireControlRadar fc = SelectBestRadar(target, timeLeft, defender);
                if (fc == null) { continue; }
                target.lockUntil = now + fc.LockOnTicksFor(target);
                engaged++;
            }
        }

        // 火力通道在「鎖定階段」（lockUntil >= 0）與「攔截彈在飛」（midcourseEngagedUntil > now）期間均占用；
        // 直到攔截彈命中或落空結算後才釋放通道，供雷達重新鎖定下一目標。
        public int CountEngaged()
        {
            int now = Find.TickManager.TicksGame;
            int n = 0;
            foreach (BVRWave w in Waves)
            {
                foreach (BVRTarget t in w.targets)
                {
                    if (t.lockUntil >= 0 || t.midcourseEngagedUntil > now) { n++; }
                }
            }
            return n;
        }

        private CompFireControlRadar SelectBestRadar(BVRTarget target, int timeLeft, Faction defender)
        {
            CompFireControlRadar best = null;
            float bestChance = -1f;
            foreach (CompFireControlRadar fc in fireControlRadars)
            {
                if (!fc.Active || fc.parent.Faction != defender || timeLeft > fc.Props.maxRangeTicks) { continue; }
                float c = fc.ComputeHitChance(target, timeLeft);
                if (c > bestChance) { bestChance = c; best = fc; }
            }
            return best;
        }

        private CompMissileLauncher SelectReadyLauncher(int now, Faction defender)
        {
            CompMissileLauncher best = null;
            foreach (CompMissileLauncher l in launchers)
            {
                if (l.parent.Faction == defender && l.ReadyToFire(now))
                {
                    // 偏好冷卻最早結束（最閒置）的發射裝置。
                    if (best == null || l.cooldownUntil < best.cooldownUntil) { best = l; }
                }
            }
            return best;
        }

        // ---- 空爆額外攔截擲骰 ----
        /// <summary>
        /// 空爆戰鬥部的額外攔截：對同一波次中其他未被攔截的目標，
        /// 以相同命中率進行獨立擲骰（不消耗彈藥/冷卻，純概率結算）。
        /// </summary>
        private void ApplyExtraInterceptRolls(BVRWave wave, BVRTarget source, float hitChance, int rolls)
        {
            int rollsLeft = rolls;
            foreach (BVRTarget other in wave.targets)
            {
                if (rollsLeft <= 0) { break; }
                if (other == source || other.midcourseEngagedUntil > Find.TickManager.TicksGame) { continue; }
                rollsLeft--;
                if (Rand.Chance(hitChance))
                {
                    RemoveTarget(other.id);
                    if (Prefs.DevMode)
                    {
                        Log.Message($"[DMSE BVR] 空爆額外攔截命中目標 #{other.id}");
                    }
                }
            }
        }

        // ---- 階段三：末端 CIWS ----
        private void TerminalDefense(BVRWave wave, int now)
        {
            foreach (CompTerminalCIWS ciws in ciwsTurrets)
            {
                if (wave.targets.Count == 0) { break; }
                if (ciws.parent.Faction != wave.defenderFaction) { continue; }
                ciws.TryEngage(wave, now);
            }
        }

        // ---- 落地結算 ----
        private void ResolveWave(BVRWave wave)
        {
            foreach (BVRTarget t in wave.targets)
            {
                t.Resolve(wave);
            }
            if (wave.signal != null)
            {
                foreach (string s in wave.signal)
                {
                    Find.SignalManager.SendSignal(new Signal(s, false));
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref waves, "waves", LookMode.Deep);
            Scribe_Values.Look(ref nextTargetId, "nextTargetId", 0);
        }

        private List<BVRWave> waves = new List<BVRWave>();
        private int nextTargetId;
    }

    /// <summary>系統的可調平衡常數（集中於此方便調整）。</summary>
    public static class BVRTuning
    {
        /// <summary>搜索雷達每「搜索距離」單位換算的窗口 ticks（被 Props.ticksPerDistance 覆寫，這裡是後備）。</summary>
        public const int WindowPenaltyPerExtraTarget = 60;

        /// <summary>反隱身不足時，搜索窗口的折扣係數。</summary>
        public const float StealthDetectFactor = 0.4f;

        /// <summary>搜索雷達功率對預警窗口的指數（>1 即超線性，越高功率影響越顯著）。</summary>
        public const float PowerWindowExponent = 2f;

        /// <summary>反隱身等級每高出目標隱身 1 級，預警窗口的加成比例。</summary>
        public const float AntiStealthBonusPerLevel = 0.5f;

        /// <summary>預測抵達時間相差在此 ticks 內的目標，合併到同一個攔截窗口（同一波次）。</summary>
        public const int MergeWindowTicks = 600;
    }
}
