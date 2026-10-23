using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 選取據點時的警戒資訊（§9）：已知階段、有效能力及封鎖開始／解除；精確庫存與壓力只在開發模式顯示。
    /// 「第五階段：防空封鎖／暫失執行能力」分開顯示，避免把被壓制誤認為已解除警戒。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.GetInspectString))]
    public static class Patch_MapParent_AirDefenseAlertInspect
    {
        public static void Postfix(MapParent __instance, ref string __result)
        {
            // Site 可能清掉 base 的文字（主威脅標籤），改由下面的 Site 補丁附加。
            if (__instance is Site) { return; }
            AirDefenseAlertUI.Append(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(Site), nameof(Site.GetInspectString))]
    public static class Patch_Site_AirDefenseAlertInspect
    {
        public static void Postfix(Site __instance, ref string __result) => AirDefenseAlertUI.Append(__instance, ref __result);
    }

    public static class AirDefenseAlertUI
    {
        public static void Append(MapParent parent, ref string text)
        {
            string extra = InspectFor(parent);
            if (extra.NullOrEmpty()) { return; }
            text = text.NullOrEmpty() ? extra : text + "\n" + extra;
        }

        public static string InspectFor(MapParent parent)
        {
            if (!WorldComponent_FactionAirDefenseAlert.Enabled || parent?.Faction == null || parent.Faction.IsPlayer) { return null; }
            WorldComponent_FactionAirDefenseAlert alert = WorldComponent_FactionAirDefenseAlert.Current;
            if (alert == null) { return null; }
            bool radar = WorldComponent_FactionAirDefenseAlert.RadarCapable(parent);
            AirDefenseSiteAlert state = alert.StateFor(parent, create: radar);
            if (state == null) { return null; }
            int now = Find.TickManager.TicksGame;

            var sb = new StringBuilder();
            sb.Append("DMSE.AirAlert.InspectStage".Translate(state.stage, WorldComponent_FactionAirDefenseAlert.StageLabel(state.stage)));
            string capability = CapabilityProblem(parent, state, radar, out BlockadeZone zone);
            if (capability != null) { sb.Append("\n").Append("DMSE.AirAlert.InspectUnavailable".Translate(capability)); }
            if (state.BlockadePending)
            {
                sb.Append("\n").Append("DMSE.AirAlert.InspectBlockadePending".Translate((state.blockadeAt - now).ToStringTicksToPeriod()));
            }
            else if (state.BlockadeActive && zone != null)
            {
                sb.Append("\n").Append("DMSE.AirAlert.InspectBlockadeActive".Translate(zone.radius.ToString("0.#")));
            }
            if (Prefs.DevMode)
            {
                state.Decay(now);
                sb.Append($"\n[Dev] L {state.local:0.0} R {state.regional:0.0} T {state.Threat:0.0} waves {state.RecentWaves(now)} "
                    + $"direct {(now <= state.directThreatUntil ? "yes" : "no")} baseline {state.baseline} since {(now - state.stageSinceTick).ToStringTicksToPeriod()}");
            }
            return sb.ToString();
        }

        /// <summary>目前無法執行的原因；能執行時回傳 null。封鎖中的據點同時回傳有效封鎖圈。</summary>
        private static string CapabilityProblem(MapParent parent, AirDefenseSiteAlert state, bool radar, out BlockadeZone zone)
        {
            zone = null;
            if (!radar) { return "DMSE.AirAlert.Unavailable.NoEquipment".Translate(); }
            WorldComponent_BVRNetwork network = WorldComponent_BVRNetwork.Current;
            BVRNode node = network?.NodeOf(parent);
            if (node == null) { return "DMSE.AirAlert.Unavailable.NoNode".Translate(); }
            if (!WorldComponent_FactionAirDefenseAlert.Current.RadarWarm(parent)) { return "DMSE.AirAlert.Unavailable.WarmingUp".Translate(); }
            if (state.stage >= (int)AirDefenseStage.Blockade)
            {
                zone = AirspaceRestrictionUtility.ZoneFor(node, network.Nodes, out string key);
                return key?.Translate();
            }
            if (node.EquipmentDetectionRange(BVRTargetProps.Default) <= 0f) { return "DMSE.AirAlert.Unavailable.RadarDown".Translate(); }
            if (state.stage >= (int)AirDefenseStage.Passive && !node.HasInterceptorAmmo) { return "DMSE.AirAlert.Unavailable.NoAmmo".Translate(); }
            return null;
        }
    }

    /// <summary>開發工具（§9）：指定階段、注入事件、增減壓力、切換外交關係、耗盡彈藥／壓制雷達、列出接戰理由。</summary>
    public static class DebugActions_AirDefenseAlert
    {
        private const string Category = "DMSE Air Alert";

        private static MapParent Selected
        {
            get
            {
                MapParent parent = Find.WorldSelector?.SingleSelectedObject as MapParent;
                if (parent == null || parent.Faction == null || parent.Faction.IsPlayer)
                {
                    Messages.Message("Select a non-player settlement or site on the world map first.", MessageTypeDefOf.RejectInput, historical: false);
                    return null;
                }
                return parent;
            }
        }

        private static WorldComponent_FactionAirDefenseAlert Alert => WorldComponent_FactionAirDefenseAlert.Current;

        [DebugAction(Category, "Set alert stage (selected site)", allowedGameStates = AllowedGameStates.Playing)]
        private static void SetStage()
        {
            MapParent parent = Selected;
            if (parent == null) { return; }
            var options = new List<FloatMenuOption>();
            for (int stage = 1; stage <= 5; stage++)
            {
                int s = stage;
                options.Add(new FloatMenuOption($"{s}: {WorldComponent_FactionAirDefenseAlert.StageLabel(s)}", () => Alert?.DevSetStage(parent, s)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        [DebugAction(Category, "Inject alert event (selected site)", allowedGameStates = AllowedGameStates.Playing)]
        private static void InjectEvent()
        {
            MapParent parent = Selected;
            if (parent == null) { return; }
            var options = new List<FloatMenuOption>();
            foreach (string kind in new[] { "transit", "targeted", "hit" })
            {
                string k = kind;
                options.Add(new FloatMenuOption(k, () => Alert?.DevInject(parent, k)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        [DebugAction(Category, "Adjust pressure (selected site)...", allowedGameStates = AllowedGameStates.Playing)]
        private static void AdjustPressure()
        {
            MapParent parent = Selected;
            if (parent == null) { return; }
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("Local +20", () => Alert?.DevAddPressure(parent, 20f, 0f)),
                new FloatMenuOption("Regional +20", () => Alert?.DevAddPressure(parent, 0f, 20f)),
                new FloatMenuOption("Both -20", () => Alert?.DevAddPressure(parent, -20f, -20f)),
                new FloatMenuOption("Clear", () => Alert?.DevAddPressure(parent, -100f, -100f)),
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        [DebugAction(Category, "Spawn allied settlement near selected site", allowedGameStates = AllowedGameStates.Playing)]
        private static void SpawnNeighbor()
        {
            MapParent parent = Selected;
            if (parent == null) { return; }
            PlanetTile tile = DebugMissileUtility.TileAtDistance(parent.Tile, 3f, 8f, buildable: true);
            if (!tile.Valid)
            {
                Messages.Message("DMSE: no buildable tile 3–8 tiles from the selected site.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            settlement.Tile = tile;
            settlement.SetFaction(parent.Faction);
            settlement.Name = SettlementNameGenerator.GenerateSettlementName(settlement);
            Find.WorldObjects.Add(settlement);
            Messages.Message($"DMSE: {settlement.Label} ({parent.Faction.Name}) spawned for regional-pressure tests.", settlement,
                MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction(Category, "Cycle relation with player (selected site)", allowedGameStates = AllowedGameStates.Playing)]
        private static void CycleRelation()
        {
            MapParent parent = Selected;
            if (parent == null) { return; }
            Faction faction = parent.Faction;
            FactionRelationKind next = faction.PlayerRelationKind == FactionRelationKind.Hostile ? FactionRelationKind.Neutral
                : faction.PlayerRelationKind == FactionRelationKind.Neutral ? FactionRelationKind.Ally : FactionRelationKind.Hostile;
            faction.SetRelationDirect(Faction.OfPlayer, next, false, null, null);
            AirspaceRestrictionUtility.Invalidate();
            Messages.Message($"{faction.Name} is now {next} to the player.", MessageTypeDefOf.NeutralEvent, historical: false);
        }

        [DebugAction(Category, "Drain interceptors (selected site)", allowedGameStates = AllowedGameStates.Playing)]
        private static void Drain()
        {
            MapParent parent = Selected;
            if (parent == null) { return; }
            WorldObjectComp_AirDefense comp = parent.GetComponent<WorldObjectComp_AirDefense>();
            if (comp != null) { comp.DevDrain(); }
            else { WorldComponent_SettlementDefense.Current?.DevDrain(parent); }
            AirspaceRestrictionUtility.Invalidate();
        }

        [DebugAction(Category, "Suppress radar (selected site)", allowedGameStates = AllowedGameStates.Playing)]
        private static void Suppress()
        {
            MapParent parent = Selected;
            if (parent == null) { return; }
            int now = Find.TickManager.TicksGame;
            WorldObjectComp_AirDefense comp = parent.GetComponent<WorldObjectComp_AirDefense>();
            if (comp != null) { comp.Degrade(now); }
            else { WorldComponent_SettlementDefense.Current?.DefenseFor(parent)?.Degrade(now); }
            AirspaceRestrictionUtility.Invalidate();
        }

        [DebugAction(Category, "Log alert states (all sites)", allowedGameStates = AllowedGameStates.Playing)]
        private static void LogStates()
        {
            WorldComponent_FactionAirDefenseAlert alert = Alert;
            if (alert == null) { return; }
            int now = Find.TickManager.TicksGame;
            var sb = new StringBuilder("[DMSE Air Alert] sites:");
            foreach (AirDefenseSiteAlert state in alert.AllStates.OrderByDescending(s => s.stage))
            {
                state.Decay(now);
                sb.Append($"\n  #{state.siteId} {state.parent?.Label ?? "?"} [{state.faction?.Name}] stage={state.stage} base={state.baseline}"
                    + $" L={state.local:0.#} R={state.regional:0.#} T={state.Threat:0.#} waves={state.RecentWaves(now)}"
                    + (state.BlockadePending ? $" blockadeIn={state.blockadeAt - now}" : "")
                    + (now <= state.directThreatUntil ? " directThreat" : ""));
            }
            Log.Message(sb.ToString());
        }

        [DebugAction(Category, "Log track decisions and blockades", allowedGameStates = AllowedGameStates.Playing)]
        private static void LogDecisions()
        {
            string text = WorldComponent_BVRNetwork.Current?.DevDescribeDecisions() ?? "No BVR network.";
            Log.Message("[DMSE Air Alert]\n" + text);
        }
    }
}
