using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary><see cref="IncidentWorker_FieldLaunchSite"/> 的設定，掛在 IncidentDef 的 modExtensions。</summary>
    public class FieldLaunchSiteIncidentExtension : DefModExtension
    {
        public WorldObjectDef worldObjectDef;
        public SitePartDef sitePartDef;

        /// <summary>離目標殖民地的 tile 距離（TileFinder 步數）。另外必須在陣地射程內。</summary>
        public IntRange distance = new IntRange(6, 12);

        /// <summary>世界上同時存在的 NPC 導彈陣地（任何種類）上限；達到時不觸發。</summary>
        public int maxActiveSites = 1;

        /// <summary>威脅點數的下限與上限，傳給站點（守軍人數由此換算）。</summary>
        public FloatRange threatPoints = new FloatRange(300f, 3000f);

        /// <summary>玩家完成此研究後事件才會出現（野戰陣地：雷達網）；null = 不限。</summary>
        public ResearchProjectDef requiredResearch;
    }

    /// <summary>
    /// 敵對工業派系在殖民地附近部署野戰發射陣地。由說書人以 ThreatBig 事件觸發：<br/>
    /// ‧ 玩家已完成 <see cref="FieldLaunchSiteIncidentExtension.requiredResearch"/>（雷達網）。<br/>
    /// ‧ 需有敵對、可擁有此 SitePart 的派系（<see cref="SitePartWorker_FieldLaunchSite"/>：工業等級）。<br/>
    /// ‧ 世界上 NPC 導彈陣地數量未達 <see cref="FieldLaunchSiteIncidentExtension.maxActiveSites"/>。<br/>
    /// ‧ 陣地落在可建據點、且在陣地射程內的 tile；以本體 <see cref="SiteMaker.MakeSite(SitePartDef, PlanetTile, Faction, bool, float?, WorldObjectDef)"/> 建立。
    /// </summary>
    public class IncidentWorker_FieldLaunchSite : IncidentWorker
    {
        private FieldLaunchSiteIncidentExtension Ext => def.GetModExtension<FieldLaunchSiteIncidentExtension>();

        protected override bool CanFireNowSub(IncidentParms parms)
        {
            FieldLaunchSiteIncidentExtension ext = Ext;
            if (ext?.worldObjectDef == null || ext.sitePartDef == null) return false;
            if (ext.requiredResearch != null && !ext.requiredResearch.IsFinished) return false;
            if (!(parms.target is Map map) || !map.IsPlayerHome) return false;
            if (ActiveSiteCount() >= ext.maxActiveSites) return false;
            return TryFindFaction(ext, out _) && TryFindTile(ext, map, out _);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            FieldLaunchSiteIncidentExtension ext = Ext;
            if (ext == null || !(parms.target is Map map)) return false;
            if (!TryFindFaction(ext, out Faction faction)) return false;
            if (!TryFindTile(ext, map, out PlanetTile tile)) return false;

            float points = ext.threatPoints.ClampToRange(parms.points > 0f ? parms.points : StorytellerUtility.DefaultThreatPointsNow(map));
            Site site = SiteMaker.MakeSite(ext.sitePartDef, tile, faction, threatPoints: points, worldObjectDef: ext.worldObjectDef);
            if (site == null) return false;
            Find.WorldObjects.Add(site);

            string direction = Find.WorldGrid.GetDirection8WayFromTo(map.Tile, tile).LabelShort();
            float distance = MissileWorldUtility.RangeDistance(map.Tile, tile);
            TaggedString text = "DMSE.FieldLaunchSite.SpawnedLetter".Translate(
                faction.NameColored, map.Parent.LabelCap, direction, distance.ToString("0"));
            SendStandardLetter(def.letterLabel, text, def.letterDef ?? LetterDefOf.ThreatBig, parms, site);
            return true;
        }

        /// <summary>世界上現存的 NPC 導彈陣地數（含原本的要塞化陣地）。</summary>
        public static int ActiveSiteCount()
            => Find.WorldObjects.AllWorldObjects.Count(o => !o.Destroyed && o.GetComponent<WorldObjectComp_MissileBase>() != null);

        private static bool TryFindFaction(FieldLaunchSiteIncidentExtension ext, out Faction faction)
        {
            return Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.defeated && !f.temporary && f.HostileTo(Faction.OfPlayer) && ext.sitePartDef.FactionCanOwn(f))
                .TryRandomElement(out faction);
        }

        private static bool TryFindTile(FieldLaunchSiteIncidentExtension ext, Map map, out PlanetTile tile)
        {
            int range = ext.worldObjectDef.comps?.OfType<CompProperties_MissileBase>().FirstOrDefault()?.range ?? 0;
            PlanetTile home = map.Tile;
            return TileFinder.TryFindNewSiteTile(out tile, home, ext.distance.min, ext.distance.max,
                selectLandmarkChance: 0f, exitOnFirstTileFound: false,
                validator: t => range <= 0 || MissileWorldUtility.RangeDistance(home, t) <= range);
        }
    }
}
