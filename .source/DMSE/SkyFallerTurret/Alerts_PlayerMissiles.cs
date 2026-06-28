using RimWorld;
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
                        // 確認是玩家發射的 MissileIncoming
                        if (target.Skyfaller is MissileIncoming mi && mi.attacker?.IsPlayer == true)
                            result.Add((wave, target, map));
                    }
                }
            }
            result.Sort((a, b) => a.wave.tickToImpact.CompareTo(b.wave.tickToImpact));
            return result;
        }

        // ── Alert 介面 ────────────────────────────────────────────────────────

        public override AlertReport GetReport()
        {
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
                        if (target.Skyfaller is MissileIncoming mi && mi.attacker?.IsPlayer == true)
                            return AlertReport.Active;
                    }
                }
            }
            return AlertReport.Inactive;
        }

        public override string GetLabel()
        {
            var targets = GetPlayerTargets();
            if (targets.Count == 0) return base.GetLabel();

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
            if (targets.Count == 0) return base.GetExplanation();

            int now = Find.TickManager.TicksGame;
            var sb = new StringBuilder();

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
                        string missileLabel = target.Skyfaller?.Label
                            ?? target.Skyfaller?.def?.label
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
            if (targets.Count == 0) return;

            // 左鍵正向、右鍵反向循環
            if (Event.current.button == 1)
                jumpIndex = (jumpIndex - 1 + targets.Count) % targets.Count;
            else
                jumpIndex = (jumpIndex + 1) % targets.Count;

            jumpIndex = GenMath.PositiveMod(jumpIndex, targets.Count);

            var (_, target, map) = targets[jumpIndex];
            CameraJumper.TryJump(target.position, map);
        }
    }
}
