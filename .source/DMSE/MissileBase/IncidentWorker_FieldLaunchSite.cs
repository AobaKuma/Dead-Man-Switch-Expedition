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

        /// <summary>
        /// true：<see cref="maxActiveSites"/> 只數同一種站點（<see cref="worldObjectDef"/>）。給長期存在的戰略站點用，
        /// 它不會因為附近有野戰陣地而不出現，也不會擋住其他陣地（另見 <see cref="CompProperties_MissileBase.countsTowardSiteLimit"/>）。
        /// </summary>
        public bool countOwnKindOnly;

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
            if (ActiveSiteCount(ext) >= ext.maxActiveSites) return false;
            if (!ConditionsMet(ext, parms, map)) return false;
            return TryFindFaction(ext, out _) && TryFindTile(ext, map, out _);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            FieldLaunchSiteIncidentExtension ext = Ext;
            if (ext == null) return false;
            Map map = HomeMapFor(parms);
            if (map == null)
                return ForcedFailure(parms, "no player home map to deploy near");
            if (!TryFindFaction(ext, out Faction faction))
                return ForcedFailure(parms, $"no hostile faction can own {ext.sitePartDef?.defName} "
                    + $"(allowed: {SitePartFactionHint(ext.sitePartDef)})");
            if (!TryFindTile(ext, map, out PlanetTile tile))
                return ForcedFailure(parms, $"no free tile {ext.distance.min}–{ext.distance.max} tiles from {map.Parent.LabelCap}"
                    + $" within range {SiteRange(ext)}{TileHint}");

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

        /// <summary>
        /// 部署的基準殖民地：目標是地圖時用它；開發者從世界地圖強制觸發（目標是 World）時，改用目前畫面上的家園地圖，沒有就任一家園地圖。
        /// 說書人不會以 World 為目標觸發（<see cref="CanFireNowSub"/> 只接受家園地圖）。
        /// </summary>
        protected static Map HomeMapFor(IncidentParms parms)
        {
            if (parms.target is Map m) return m.IsPlayerHome ? m : (parms.forced ? Find.AnyPlayerHomeMap : null);
            if (parms.forced && parms.target is World)
                return Find.CurrentMap != null && Find.CurrentMap.IsPlayerHome ? Find.CurrentMap : Find.AnyPlayerHomeMap;
            return null;
        }

        /// <summary>強制觸發（開發者）失敗時說明原因；說書人觸發時 CanFireNow 已先擋掉，不會走到這裡。</summary>
        protected bool ForcedFailure(IncidentParms parms, string reason)
        {
            if (parms.forced)
            {
                string text = $"DMSE: {def.defName} did not fire: {reason}.";
                Log.Warning(text);
                Messages.Message(text, MessageTypeDefOf.RejectInput, false);
            }
            return false;
        }

        /// <summary>找不到落點時附在說明後面的提示。</summary>
        protected virtual string TileHint => string.Empty;

        private static string SitePartFactionHint(SitePartDef part)
        {
            System.Collections.Generic.List<FactionDef> list = part?.GetModExtension<SeaLaunchSiteFactionExtension>()?.factions;
            if (!list.NullOrEmpty())
            {
                return string.Join(", ", list.Select(f =>
                {
                    Faction fa = Find.FactionManager.FirstFactionOfDef(f);
                    return fa == null ? $"{f.defName} (not in this world)" : $"{f.defName} ({(fa.HostileTo(Faction.OfPlayer) ? "hostile" : "not hostile")})";
                }));
            }
            return part?.minFactionTechLevel.ToString() ?? "?";
        }

        /// <summary>
        /// 世界上現存、計入共同上限的 NPC 導彈陣地數（含原本的要塞化陣地）。
        /// <see cref="CompProperties_MissileBase.countsTowardSiteLimit"/> 為 false 的站點（彈道發射場）不算。
        /// </summary>
        public static int ActiveSiteCount()
            => Find.WorldObjects.AllWorldObjects.Count(o => !o.Destroyed
                && o.GetComponent<WorldObjectComp_MissileBase>() is WorldObjectComp_MissileBase comp && comp.Props.countsTowardSiteLimit);

        /// <summary>這個事件要比較的站點數：只數同種（<see cref="FieldLaunchSiteIncidentExtension.countOwnKindOnly"/>）或共同上限。</summary>
        public static int ActiveSiteCount(FieldLaunchSiteIncidentExtension ext)
            => ext.countOwnKindOnly
                ? Find.WorldObjects.AllWorldObjects.Count(o => !o.Destroyed && o.def == ext.worldObjectDef)
                : ActiveSiteCount();

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

        protected static int SiteRange(FieldLaunchSiteIncidentExtension ext)
            => ext.worldObjectDef.comps?.OfType<CompProperties_MissileBase>().FirstOrDefault()?.range ?? 0;

        /// <summary>陣地落點：預設是陸地上可建據點的 tile（<see cref="TileFinder.TryFindNewSiteTile"/>）。</summary>
        protected virtual bool TryFindTile(FieldLaunchSiteIncidentExtension ext, Map map, out PlanetTile tile)
        {
            int range = SiteRange(ext);
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
