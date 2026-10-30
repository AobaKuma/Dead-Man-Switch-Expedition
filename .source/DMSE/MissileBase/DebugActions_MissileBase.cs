using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>開發工具：生成 NPC 導彈陣地、立即齊射、列出所有陣地的狀態。</summary>
    public static class DebugActions_MissileBase
    {
        private const string Category = "DMSE Missile Base";

        [DebugAction(Category, "Spawn NPC missile base near this map...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnNearPlayer()
        {
            Map map = Find.CurrentMap;
            var options = new[] { (5f, 10f), (10f, 15f), (15f, 20f) }
                .Select(r => new FloatMenuOption($"{r.Item1:0}–{r.Item2:0} tiles", () => Spawn(map.Tile, r.Item1, r.Item2))).ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>野戰發射陣地只給工業等級派系；威脅點數取目前地圖的預設值，結構與守軍再依難度分級。</summary>
        [DebugAction(Category, "Spawn industrial field launch site near this map...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnFieldSiteNearPlayer()
        {
            Map map = Find.CurrentMap;
            var options = new[] { (5f, 10f), (10f, 15f), (15f, 20f) }
                .Select(r => new FloatMenuOption($"{r.Item1:0}–{r.Item2:0} tiles", () => Spawn(map.Tile, r.Item1, r.Item2,
                    "DMSE_FieldLaunchSite", "DMSE_FieldLaunchSitePart", StorytellerUtility.DefaultThreatPointsNow(map), StorytellerUtility.DefaultThreatPointsNow(map)))).ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void Spawn(PlanetTile origin, float min, float max,
            string worldObjectDefName = "DMSE_MissileBase", string sitePartDefName = "DMSE_MissileBaseSitePart", float points = 2000f, float threatPoints = 0f)
        {
            WorldObjectDef baseDef = DefDatabase<WorldObjectDef>.GetNamedSilentFail(worldObjectDefName);
            SitePartDef partDef = DefDatabase<SitePartDef>.GetNamedSilentFail(sitePartDefName);
            if (baseDef == null || partDef == null)
            {
                Messages.Message($"DMSE: missing {worldObjectDefName} or {sitePartDefName}.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Faction faction = Find.FactionManager.AllFactionsListForReading
                .Where(f => f.HostileTo(Faction.OfPlayer) && partDef.FactionCanOwn(f)).RandomElementWithFallback()
                ?? DebugMissileUtility.HostileFaction();
            if (faction != null && !partDef.FactionCanOwn(faction))
                Messages.Message($"DMSE: no hostile faction can own {sitePartDefName}; using {faction.Name} ({faction.def.techLevel}).",
                    MessageTypeDefOf.CautionInput, false);
            PlanetTile tile = DebugMissileUtility.TileAtDistance(origin, min, max, buildable: true);
            if (!tile.Valid)
            {
                Messages.Message($"DMSE: no buildable tile {min:0}–{max:0} tiles away.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Site site = (Site)WorldObjectMaker.MakeWorldObject(baseDef);
            site.Tile = tile;
            if (faction != null) { site.SetFaction(faction); }
            site.AddPart(new SitePart(site, partDef, new SitePartParams { points = points, threatPoints = threatPoints }));
            Find.WorldObjects.Add(site);
            Messages.Message($"DMSE: missile base spawned at {tile} ({faction?.Name ?? "no faction"}).", site, MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>選取的陣地（沒選就用最近的）立即照正常規則齊射。</summary>
        [DebugAction(Category, "Fire salvo now (selected or nearest base)", allowedGameStates = AllowedGameStates.Playing)]
        private static void FireNow()
        {
            WorldObject selected = Find.WorldSelector.SingleSelectedObject;
            WorldObject wo = selected?.GetComponent<WorldObjectComp_MissileBase>() != null ? selected : null;
            if (wo == null)
            {
                PlanetTile from = Find.CurrentMap?.Tile ?? PlanetTile.Invalid;
                wo = Find.WorldObjects.AllWorldObjects.Where(o => o.GetComponent<WorldObjectComp_MissileBase>() != null)
                    .OrderBy(o => from.Valid ? MissileWorldUtility.RangeDistance(from, o.Tile) : 0f).FirstOrDefault();
            }
            if (wo == null)
            {
                Messages.Message("DMSE: no NPC missile base.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int fired = wo.GetComponent<WorldObjectComp_MissileBase>().TryFireAtPlayerColonies();
            Messages.Message(fired > 0 ? $"DMSE: {wo.Label} fired {fired} missile(s)." : $"DMSE: {wo.Label} has no player colony in range.",
                wo, fired > 0 ? MessageTypeDefOf.ThreatBig : MessageTypeDefOf.RejectInput, false);
        }

        [DebugAction(Category, "Log all missile bases", allowedGameStates = AllowedGameStates.Playing)]
        private static void LogBases()
        {
            var sb = new StringBuilder("[DMSE MissileBase]");
            WorldComponent_FactionAirDefenseAlert alert = WorldComponent_FactionAirDefenseAlert.Current;
            int n = 0;
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects.Where(o => o.GetComponent<WorldObjectComp_MissileBase>() != null))
            {
                n++;
                WorldObjectComp_AirDefense defense = wo.GetComponent<WorldObjectComp_AirDefense>();
                MapParent mp = wo as MapParent;
                sb.Append($"\n  {wo.Label} tile={wo.Tile} faction={wo.Faction?.Name ?? "none"} map={mp?.HasMap == true}"
                    + $" stock={(defense != null ? defense.Stock + "/" + defense.Props.interceptorStock : "-")}"
                    + $" alertStage={(mp != null ? alert?.StageOf(mp).ToString() ?? "-" : "-")}");
            }
            if (n == 0) { sb.Append(" none"); }
            Log.Message(sb.ToString());
        }
    }
}
