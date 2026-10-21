using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>候選導彈不能立即發射或不能用的原因（docs/BVR_波次打擊規劃.md §5-3）。</summary>
    public enum StrikeRoundIssue
    {
        None,
        /// <summary>平台斷電／關閉／損壞／無人操作：可排進波次，發射時再判定。</summary>
        Inactive,
        /// <summary>平台冷卻中：可排進波次，發射時再判定。</summary>
        Cooldown,
        /// <summary>超出射程：不能加入波次。</summary>
        OutOfRange,
        /// <summary>跨圖層而無導引能力：不能加入波次。</summary>
        CrossLayerNoGuidance,
    }

    /// <summary>規劃視窗的一枚候選導彈（不存檔）。</summary>
    public class StrikeCandidate
    {
        public CompMissileLauncher_WorldTargeting launcher;
        public Thing round;
        public MissileConfig config;
        public float distance;
        public int flightTicks;
        public StrikeRoundIssue issue;
        public string issueText;
        public string groupKey;
        public string groupLabel;
        public float value;

        /// <summary>可加入波次（射程與導引能力沒問題）。</summary>
        public bool Assignable => issue == StrikeRoundIssue.None || issue == StrikeRoundIssue.Inactive
            || issue == StrikeRoundIssue.Cooldown;
    }

    /// <summary>
    /// 人工版彈道計算（docs/BVR_波次打擊規劃.md §8-2）：總工作量 = (基礎 + 每枚 × 枚數) × 跨圖層倍率。
    /// 先做基礎工作量，之後依波次順序每累積「每枚」的量就解出一枚，所以第 1 波可以先發射；
    /// 調整波次加入的導彈只增加剩餘工作量（增量計算）。
    /// </summary>
    public static class StrikeComputation
    {
        public static float TotalWork(float workBase, float workPerRound, float factor, int rounds)
        {
            return Mathf.Max(0f, (workBase + workPerRound * rounds) * Mathf.Max(0.01f, factor));
        }

        /// <summary>可做研究工作的殖民者（未倒地、研究工作已啟用）。</summary>
        public static IEnumerable<Pawn> Researchers(Map map)
        {
            if (map == null) { yield break; }
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (!p.Downed && p.workSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Research)
                    && p.workSettings.WorkIsActive(WorkTypeDefOf.Research))
                {
                    yield return p;
                }
            }
        }

        /// <summary>本地圖最快研究者的研究速度；沒有研究者時回傳 0。</summary>
        public static float FastestResearchSpeed(Map map)
        {
            float best = 0f;
            foreach (Pawn p in Researchers(map)) { best = Mathf.Max(best, p.GetStatValue(StatDefOf.ResearchSpeed)); }
            return best;
        }

        /// <summary>
        /// 投入 <paramref name="amount"/> 工作量：先填基礎，剩下的累積成 credit，每滿一枚的量就解出一枚。
        /// 回傳這次解出的枚數（不超過 <paramref name="unsolved"/>）。
        /// </summary>
        public static int Advance(ref float baseLeft, ref float credit, float amount, float perRound, int unsolved)
        {
            amount = Mathf.Max(0f, amount);
            float toBase = Mathf.Min(baseLeft, amount);
            baseLeft -= toBase;
            credit += amount - toBase;
            if (baseLeft > 0.001f) { return 0; }
            baseLeft = 0f;
            int solved = 0;
            while (solved < unsolved && (perRound <= 0f || credit >= perRound - 0.001f))
            {
                credit = Mathf.Max(0f, credit - Mathf.Max(0f, perRound));
                solved++;
            }
            return solved;
        }

        /// <summary>還需要的工作量。</summary>
        public static float Remaining(float baseLeft, float credit, float perRound, int unsolved)
        {
            return Mathf.Max(0f, baseLeft) + Mathf.Max(0f, perRound * unsolved - credit);
        }
    }

    /// <summary>波次打擊的候選列舉、設定指紋、飛行時間與自動分波。</summary>
    public static class StrikeRoundUtility
    {
        /// <summary>
        /// 本地圖玩家世界選靶平台上所有可進攻的在架彈藥。被其他擁有者保留的不列入，
        /// 數量由 <paramref name="reservedElsewhere"/> 回傳；<paramref name="owner"/> 自己保留的照常列出。
        /// </summary>
        public static List<StrikeCandidate> Candidates(Map map, PlanetTile target, int owner, out int reservedElsewhere)
        {
            reservedElsewhere = 0;
            var result = new List<StrikeCandidate>();
            MapComponent_StrikePlanner planner = map?.GetComponent<MapComponent_StrikePlanner>();
            if (planner == null) { return result; }
            PlanetTile from = map.Tile;
            int now = Find.TickManager.TicksGame;
            foreach (CompMissileLauncher_WorldTargeting launcher in planner.launchers
                .Where(l => l.parent.Faction == Faction.OfPlayer).OrderBy(l => l.parent.thingIDNumber))
            {
                foreach (Thing round in launcher.StrikeRounds)
                {
                    int holder = planner.OwnerOf(round);
                    if (holder >= 0 && holder != owner)
                    {
                        reservedElsewhere++;
                        continue;
                    }
                    result.Add(Make(launcher, round, from, target, now));
                }
            }
            return result;
        }

        public static StrikeCandidate Make(CompMissileLauncher_WorldTargeting launcher, Thing round, PlanetTile from, PlanetTile target, int now)
        {
            MissileConfig config = launcher.BuildFiringConfig(round);
            var c = new StrikeCandidate
            {
                launcher = launcher,
                round = round,
                config = config,
                distance = MissileWorldUtility.RangeDistance(from, target),
                flightTicks = launcher.DepartureTicksFor(config, target) + FlightTicks(config, from, target),
                groupKey = GroupKey(config, round),
                groupLabel = GroupLabel(config, round),
                value = RoundValue(round, config),
            };
            if (!MissileWorldUtility.CanEngage(config, from, target, 0f, out string reason))
            {
                c.issue = !MissileWorldUtility.SameLayer(from, target) && (config == null || !config.HasTerminalGuidance)
                    ? StrikeRoundIssue.CrossLayerNoGuidance : StrikeRoundIssue.OutOfRange;
                c.issueText = reason;
            }
            else if (!launcher.Active)
            {
                c.issue = StrikeRoundIssue.Inactive;
                c.issueText = "DMSE.BVR.DeviceInactive".Translate();
            }
            else if (launcher.OnCooldown(now))
            {
                c.issue = StrikeRoundIssue.Cooldown;
                c.issueText = "DMSE.MissileLauncher.LaunchCooldown".Translate(launcher.CooldownTicksRemaining(now).ToStringTicksToPeriod());
            }
            return c;
        }

        /// <summary>發射設定指紋（跨存讀檔穩定）：彈體與各部件 defName。</summary>
        public static int ConfigHash(MissileConfig config)
        {
            if (config == null) { return 0; }
            IEnumerable<string> parts = config.parts.Where(p => p != null).Select(p => p.defName).OrderBy(n => n);
            return GenText.StableStringHash((config.body?.defName ?? "none") + "|" + string.Join("|", parts));
        }

        /// <summary>同一地圖出發、以導彈世界速度飛到目標的 tick 數（與世界航跡的運動模型一致）。</summary>
        public static int FlightTicks(MissileConfig config, PlanetTile from, PlanetTile to)
        {
            if (!from.Valid || !to.Valid) { return 0; }
            float angle = GenMath.SphericalDistance(MissileWorldUtility.TileCenter(from).normalized,
                MissileWorldUtility.TileCenter(to).normalized);
            return Mathf.CeilToInt(angle / Mathf.Max(1e-9f, MissileWorldMotion.AngularSpeed(config)));
        }

        public static string GroupKey(MissileConfig config, Thing round)
        {
            if (config == null) { return round?.def?.defName ?? "none"; }
            return (config.body?.defName ?? "none") + "|" + string.Join("|",
                config.parts.Where(p => p != null).Select(p => p.defName).OrderBy(n => n));
        }

        /// <summary>例：「輕型導彈 · 慣性導引 · 高爆彈頭」。</summary>
        public static string GroupLabel(MissileConfig config, Thing round)
        {
            if (config?.body == null) { return round?.LabelCap ?? "?"; }
            var bits = new List<string> { config.body.LabelCap, config.TargetingLabel };
            foreach (MissilePartCategory cat in new[] { MissilePartCategory.Warhead, MissilePartCategory.Payload, MissilePartCategory.Propulsion })
            {
                MissilePartDef part = config.PartFor(cat);
                if (part != null) { bits.Add(part.LabelCap); }
            }
            return string.Join(" · ", bits);
        }

        /// <summary>單枚價值：導彈物品取市價；彈道井等平台彈藥取部件成本。</summary>
        public static float RoundValue(Thing round, MissileConfig config)
        {
            if (round != null && round.def.category == ThingCategory.Item) { return round.MarketValue; }
            float value = 0f;
            if (config != null)
            {
                foreach (MissilePartDef part in config.parts)
                {
                    if (part?.costList == null) { continue; }
                    foreach (ThingDefCountClass cost in part.costList) { value += cost.count * cost.thingDef.BaseMarketValue; }
                }
            }
            return value;
        }

        /// <summary>
        /// 自動分波（§10-2）：候選已依價值由低到高排序，依序填滿各波，最高價值的落在最後一波。
        /// 回傳每波的候選索引；超出 <paramref name="maxWaves"/> × <paramref name="cap"/> 的不分配。
        /// </summary>
        public static List<List<int>> AutoAssign(int count, int cap, int maxWaves)
        {
            var waves = new List<List<int>>();
            if (cap <= 0 || maxWaves <= 0) { return waves; }
            for (int i = 0; i < count; i++)
            {
                if (i % cap == 0)
                {
                    if (waves.Count >= maxWaves) { break; }
                    waves.Add(new List<int>());
                }
                waves[waves.Count - 1].Add(i);
            }
            return waves;
        }
    }
}
