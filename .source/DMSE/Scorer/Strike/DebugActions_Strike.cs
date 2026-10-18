using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 波次打擊 P0 的除錯動作：手動保留／釋放在架彈藥，檢查單發 Gizmo、SAM 與裝配是否略過保留彈。
    /// 手動保留不存檔（<see cref="MapComponent_StrikePlanner.DebugOwner"/>）。
    /// </summary>
    public static class DebugActions_Strike
    {
        [DebugAction("DMSE", "Strike: reserve rounds on selected launchers", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReserveSelected()
        {
            int count = 0;
            foreach (ThingWithComps building in Find.Selector.SelectedObjectsListForReading.OfType<ThingWithComps>())
            {
                MapComponent_StrikePlanner planner = StrikeReservation.PlannerFor(building);
                if (planner == null) { continue; }
                foreach (CompMissileLauncher_WorldTargeting launcher in building.GetComps<CompMissileLauncher_WorldTargeting>())
                {
                    foreach (Thing round in launcher.StrikeRounds.ToList())
                    {
                        if (planner.TryReserve(round, MapComponent_StrikePlanner.DebugOwner)) { count++; }
                    }
                }
            }
            Messages.Message($"DMSE: reserved {count} strike round(s) (debug, not saved)", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("DMSE", "Strike: release debug reservations", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReleaseDebug()
        {
            Find.CurrentMap?.GetComponent<MapComponent_StrikePlanner>()?.ReleaseAll(MapComponent_StrikePlanner.DebugOwner);
            Messages.Message("DMSE: debug strike reservations released", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("DMSE", "Strike: log strike rounds on map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
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
