using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>開發工具：列出地圖上的世界打擊發射器與可用彈藥（含保留狀態）。</summary>
    public static class DebugActions_Strike
    {
        [DebugAction("DMSE Strike", "Log strike rounds on map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogRounds()
        {
            MapComponent_StrikePlanner planner = Find.CurrentMap?.GetComponent<MapComponent_StrikePlanner>();
            if (planner == null) { return; }
            StringBuilder sb = new StringBuilder("[DMSE Strike] registered world-targeting launchers: " + planner.launchers.Count);
            foreach (CompMissileLauncher_WorldTargeting launcher in planner.launchers)
            {
                sb.Append($"\n  {launcher.parent.LabelShort} ({launcher.GetType().Name}) active={launcher.Active}"
                    + $" cooldown={launcher.CooldownTicksRemaining(Find.TickManager.TicksGame)} salvo={launcher.SalvoIntervalTicks}");
                foreach (Thing round in launcher.StrikeRounds)
                {
                    MissileConfig config = launcher.BuildFiringConfig(round);
                    sb.Append($"\n    - {round.LabelShort} body={config?.body?.defName ?? "none"} range={config?.Range ?? 0}"
                        + $" reservedBy={planner.OwnerOf(round)}");
                }
            }
            Log.Message(sb.ToString());
        }
    }
}
