using System.Collections.Generic;
using System.Linq;
using Fortified.Structures;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 海上發射站（2026-11-23）：野戰發射陣地的海上版，蓋在海上平台地圖上（<see cref="SeaPlatformMapExtension"/>）。
    /// 每座結構先在底下鋪一塊甲板（外擴 <see cref="deckMargin"/> 格）再生成，不受原地形限制；
    /// 結構之間的走道由 <see cref="GenStep_FieldLaunchSiteRoads"/> 以甲板鋪成（openTerrain = 高空），
    /// 最後 <see cref="GenStep_SeaLaunchSitePillars"/> 補上撐住所有甲板的支柱。
    /// </summary>
    public class GenStep_SeaLaunchSite : GenStep_FieldLaunchSite
    {
        /// <summary>結構四周多鋪幾格甲板（人員進出、走道接上門口）。</summary>
        public int deckMargin = 2;

        public override int SeedPart => 0x5EA1A5C7;

        protected override bool GroundFits(Map map, Sketch sketch, IntVec3 offset, CellRect rect)
        {
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(map);
            if (ext == null) return FitsNaturalGround(map, sketch, offset);
            foreach (IntVec3 c in rect.ExpandedBy(deckMargin))
            {
                if (!c.InBounds(map) || c.GetEdifice(map) != null) return false;
                TerrainDef t = map.terrainGrid.BaseTerrainAt(c);
                if (t != ext.voidTerrain && t != ext.deckTerrain) return false;
            }
            return true;
        }

        /// <summary>海上沒有自然地形：不論 keepNaturalTerrain，都要確認整塊（含外擴）是空的高空或甲板。</summary>
        protected override bool CheckGround => true;

        /// <summary>
        /// 結構自己畫了甲板（LaunchSite_Ocean.xml 的海上平台）：照它的形狀鋪，只在它的地形格與建築佔地底下補甲板，
        /// 讓地板（鋪面、混凝土）疊在甲板上，平台外形不被填成整個矩形。<br/>
        /// 其他結構（野戰陣地的雷達、近防）：整個範圍外擴 <see cref="deckMargin"/> 格鋪甲板。
        /// </summary>
        protected override void PrepareGround(Map map, Sketch sketch, IntVec3 offset, CellRect rect)
        {
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(map);
            if (ext == null) return;
            if (HasOwnDeck(sketch, ext))
            {
                foreach (SketchTerrain st in sketch.Terrain)
                    LayDeck(map, ext, st.pos + offset);
                foreach (SketchThing st in sketch.Things)
                    foreach (IntVec3 c in GenAdj.OccupiedRect(st.pos + offset, st.rot, st.def.size))
                        LayDeck(map, ext, c);
                return;
            }
            foreach (IntVec3 c in rect.ExpandedBy(deckMargin).ClipInsideMap(map))
                LayDeck(map, ext, c);
        }

        private static bool HasOwnDeck(Sketch sketch, SeaPlatformMapExtension ext)
        {
            foreach (SketchTerrain st in sketch.Terrain)
                if (st.def == ext.deckTerrain) return true;
            return false;
        }

        private static void LayDeck(Map map, SeaPlatformMapExtension ext, IntVec3 c)
        {
            if (c.InBounds(map) && map.terrainGrid.BaseTerrainAt(c) == ext.voidTerrain)
                map.terrainGrid.SetTerrain(c, ext.deckTerrain);
        }
    }

    /// <summary>
    /// 海上發射站的支柱：每一塊相連的甲板至少一根，而且每格甲板都在某根支柱的支撐半徑內
    /// （否則 <see cref="MapComponent_SeaPlatformSupport"/> 會在開局 15 秒後讓它塌進海裡）。
    /// 貪婪擺放：挑一格還沒撐住的甲板，在它附近抽樣候選位置，選能多撐住最多甲板的一個。
    /// 支柱屬於站點派系；玩家打掉支柱，撐不住的甲板連同上面的雷達與發射架一起崩塌。
    /// </summary>
    public class GenStep_SeaLaunchSitePillars : GenStep
    {
        public ThingDef pillarDef;

        /// <summary>候選位置離目標格最遠多少（支撐半徑的比例）；小一點讓支柱之間的範圍重疊。</summary>
        public float searchRadiusFactor = 0.6f;

        public int candidatesPerPillar = 160;

        public int maxPillars = 40;

        public override int SeedPart => 0x5EA1B11A;

        public override void Generate(Map map, GenStepParams parms)
        {
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(map);
            SeaPlatformPillarExtension pillarExt = pillarDef?.GetModExtension<SeaPlatformPillarExtension>();
            if (ext == null || pillarExt == null)
            {
                Log.Error($"[DMSE SeaLaunchSite] {map.generatorDef?.defName}: needs a SeaPlatformMapExtension and a pillarDef with SeaPlatformPillarExtension.");
                return;
            }
            Faction faction = map.ParentFaction;
            float radius = pillarExt.supportRadius;
            List<CellRect> structures = MapGenerator.TryGetVar(FieldLaunchSiteRoads.RectsVar, out List<CellRect> rects) ? rects : new List<CellRect>();

            // 結構自帶的支柱（海上平台各有一根）已經撐住一部分甲板，只補它們範圍以外的
            List<Vector3> existing = map.listerThings.ThingsOfDef(pillarDef).Select(t => t.TrueCenter()).ToList();

            int placed = 0, uncoveredLeft = 0;
            foreach (List<IntVec3> island in DeckIslands(map, ext))
            {
                HashSet<IntVec3> island1 = new HashSet<IntVec3>(island);
                List<IntVec3> uncovered = island.Where(c => !existing.Any(p => SeaPlatformGeometry.Supports(p, c, radius))).ToList();
                // 候選位置一定碰得到這塊甲板，所以撐住所有格子時也一定接上了支柱
                while (uncovered.Count > 0 && placed < maxPillars)
                {
                    IntVec3 target = uncovered.RandomElement();
                    if (!TryBestSpot(map, ext, target, radius, island1, uncovered, structures, out IntVec3 spot))
                    {
                        // 這格附近放不下支柱：跳過，其他支柱也許剛好撐得到它
                        uncovered.Remove(target);
                        uncoveredLeft++;
                        continue;
                    }
                    Thing pillar = ThingMaker.MakeThing(pillarDef);
                    if (faction != null) pillar.SetFaction(faction);
                    GenSpawn.Spawn(pillar, spot, map, Rot4.North);
                    placed++;
                    Vector3 center = pillar.TrueCenter();
                    uncovered.RemoveAll(c => SeaPlatformGeometry.Supports(center, c, radius));
                }
                uncoveredLeft += uncovered.Count;
            }

            if (Prefs.DevMode)
                Log.Message($"[DMSE SeaLaunchSite] {existing.Count} structure pillars + {placed} placed; {uncoveredLeft} deck cells left without a pillar in range.");
        }

        /// <summary>以共邊相連分出每一塊甲板。</summary>
        private static List<List<IntVec3>> DeckIslands(Map map, SeaPlatformMapExtension ext)
        {
            List<List<IntVec3>> result = new List<List<IntVec3>>();
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            Queue<IntVec3> queue = new Queue<IntVec3>();
            foreach (IntVec3 start in map.AllCells)
            {
                if (seen.Contains(start) || !SeaPlatformUtility.IsDeck(start, map, ext)) continue;
                List<IntVec3> island = new List<IntVec3>();
                seen.Add(start);
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    IntVec3 c = queue.Dequeue();
                    island.Add(c);
                    for (int i = 0; i < 4; i++)
                    {
                        IntVec3 n = c + GenAdj.CardinalDirections[i];
                        if (!seen.Contains(n) && SeaPlatformUtility.IsDeck(n, map, ext))
                        {
                            seen.Add(n);
                            queue.Enqueue(n);
                        }
                    }
                }
                result.Add(island);
            }
            return result;
        }

        private bool TryBestSpot(Map map, SeaPlatformMapExtension ext, IntVec3 target, float radius, HashSet<IntVec3> island,
            List<IntVec3> uncovered, List<CellRect> structures, out IntVec3 best)
        {
            best = IntVec3.Invalid;
            int bestScore = -1;
            float search = radius * searchRadiusFactor;
            for (int i = 0; i < candidatesPerPillar; i++)
            {
                Vector2 v = Rand.InsideUnitCircle * search;
                IntVec3 c = i == 0 ? target : target + new IntVec3(Mathf.RoundToInt(v.x), 0, Mathf.RoundToInt(v.y));
                if (!ValidSpot(map, ext, c, island, structures)) continue;
                Vector3 center = c.ToVector3Shifted();
                int score = 0;
                for (int j = 0; j < uncovered.Count; j++)
                    if (SeaPlatformGeometry.Supports(center, uncovered[j], radius)) score++;
                if (!SeaPlatformGeometry.Supports(center, target, radius)) score /= 4;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best.IsValid;
        }

        /// <summary>
        /// 3×3 佔地全是高空或甲板、沒有建築、不壓在結構範圍上，而且支柱自己開出的平台（佔地外一圈）會碰到這塊甲板，
        /// 支柱才接得上它要撐的平台。
        /// </summary>
        private bool ValidSpot(Map map, SeaPlatformMapExtension ext, IntVec3 c, HashSet<IntVec3> island, List<CellRect> structures)
        {
            CellRect rect = GenAdj.OccupiedRect(c, Rot4.North, pillarDef.size);
            if (!rect.ExpandedBy(1).InBounds(map)) return false;
            foreach (IntVec3 x in rect)
            {
                TerrainDef t = map.terrainGrid.BaseTerrainAt(x);
                if (t != ext.voidTerrain && t != ext.deckTerrain) return false;
                if (x.GetEdifice(map) != null || x.GetFirstItem(map) != null) return false;
            }
            for (int i = 0; i < structures.Count; i++)
                if (structures[i].Overlaps(rect)) return false;
            foreach (IntVec3 x in rect.ExpandedBy(1))
                if (island.Contains(x)) return true;
            return false;
        }
    }

    /// <summary><see cref="SitePartWorker_SeaLaunchSite"/> 的設定（掛在 SitePartDef）：哪些派系能部署。</summary>
    public class SeaLaunchSiteFactionExtension : DefModExtension
    {
        public List<FactionDef> factions;
    }

    /// <summary>海上發射站：只給 <see cref="SeaLaunchSiteFactionExtension.factions"/> 列出的派系（殖民艦隊）。</summary>
    public class SitePartWorker_SeaLaunchSite : SitePartWorker_MissileBase
    {
        public override bool FactionCanOwn(Faction faction)
        {
            List<FactionDef> allowed = def.GetModExtension<SeaLaunchSiteFactionExtension>()?.factions;
            return base.FactionCanOwn(faction) && faction != null && (allowed.NullOrEmpty() || allowed.Contains(faction.def));
        }

        public override string GetArrivedLetterPart(Map map, out LetterDef preferredLetterDef, out LookTargets lookTargets)
        {
            base.GetArrivedLetterPart(map, out preferredLetterDef, out lookTargets);
            return "DMSE.SeaLaunchSite.ArrivedLetter".Translate();
        }
    }

    /// <summary>
    /// 殖民艦隊在殖民地附近的海面部署海上發射站。條件與野戰發射陣地相同（研究、敵對派系、站點數上限），
    /// 只是落點改成無人占用的水域 tile（世界地圖上以 tile 步數向外找，<see cref="FieldLaunchSiteIncidentExtension.distance"/> 內，且在站點射程內）。
    /// 殖民地附近沒有海就不會觸發。
    /// </summary>
    public class IncidentWorker_SeaLaunchSite : IncidentWorker_FieldLaunchSite
    {
        protected override string TileHint => "; a sea launch station needs an empty water tile (ocean or lake) that close to the colony";

        protected override bool TryFindTile(FieldLaunchSiteIncidentExtension ext, Map map, out PlanetTile tile)
        {
            int range = SiteRange(ext);
            PlanetTile home = map.Tile;
            List<PlanetTile> candidates = new List<PlanetTile>();
            home.Layer.Filler.FloodFill(home, t => true, (PlanetTile from, PlanetTile t, int dist) =>
            {
                if (dist > ext.distance.max) return true;
                if (dist >= ext.distance.min && t.Tile.WaterCovered && !Find.WorldObjects.AnyWorldObjectAt(t)
                    && (range <= 0 || MissileWorldUtility.RangeDistance(home, t) <= range))
                    candidates.Add(t);
                return false;
            });
            return candidates.TryRandomElement(out tile);
        }
    }
}
