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
        public bool AnyActiveSearchRadar => searchRadars.Any(r => r.Active);

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

            BVRWave wave = Waves.Find(w => w.tickToImpact == impactTick && w.defenderFaction == defender);
            if (wave == null)
            {
                wave = new BVRWave(impactTick, new List<BVRTarget> { target });
                wave.defenderFaction = defender;
                Waves.Add(wave);
            }
            else
            {
                wave.targets.Add(target);
                // 目標數量越多，整體窗口略為縮短（對應設計圖「目標數量 → 預警時間」）。
                wave.tickToImpact = Mathf.Max(
                    Find.TickManager.TicksGame + 1,
                    wave.tickToImpact - BVRTuning.WindowPenaltyPerExtraTarget);
            }
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
                float eff = radar.Props.searchDistance
                            * radar.Props.ticksPerDistance
                            * Mathf.Max(1, radar.Props.powerLevel);
                if (radar.Props.antiStealthLevel < tp.stealthLevel)
                {
                    eff *= BVRTuning.StealthDetectFactor; // 反隱身不足，僅能局部偵測，窗口縮短。
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

            int engaged = CountEngaged(now);

            foreach (BVRTarget target in wave.targets)
            {
                if (engaged >= capacity) { break; }
                if (target.midcourseEngagedUntil > now) { continue; } // 已有攔截彈在飛。

                CompFireControlRadar fc = SelectBestRadar(target, timeLeft, defender);
                if (fc == null) { continue; }

                CompMissileLauncher launcher = SelectReadyLauncher(now, defender);
                if (launcher == null) { break; } // 沒有可用的發射裝置。

                float hitChance = fc.ComputeHitChance(target, timeLeft - terminalWindow);
                launcher.FireInterceptor(target, hitChance, now);
                target.midcourseEngagedUntil = now + launcher.Props.interceptorTravelTicks;
                engaged++;
            }
        }

        private int CountEngaged(int now)
        {
            int n = 0;
            foreach (BVRWave w in Waves)
            {
                foreach (BVRTarget t in w.targets)
                {
                    if (t.midcourseEngagedUntil > now) { n++; }
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
    }
}
