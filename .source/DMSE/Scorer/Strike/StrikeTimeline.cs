using System.Collections.Generic;
using System.Linq;

namespace DMSE
{
    /// <summary>時間軸模擬用的一枚導彈（docs/BVR_波次打擊規劃.md §2-3、§10-2）。</summary>
    public struct StrikePlanSlot
    {
        /// <summary>發射平台（同一平台的各枚依漣波間隔接續，並共用冷卻）。</summary>
        public object launcher;
        public int flightTicks;
        public int salvoInterval;
        public int launchCooldown;
        /// <summary>平台冷卻結束的 tick。</summary>
        public int readyTick;
        /// <summary>已發射（或已排入佇列）的導彈：不再排程，只用其抵達時間。</summary>
        public bool launched;
        /// <summary>已發射導彈的預計抵達 tick；-1 表示已結束（被攔截、已抵達）而不計入。</summary>
        public int arrivalTick;
    }

    /// <summary>模擬出的一波：最快可發射的 tick、抵達區間、是否會比前面的波次早到。</summary>
    public class StrikePlanWave
    {
        public int fireTick = -1;
        public int arriveMin = -1;
        public int arriveMax = -1;
        /// <summary>本波最早抵達會比這一波（索引）的最晚抵達還早；-1 表示順序正常。</summary>
        public int earlierThan = -1;
        /// <summary>本波待發射枚數超過導引通道容量，永遠無法整波發射。</summary>
        public bool overCapacity;
        /// <summary>讓本波排在前面各波之後抵達，需要再延後的 tick 數。</summary>
        public int suggestedDelay;
    }

    /// <summary>
    /// 波次時間軸：各波依序在「導引通道夠用、平台冷卻結束」的最早時刻發射，同平台漣波接續，
    /// 每枚升空後占用導引通道到導引期結束；算出抵達區間並標出比前面波次早到的波次。
    /// 與實際發射規則相同：按下發射即訂通道，第一枚在下一個 tick 升空。
    /// </summary>
    public static class StrikeTimeline
    {
        public static List<StrikePlanWave> Simulate(List<List<StrikePlanSlot>> waves, int capacity, int guidanceTicks,
            int spacingTicks, int now, IEnumerable<int> holdReleaseTicks)
        {
            var holds = holdReleaseTicks.Where(e => e > now).Select(e => (start: int.MinValue, end: e)).ToList();
            var ready = new Dictionary<object, int>();
            foreach (StrikePlanSlot slot in waves.SelectMany(w => w))
            {
                if (slot.launcher == null) { continue; }
                ready[slot.launcher] = ready.TryGetValue(slot.launcher, out int r) ? System.Math.Max(r, slot.readyTick) : slot.readyTick;
            }
            var result = new List<StrikePlanWave>();
            int floor = now;
            int latestSoFar = -1, latestWave = -1;
            for (int k = 0; k < waves.Count; k++)
            {
                var plan = new StrikePlanWave();
                result.Add(plan);
                var arrivals = new List<int>();
                foreach (StrikePlanSlot slot in waves[k].Where(s => s.launched && s.arrivalTick >= 0)) { arrivals.Add(slot.arrivalTick); }
                List<StrikePlanSlot> pending = waves[k].Where(s => !s.launched).ToList();
                if (pending.Count > capacity) { plan.overCapacity = true; }
                else if (pending.Count > 0)
                {
                    var launchers = pending.Select(s => s.launcher).Distinct().ToList();
                    var times = new SortedSet<int> { floor };
                    foreach (var h in holds) { if (h.end > floor) { times.Add(h.end); } }
                    foreach (object l in launchers) { if (l != null && ready.TryGetValue(l, out int r) && r > floor) { times.Add(r); } }
                    int fire = times.Max;
                    foreach (int t in times)
                    {
                        int free = capacity - holds.Count(h => h.start <= t && t < h.end);
                        if (free >= pending.Count && launchers.All(l => l == null || !ready.TryGetValue(l, out int r) || r <= t))
                        {
                            fire = t;
                            break;
                        }
                    }
                    plan.fireTick = fire;
                    foreach (IGrouping<object, StrikePlanSlot> group in pending.GroupBy(s => s.launcher))
                    {
                        int j = 0, last = fire;
                        foreach (StrikePlanSlot slot in group)
                        {
                            int launch = fire + 1 + j * slot.salvoInterval;
                            arrivals.Add(launch + slot.flightTicks);
                            holds.Add((fire, launch + guidanceTicks));
                            last = launch;
                            j++;
                        }
                        if (group.Key != null) { ready[group.Key] = last + group.First().launchCooldown; }
                    }
                    floor = fire;
                }
                if (arrivals.Count > 0)
                {
                    plan.arriveMin = arrivals.Min();
                    plan.arriveMax = arrivals.Max();
                    if (latestWave >= 0 && plan.arriveMin < latestSoFar)
                    {
                        plan.earlierThan = latestWave;
                        plan.suggestedDelay = latestSoFar + spacingTicks - plan.arriveMin;
                    }
                    if (plan.arriveMax > latestSoFar)
                    {
                        latestSoFar = plan.arriveMax;
                        latestWave = k;
                    }
                }
            }
            return result;
        }
    }
}
