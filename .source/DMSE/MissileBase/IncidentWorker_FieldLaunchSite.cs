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

        /// <summary>生成信函的翻譯鍵：{0} 派系、{1} 殖民地、{2} 方位、{3} 距離。</summary>
        public string spawnedLetterKey = "DMSE.FieldLaunchSite.SpawnedLetter";
    }

    /// <summary><see cref="IncidentWorker_EarlyLaunchSite"/> 的設定。</summary>
    public class EarlyLaunchSiteIncidentExtension : FieldLaunchSiteIncidentExtension
    {
        /// <summary>分界研究（雷達網）。未完成：高點數才出現；完成後：只由海盜隨機部署。</summary>
        public ResearchProjectDef gateResearch;

        /// <summary>分界研究未完成時，事件點數至少要到這個值。</summary>
        public float minPointsBeforeResearch = 1500f;

        /// <summary>分界研究完成後的機率倍率：野戰陣地成為主力，舊式陣地只偶爾由海盜部署。</summary>
        public float chanceFactorAfterResearch = 0.5f;
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
            if (!ConditionsMet(ext, parms, map)) return false;
            return TryFindFaction(ext, out _) && TryFindTile(ext, map, out _);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            FieldLaunchSiteIncidentExtension ext = Ext;
            if (ext == null || !(parms.target is Map map)) return false;
            if (!TryFindFaction(ext, out Faction faction)) return false;
            if (!TryFindTile(ext, map, out PlanetTile tile)) return false;

            float points = ext.threatPoints.ClampToRange(PointsOf(parms, map));
            Site site = SiteMaker.MakeSite(ext.sitePartDef, tile, faction, threatPoints: points, worldObjectDef: ext.worldObjectDef);
            if (site == null) return false;
            Find.WorldObjects.Add(site);

            string direction = Find.WorldGrid.GetDirection8WayFromTo(map.Tile, tile).LabelShort();
            float distance = MissileWorldUtility.RangeDistance(map.Tile, tile);
            TaggedString text = ext.spawnedLetterKey.Translate(
                faction.NameColored, map.Parent.LabelCap, direction, distance.ToString("0"));
            SendStandardLetter(def.letterLabel, text, def.letterDef ?? LetterDefOf.ThreatBig, parms, site);
            return true;
        }

        /// <summary>世界上現存的 NPC 導彈陣地數（含原本的要塞化陣地）。</summary>
        public static int ActiveSiteCount()
            => Find.WorldObjects.AllWorldObjects.Count(o => !o.Destroyed && o.GetComponent<WorldObjectComp_MissileBase>() != null);

        protected static float PointsOf(IncidentParms parms, Map map)
            => parms.points > 0f ? parms.points : StorytellerUtility.DefaultThreatPointsNow(map);

        /// <summary>子類的額外觸發條件。</summary>
        protected virtual bool ConditionsMet(FieldLaunchSiteIncidentExtension ext, IncidentParms parms, Map map) => true;

        /// <summary>子類進一步限制可部署的派系（已通過敵對與 SitePart 檢查）。</summary>
        protected virtual bool FactionAllowed(FieldLaunchSiteIncidentExtension ext, Faction faction) => true;

        private bool TryFindFaction(FieldLaunchSiteIncidentExtension ext, out Faction faction)
        {
            return Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.defeated && !f.temporary && f.HostileTo(Faction.OfPlayer) && ext.sitePartDef.FactionCanOwn(f)
                    && FactionAllowed(ext, f))
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

    /// <summary>
    /// 舊式導彈陣地（雷達網之前的早期導彈襲擊）：<br/>
    /// ‧ 玩家尚未完成雷達網：事件點數達 <see cref="EarlyLaunchSiteIncidentExtension.minPointsBeforeResearch"/> 才出現，
    ///   敵對工業派系與海盜都可能部署。<br/>
    /// ‧ 玩家已完成雷達網：不看點數門檻，但只有海盜會部署，機率乘上
    ///   <see cref="EarlyLaunchSiteIncidentExtension.chanceFactorAfterResearch"/>。
    /// </summary>
    public class IncidentWorker_EarlyLaunchSite : IncidentWorker_FieldLaunchSite
    {
        private EarlyLaunchSiteIncidentExtension EarlyExt => def.GetModExtension<EarlyLaunchSiteIncidentExtension>();

        private static bool GateFinished(EarlyLaunchSiteIncidentExtension ext) => ext?.gateResearch != null && ext.gateResearch.IsFinished;

        protected override bool ConditionsMet(FieldLaunchSiteIncidentExtension ext, IncidentParms parms, Map map)
        {
            EarlyLaunchSiteIncidentExtension early = EarlyExt;
            return early != null && (GateFinished(early) || PointsOf(parms, map) >= early.minPointsBeforeResearch);
        }

        protected override bool FactionAllowed(FieldLaunchSiteIncidentExtension ext, Faction faction)
            => !GateFinished(EarlyExt) || SitePartWorker_EarlyLaunchSite.IsPirate(faction);

        public override float ChanceFactorNow(IIncidentTarget target)
        {
            EarlyLaunchSiteIncidentExtension early = EarlyExt;
            return base.ChanceFactorNow(target) * (GateFinished(early) ? early.chanceFactorAfterResearch : 1f);
        }
    }
}
