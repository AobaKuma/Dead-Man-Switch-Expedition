using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace DMSE
{
    /// <summary>
    /// 被擊落載具裡的 pawn：玩家陣營的移出容器、改為失蹤（留在世界 pawn）；其餘一律殺死——
    /// Vanish 不算 Dead、不送任務 Killed 信號，Boss 群等任務會卡住。
    /// 地圖層攔截（<see cref="BVRTarget"/>）與世界層封鎖攔截（<see cref="BlockadeHitUtility"/>）共用。
    /// </summary>
    public static class InterceptedPawnUtility
    {
        /// <summary>處理 <paramref name="pawns"/>（呼叫端先取出清單：殺死 pawn 會把它在容器中換成屍體）。回傳被殺死的 pawn。</summary>
        public static List<Pawn> Resolve(IEnumerable<Pawn> pawns, LookTargets look)
        {
            List<Pawn> killed = new List<Pawn>();
            List<Pawn> missing = new List<Pawn>();
            foreach (Pawn pawn in pawns.ToList())
            {
                if (pawn.Dead) { continue; }
                if (pawn.Faction != null && pawn.Faction.IsPlayer)
                {
                    pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
                    pawn.holdingOwner?.Remove(pawn);
                    // 飛行中的運輸艙乘員已經是世界 pawn；改成永久保留，避免被世界 pawn 回收。
                    if (pawn.IsWorldPawn()) { Find.WorldPawns.RemovePawn(pawn); }
                    Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
                    PawnDiedOrDownedThoughtsUtility.TryGiveThoughts(pawn, null, PawnDiedOrDownedThoughtsKind.Lost);
                    missing.Add(pawn);
                }
                else
                {
                    pawn.Kill(new DamageInfo(DamageDefOf.Blunt, 50f));
                    killed.Add(pawn);
                }
            }
            if (missing.Count > 0)
            {
                Find.LetterStack.ReceiveLetter("DMSE.BVR.PawnsMissingLabel".Translate(),
                    "DMSE.BVR.PawnsMissingText".Translate(missing.Select(p => p.LabelCap).ToLineList("  - ")),
                    LetterDefOf.NegativeEvent, look);
            }
            return killed;
        }
    }
}
