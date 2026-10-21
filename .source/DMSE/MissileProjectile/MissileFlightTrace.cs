using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈飛行的逐 tick 追蹤（docs/Missile_Skyfaller轉Projectile_計畫.md §8.6）。
    /// 只在驗收測試開啟 <see cref="Active"/> 時記錄；用來比對舊 Skyfaller 與新 Projectile 的軌跡、姿態與尾焰參數是否逐 tick 相同。
    /// 一般遊戲中 <see cref="Active"/> 為 false，各記錄點只多一次布林判斷。
    /// </summary>
    public static class MissileFlightTrace
    {
        public sealed class Sample
        {
            public int tick;
            public int ticksToImpact;
            public Vector3 pos;
            public float rotation;
            public bool hasTimeline;
            /// <summary>離場巡航段（升空交接之後）。</summary>
            public bool cruising;

            // 同一 tick 內 CompEffecterLaunch 的輸出（尾焰在時間軸推進前計算）。
            public bool ignited;
            public bool exhaust;
            public Vector3 exhaustPos;
            public float exhaustRotation;
            public float evaluate;
            public float speed;
        }

        public static bool Active;

        private static readonly Dictionary<Thing, List<Sample>> samples
            = new Dictionary<Thing, List<Sample>>(ReferenceEqualityComparer.Instance);

        public static void Begin()
        {
            samples.Clear();
            Active = true;
        }

        public static void End()
        {
            Active = false;
        }

        public static void Clear() => samples.Clear();

        public static List<Sample> For(Thing thing)
        {
            return thing != null && samples.TryGetValue(thing, out List<Sample> list) ? list : new List<Sample>();
        }

        private static Sample Current(Thing thing)
        {
            if (!samples.TryGetValue(thing, out List<Sample> list))
            {
                list = new List<Sample>();
                samples[thing] = list;
            }
            int now = Find.TickManager.TicksGame;
            if (list.Count == 0 || list[list.Count - 1].tick != now)
            {
                list.Add(new Sample { tick = now });
            }
            return list[list.Count - 1];
        }

        /// <summary>時間軸推進後的狀態（每 tick 一次）。</summary>
        public static void RecordTick(Thing thing, int ticksToImpact, Vector3 pos, float rotation, bool cruising = false)
        {
            if (!Active || thing == null) { return; }
            Sample s = Current(thing);
            s.hasTimeline = true;
            s.cruising = cruising;
            s.ticksToImpact = ticksToImpact;
            s.pos = pos;
            s.rotation = rotation;
        }

        public static void RecordIgnition(Thing thing)
        {
            if (!Active || thing == null) { return; }
            Current(thing).ignited = true;
        }

        public static void RecordExhaust(Thing thing, Vector3 drawPos, float rotation, float evaluate, float speed)
        {
            if (!Active || thing == null) { return; }
            Sample s = Current(thing);
            s.exhaust = true;
            s.exhaustPos = drawPos;
            s.exhaustRotation = rotation;
            s.evaluate = evaluate;
            s.speed = speed;
        }
    }
}
