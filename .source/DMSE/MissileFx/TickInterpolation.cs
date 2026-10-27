using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 幀內的 tick 內插值（docs/導彈發射特效_重製計畫.md §0.1-1）：0 = 剛跑完上一 tick、接近 1 = 快到下一 tick。
    /// 原版沒有公開；<see cref="TickManager.TickManagerUpdate"/> 跑完整 tick 後，私有的 realTimeToTickThrough 落在
    /// (−CurTimePerTick, 0]，所以 α = 1 + realTimeToTickThrough / CurTimePerTick。讀不到（版本變動）或暫停時回傳 1（不內插，用最新姿態）。
    /// </summary>
    public static class TickInterpolation
    {
        private static readonly AccessTools.FieldRef<TickManager, float> RealTimeToTickThrough;
        private static readonly Func<TickManager, float> CurTimePerTick;

        static TickInterpolation()
        {
            try
            {
                RealTimeToTickThrough = AccessTools.FieldRefAccess<TickManager, float>("realTimeToTickThrough");
                CurTimePerTick = AccessTools.MethodDelegate<Func<TickManager, float>>(AccessTools.PropertyGetter(typeof(TickManager), "CurTimePerTick"));
            }
            catch (Exception e)
            {
                Log.Warning("[DMSE] Tick interpolation unavailable, launch plumes will step once per tick: " + e.Message);
            }
        }

        public static float Alpha
        {
            get
            {
                TickManager tm = Find.TickManager;
                if (tm == null || tm.Paused || RealTimeToTickThrough == null || CurTimePerTick == null) { return 1f; }
                float perTick = CurTimePerTick(tm);
                if (perTick <= 0f) { return 1f; }
                return Mathf.Clamp01(1f + RealTimeToTickThrough(tm) / perTick);
            }
        }
    }
}
