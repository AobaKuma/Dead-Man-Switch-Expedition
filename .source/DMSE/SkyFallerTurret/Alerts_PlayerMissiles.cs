using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 玩家作為攻擊方、導彈正在被敵方 BVR 系統追蹤時顯示。<br/>
    /// ‧ Hover 展開 Tooltip：每枚導彈的類型、落點座標、攔截狀態與預計衝擊倒計時。<br/>
    /// ‧ 左鍵循環：依次把相機跳到各枚導彈的落點（支援跨地圖切換）。<br/>
    /// ‧ 右鍵反向循環。
    /// </summary>
    public class Alerts_PlayerMissiles : Alert
    {
        // 相機循環索引（跨幀持久）。
        private int jumpIndex = -1;

        // ── 資料蒐集 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 蒐集所有由玩家發射、目前正被敵方 BVR 系統追蹤的 <see cref="BVRTarget"/>。
        /// 依波次衝擊時間升序排列。
        /// </summary>
        private static List<(BVRWave wave, BVRTarget target, Map map)> GetPlayerTargets()
        {
            var result = new List<(BVRWave wave, BVRTarget target, Map map)>();
            foreach (Map map in Find.Maps)
            {
                MapComponent_BVRCombat comp = map.GetComponent<MapComponent_BVRCombat>();
                if (comp == null) continue;

                foreach (BVRWave wave in comp.Waves)
                {
                    // 防禦方不是玩家 → 玩家正在攻擊此地圖
                    if (wave.defenderFaction == null || wave.defenderFaction.IsPlayer) continue;

                    foreach (BVRTarget target in wave.targets)
                    {
                        // 確認是玩家發射的導彈（IIncomingMissile）
                        if (target.Missile is IIncomingMissile mi && mi.Attacker?.IsPlayer == true)
                            result.Add((wave, target, map));
                    }
                }
            }
            result.Sort((a, b) => a.wave.tickToImpact.CompareTo(b.wave.tickToImpact));
            return result;
        }

        // ── Alert 介面 ────────────────────────────────────────────────────────

        private static List<MissileWorldObject> WorldTargets() => WorldComponent_BVRNetwork.Current?.Tracks
            .Where(t => t.Attacker == Faction.OfPlayer).OrderBy(t => t.TicksToArrival).ToList()
            ?? new List<MissileWorldObject>();

        public override AlertReport GetReport()
        {
            if (WorldTargets().Count > 0) return AlertReport.Active;
            // 短路版本：找到第一個匹配即返回，避免建立完整列表。
            foreach (Map map in Find.Maps)
            {
                MapComponent_BVRCombat comp = map.GetComponent<MapComponent_BVRCombat>();
                if (comp == null) continue;
                foreach (BVRWave wave in comp.Waves)
                {
                    if (wave.defenderFaction == null || wave.defenderFaction.IsPlayer) continue;
                    foreach (BVRTarget target in wave.targets)
                    {
                        if (target.Missile is IIncomingMissile mi && mi.Attacker?.IsPlayer == true)
                            return AlertReport.Active;
                    }
                }
            }
            return AlertReport.Inactive;
        }

        /// <summary>一個打擊波次的摘要：枚數、ETA 區間、被偵測與被接戰的枚數。</summary>
        public static string StrikeWaveLine(List<ScorerProjectile_WorldObject> tracks)
        {
            ScorerProjectile_WorldObject first = tracks[0];
            int detected = tracks.Count(t => t.State.detectedByFaction != null);
            int engaged = tracks.Count(t => t.State.lockUntil >= 0 || t.State.interceptor != null);
            return "DMSE.Strike.Alert.WaveLine".Translate(WorldComponent_StrikeReports.MissionLabel(first.strikeMissionId), first.strikeWave + 1, tracks.Count,
                tracks.Min(t => t.TicksToArrival).ToStringTicksToPeriod(), tracks.Max(t => t.TicksToArrival).ToStringTicksToPeriod(),
                detected, engaged);
        }

        public override string GetLabel()
        {
            var targets = GetPlayerTargets();
            var world = WorldTargets();
            if (targets.Count == 0 && world.Count == 0) return base.GetLabel();
            if (world.Count > 0)
            {
                int eta = world[0].TicksToArrival;
                if (targets.Count > 0) eta = System.Math.Min(eta, targets.Min(t => t.wave.tickToImpact) - Find.TickManager.TicksGame);
                return "DMSE.Alert.PlayerMissiles.Label".Translate(targets.Count + world.Count, eta.ToStringTicksToPeriod());
            }

            int now = Find.TickManager.TicksGame;
            int earliestTick = targets.Min(t => t.wave.tickToImpact);
            int timeLeft = earliestTick - now;

            return "DMSE.Alert.PlayerMissiles.Label".Translate(
                targets.Count,
                timeLeft.ToStringTicksToPeriod(allowSeconds: false));
        }

        public override TaggedString GetExplanation()
        {
            var targets = GetPlayerTargets();
            var world = WorldTargets();
            if (targets.Count == 0 && world.Count == 0) return base.GetExplanation();

            int now = Find.TickManager.TicksGame;
            var sb = new StringBuilder();
            // 波次打擊的導彈依任務／波次彙整成一行，其餘航跡逐一列出
            foreach (var group in world.OfType<ScorerProjectile_WorldObject>().Where(t => t.strikeMissionId >= 0)
                .GroupBy(t => (t.strikeMissionId, t.strikeWave)).OrderBy(g => g.Min(t => t.TicksToArrival)))
            {
                sb.AppendLine(StrikeWaveLine(group.ToList()));
            }
            foreach (var track in world.Where(t => !(t is ScorerProjectile_WorldObject s && s.strikeMissionId >= 0)))
                sb.AppendLine(track.Label + "\n" + track.GetInspectString() + "\n");

            // ── 按目標地圖分組 ──
            var byMap = new Dictionary<Map, List<(BVRWave wave, BVRTarget target)>>();
            foreach (var (wave, target, map) in targets)
            {
                if (!byMap.TryGetValue(map, out var list))
                    byMap[map] = list = new List<(BVRWave wave, BVRTarget target)>();
                list.Add((wave, target));
            }

            foreach (var kv in byMap)
            {
                Map map = kv.Key;
                string mapLabel = map.Parent?.LabelCap ?? map.ToString();
                sb.AppendLine("DMSE.Alert.PlayerMissiles.MapHeader".Translate(mapLabel));

                // ── 按波次分組，並依衝擊時間升序 ──
                var byWave = new Dictionary<BVRWave, List<BVRTarget>>();
                foreach (var (wave, target) in kv.Value)
                {
                    if (!byWave.TryGetValue(wave, out var wl))
                        byWave[wave] = wl = new List<BVRTarget>();
                    wl.Add(target);
                }

                foreach (var waveKv in byWave.OrderBy(w => w.Key.tickToImpact))
                {
                    BVRWave wave = waveKv.Key;
                    int timeLeft = wave.tickToImpact - now;
                    sb.AppendLine("DMSE.Alert.PlayerMissiles.WaveImpact".Translate(
                        timeLeft.ToStringTicksToPeriod(allowSeconds: false)));

                    foreach (BVRTarget target in waveKv.Value)
                    {
                        // 攔截狀態
                        string statusKey;
                        if (target.midcourseEngagedUntil > now)
                            statusKey = "DMSE.Alert.Pods.Status.InFlight";
                        else if (target.lockUntil > now)
                            statusKey = "DMSE.Alert.Pods.Status.Locking";
                        else
                            statusKey = "DMSE.Alert.Pods.Status.Unengaged";

                        // 導彈名稱
                        string missileLabel = target.Payload?.Label
                            ?? target.Payload?.def?.label
                            ?? "?";

                        sb.AppendLine("DMSE.Alert.PlayerMissiles.Target".Translate(
                            missileLabel,
                            target.position.x,
                            target.position.z,
                            statusKey.Translate()));
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("DMSE.Alert.PlayerMissiles.ClickHint".Translate());
            return sb.ToString().TrimEnd();
        }

        protected override void OnClick()
        {
            var targets = GetPlayerTargets();
            var world = WorldTargets();
            int count = targets.Count + world.Count;
            if (count == 0) return;
            jumpIndex = GenMath.PositiveMod(jumpIndex + (Event.current.button == 1 ? -1 : 1), count);
            if (jumpIndex < world.Count)
                CameraJumper.TryJump(new GlobalTargetInfo(world[jumpIndex]));
            else
            {
                var (_, target, map) = targets[jumpIndex - world.Count];
                CameraJumper.TryJump(target.position, map);
            }
        }
    }
}
