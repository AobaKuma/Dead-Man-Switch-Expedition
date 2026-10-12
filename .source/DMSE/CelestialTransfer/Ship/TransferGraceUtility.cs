using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移後安全期：轉移艦抵達後，在降落的地圖上掛遊戲條件 <see cref="DMSE_DefOf.DMSE_TransferGrace"/>
    /// （<c>preventIncidents</c>）。原版 <c>IncidentWorker.CanFireNow</c> 對有此條件的地圖一律不觸發說書人事件
    /// （襲擊、新的遊戲條件、訪客等）；任務或劇本強制觸發的事件（<c>parms.forced</c>）不受影響。
    ///
    /// 長度 = min(上限, 上限 × 門檻格數 / 船體格數) × 難度係數：船越小，安全期越長。
    /// 計時從抵達開始；之後降落到其他地圖時，只把剩餘時間轉移過去，不重新計時。
    /// </summary>
    public static class TransferGraceUtility
    {
        /// <summary>目前難度的係數（依 threatScale 換算）。</summary>
        public static float DifficultyFactor
        {
            get
            {
                Difficulty difficulty = Find.Storyteller?.difficulty;
                if (difficulty == null)
                {
                    return 1f;
                }
                return Mathf.Max(0f, TransferFlightMath.Tuning.graceFactorByThreatScale.Evaluate(difficulty.threatScale));
            }
        }

        /// <summary>船體格數對應的安全期（tick）。</summary>
        public static int GraceTicksFor(int hullCells)
        {
            TransferFlightTuningDef tuning = TransferFlightMath.Tuning;
            if (hullCells <= 0 || tuning.graceMaxDays <= 0f)
            {
                return 0;
            }
            float days = tuning.graceMaxDays * Mathf.Min(1f, (float)tuning.graceThresholdCells / hullCells);
            return Mathf.RoundToInt(days * DifficultyFactor * GenDate.TicksPerDay);
        }

        /// <summary>地圖上安全期的剩餘 tick；沒有安全期為 0。</summary>
        public static int GraceTicksLeft(Map map)
        {
            GameCondition cond = map?.gameConditionManager.GetActiveCondition(DMSE_DefOf.DMSE_TransferGrace);
            return cond != null && !cond.Permanent ? Mathf.Max(0, cond.TicksLeft) : 0;
        }

        /// <summary>在地圖上開始（或延長到）指定長度的安全期。</summary>
        public static void BeginGrace(Map map, int ticks)
        {
            if (map == null || ticks <= 0)
            {
                return;
            }
            GameCondition cond = map.gameConditionManager.GetActiveCondition(DMSE_DefOf.DMSE_TransferGrace);
            if (cond != null)
            {
                if (!cond.Permanent && cond.TicksLeft < ticks)
                {
                    cond.TicksLeft = ticks;
                }
                return;
            }
            map.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(DMSE_DefOf.DMSE_TransferGrace, ticks));
        }

        /// <summary>轉移艦抵達：依船體地圖上的船體格數開始安全期並通知玩家。</summary>
        public static void BeginAfterArrival(WorldObject_TransferShip ship)
        {
            if (ship == null || !ship.HasMap)
            {
                return;
            }
            int cells = TransferLandingUtility.HullCells(ship.Map).Count;
            int ticks = GraceTicksFor(cells);
            if (ticks <= 0)
            {
                return;
            }
            BeginGrace(ship.Map, ticks);
            Messages.Message("DMSE.Transfer.Grace.Begin".Translate(ship.Label, ticks.ToStringTicksToPeriod(), cells), ship, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>降落：把船體地圖剩餘的安全期轉移到降落的地圖（不重新計時）。</summary>
        public static void CarryOver(Map from, Map to)
        {
            BeginGrace(to, GraceTicksLeft(from));
        }
    }
}
