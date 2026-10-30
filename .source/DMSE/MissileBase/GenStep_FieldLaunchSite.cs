using System.Collections.Generic;
using System.Linq;
using Fortified.Structures;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 野戰發射陣地的難度分級。依 Storyteller 的 <see cref="Difficulty.threatScale"/> 落在哪一段門檻決定等級，
    /// 結構數量與守軍人數都以這個等級查表。<br/>
    /// 結構步驟把結果存進 MapGenerator 變數 <see cref="TierVar"/>，守軍步驟沿用同一個等級。
    /// </summary>
    public static class FieldLaunchSiteDifficulty
    {
        public const string TierVar = "DMSE_FieldLaunchSiteTier";

        /// <summary>預設門檻：社區建設者／和平 → 0、冒險故事 → 1、生存挑戰 → 2、血與塵埃以上 → 3。</summary>
        public static readonly List<float> DefaultThresholds = new List<float> { 0.5f, 0.9f, 1.4f };

        public static float ThreatScale => Find.Storyteller?.difficulty?.threatScale ?? 1f;

        public static int TierFor(List<float> thresholds)
        {
            List<float> list = thresholds.NullOrEmpty() ? DefaultThresholds : thresholds;
            float scale = ThreatScale;
            int tier = 0;
            foreach (float t in list)
                if (scale >= t) tier++;
            return tier;
        }

        /// <summary>地圖生成中已決定的等級；沒有就依預設門檻重算。</summary>
        public static int CurrentTier()
        {
            if (MapGenerator.TryGetVar(TierVar, out int tier)) return tier;
            return TierFor(null);
        }

        /// <summary>依等級取表；等級超過表長時取最後一項。</summary>
        public static IntRange Pick(List<IntRange> byTier, int tier, IntRange fallback)
        {
            if (byTier.NullOrEmpty()) return fallback;
            return byTier[Mathf.Clamp(tier, 0, byTier.Count - 1)];
        }
    }

    /// <summary>野戰陣地的一類組件（雷達、發射陣位、彈藥車……）及其每個難度等級的數量與擺放半徑。</summary>
    public class FieldLaunchSitePieceGroup
    {
        public string label;

        /// <summary>候選 FFF 結構；同組內先輪完不重複的再重複。</summary>
        public List<FFF_StructureDef> pool;

        /// <summary>
        /// 每組第一座改從這裡抽（例如保證至少一座帶世界選靶發射器的陣位）；之後照常從 <see cref="pool"/> 抽。
        /// </summary>
        public List<FFF_StructureDef> firstPool;

        /// <summary>依難度等級（0 起）的數量。</summary>
        public List<IntRange> countByTier;

        /// <summary>離陣地中心的距離（格）。放不下時會逐步外擴到 1.5 倍。</summary>
        public FloatRange radius = new FloatRange(0f, 8f);

        public bool randomRotation = true;

        /// <summary>與已放結構之間至少留幾格。</summary>
        public int spacing = 3;
    }

    /// <summary>
    /// 工業等級派系的野戰發射陣地布局：以搜索雷達為中心，依難度等級向外散布發射陣位、火控雷達、彈藥車、
    /// 近防與休息區（皆為 <c>LaunchSite_Mid.xml</c> 的 FFF 結構）。<br/>
    /// 完成後把所有結構的外框設為 <c>RectOfInterest</c>，供守軍步驟（order 450）定位。
    /// </summary>
    public class GenStep_FieldLaunchSite : GenStep
    {
        public List<float> difficultyThresholds;

        /// <summary>依序擺放；第一組通常是中心雷達。</summary>
        public List<FieldLaunchSitePieceGroup> groups;

        /// <summary>陣地中心相對地圖中心的隨機偏移（格）。</summary>
        public int centerJitter = 6;

        /// <summary>
        /// 保留自然地形：不鋪結構的地板（混凝土、鋪面），且只放在每座建築都能直接蓋在原地面、沒有岩石的位置，
        /// 讓各結構散落在原有地貌之間，而不是把地形剷平。false = 沿用 FFF 的完整生成（含地板）。
        /// </summary>
        public bool keepNaturalTerrain = true;


        private const int AttemptsPerPiece = 120;

        public override int SeedPart => 0x2E4F1A37;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (groups.NullOrEmpty()) return;

            Faction faction = parms.sitePart?.site?.Faction ?? map.ParentFaction;
            int tier = FieldLaunchSiteDifficulty.TierFor(difficultyThresholds);
            MapGenerator.SetVar(FieldLaunchSiteDifficulty.TierVar, tier);

            CellRect usable = FFF_StructureUtility.UsableRect(map);
            IntVec3 origin = map.Center + new IntVec3(Rand.RangeInclusive(-centerJitter, centerJitter), 0,
                Rand.RangeInclusive(-centerJitter, centerJitter));

            List<CellRect> occupied = new List<CellRect>();
            int placedTotal = 0, wantedTotal = 0;

            foreach (FieldLaunchSitePieceGroup group in groups)
            {
                if (group?.pool.NullOrEmpty() != false) continue;
                int count = FieldLaunchSiteDifficulty.Pick(group.countByTier, tier, new IntRange(1, 1)).RandomInRange;
                wantedTotal += count;

                List<FFF_StructureDef> bag = new List<FFF_StructureDef>();
                for (int i = 0; i < count; i++)
                {
                    if (bag.Count == 0) bag.AddRange(group.pool.Where(d => d != null));
                    if (bag.Count == 0) break;
                    FFF_StructureDef def = i == 0 && !group.firstPool.NullOrEmpty()
                        ? group.firstPool.Where(d => d != null).RandomElementWithFallback() ?? bag.RandomElement()
                        : bag.RandomElement();
                    bag.Remove(def);

                    if (TryPlace(map, def, group, origin, usable, occupied, faction, keepNaturalTerrain, out CellRect placed))
                    {
                        // 第一座（中心雷達）落在哪，其餘各組就繞著它散布。
                        if (placedTotal == 0) origin = placed.CenterCell;
                        placedTotal++;
                    }
                    else if (Prefs.DevMode)
                        Log.Message($"[DMSE FieldLaunchSite] {group.label ?? "group"}: no room for {def.defName}; skipped.");
                }
            }

            // 道路由 GenStep_FieldLaunchSiteRoads 在原版遺跡、地標之後鋪設（否則會被它們的地板蓋掉）。
            MapGenerator.SetVar(FieldLaunchSiteRoads.RectsVar, new List<CellRect>(occupied));

            if (occupied.Count > 0)
            {
                CellRect bounds = occupied[0];
                foreach (CellRect r in occupied) bounds = bounds.Encapsulate(r);
                MapGenerator.SetVar("RectOfInterest", bounds);
            }

            if (Prefs.DevMode)
                Log.Message($"[DMSE FieldLaunchSite] threatScale={FieldLaunchSiteDifficulty.ThreatScale:0.##} tier={tier}："
                    + $"placed {placedTotal}/{wantedTotal} structures.");
        }

        private static bool TryPlace(Map map, FFF_StructureDef def, FieldLaunchSitePieceGroup group, IntVec3 origin,
            CellRect usable, List<CellRect> occupied, Faction faction, bool keepNaturalTerrain, out CellRect placed)
        {
            placed = default;
            Dictionary<Rot4, Sketch> sketches = new Dictionary<Rot4, Sketch>();
            for (int attempt = 0; attempt < AttemptsPerPiece; attempt++)
            {
                // 前半段在設定半徑內找，後半段逐步外擴，避免大地形障礙或小地圖時整組放不下。
                float grow = attempt < AttemptsPerPiece / 2 ? 1f : Mathf.Lerp(1f, 1.5f, (attempt - AttemptsPerPiece / 2f) / (AttemptsPerPiece / 2f));
                float r = Rand.Range(group.radius.min, group.radius.max * grow);
                float angle = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                IntVec3 center = origin + new IntVec3(Mathf.RoundToInt(Mathf.Cos(angle) * r), 0, Mathf.RoundToInt(Mathf.Sin(angle) * r));

                Rot4 rot = group.randomRotation ? Rot4.Random : Rot4.North;
                if (!sketches.TryGetValue(rot, out Sketch sketch))
                {
                    sketch = def.GetSketch();
                    if (rot != Rot4.North) sketch.Rotate(rot);
                    sketches[rot] = sketch;
                }
                IntVec3 offset = center - sketch.OccupiedRect.CenterCell;
                CellRect rect = sketch.OccupiedRect.MovedBy(offset);
                if (rect.Width <= 0 || rect.Height <= 0) return false;
                if (rect.minX < usable.minX || rect.maxX > usable.maxX || rect.minZ < usable.minZ || rect.maxZ > usable.maxZ) continue;
                if (occupied.Any(o => o.ExpandedBy(group.spacing).Overlaps(rect))) continue;
                if (FFF_StructureUtility.OverlapsUsedRect(map, rect)) continue;
                if (keepNaturalTerrain && !FitsNaturalGround(map, sketch, offset)) continue;

                if (keepNaturalTerrain)
                    SpawnKeepingTerrain(map, def, sketch, offset, rot, faction);
                else
                    // reconnectPower: false —— 生成期由遊戲在結束後自行建立電網。
                    FFF_StructureUtility.Generate(def, center, map, faction, rot, reconnectPower: false);
                occupied.Add(rect);
                FFF_StructureUtility.ReserveUsedRect(map, rect, 1);
                placed = rect;
                return true;
            }
            return false;
        }

        /// <summary>每座建築都能直接蓋在原地形上（不需改鋪地板），且占用格沒有岩石或其他建築。</summary>
        private static bool FitsNaturalGround(Map map, Sketch sketch, IntVec3 offset)
        {
            foreach (SketchThing st in sketch.Things)
            {
                IntVec3 pos = st.pos + offset;
                foreach (IntVec3 c in GenAdj.OccupiedRect(pos, st.rot, st.def.size))
                {
                    if (!c.InBounds(map) || c.GetEdifice(map) != null) return false;
                }
                if (st.def.category == ThingCategory.Building && !st.CanBuildOnTerrain(pos, map)) return false;
            }
            return true;
        }

        /// <summary>
        /// 只生成結構的建築、守衛與屋頂等任務，略過 sketch 地板，保留原本的土壤、草地與植被（壓在建築下的植物除外）。
        /// </summary>
        private static void SpawnKeepingTerrain(Map map, FFF_StructureDef def, Sketch sketch, IntVec3 offset, Rot4 rot, Faction faction)
        {
            FFF_StructureUtility.SpawnThings(map, sketch, offset, faction);
            FFF_StructureUtility.SpawnPawns(def, rot, offset, map, faction);
            List<IFFF_GenerationTask> tasks = def.GetTasks(rot, offset);
            if (tasks == null) return;
            foreach (IFFF_GenerationTask task in tasks)
            {
                // 地形類任務同樣會改寫地面；保留自然地形時一併略過。
                if (task is Task_ApplyTerrainLayer || task is Task_ApplyTerrainColor) continue;
                try { task.Execute(map, IntVec3.Zero, faction); }
                catch (System.Exception e) { Log.Error($"[DMSE FieldLaunchSite] {def.defName}: task {task.GetType().Name} failed: {e}"); }
            }
        }
    }

    /// <summary>
    /// 野戰發射陣地守軍：人數 = 威脅點數換算值，夾在該難度等級的 <see cref="crewByTier"/> 範圍內，且精確生成這個人數。
    /// </summary>
    public class GenStep_FieldLaunchSiteCrew : GenStep_MissileBaseCrew
    {
        public List<IntRange> crewByTier;

        public override int SeedPart => 0x3B6D2C51;

        protected override bool ExactCrewCount => true;

        protected override int ResolveCrewCount(GenStepParams parms)
        {
            int fromPoints = base.ResolveCrewCount(parms);
            if (crewByTier.NullOrEmpty()) return fromPoints;

            IntRange range = FieldLaunchSiteDifficulty.Pick(crewByTier, FieldLaunchSiteDifficulty.CurrentTier(), new IntRange(minCrew, maxCrew));
            return Mathf.Clamp(fromPoints, range.min, range.max);
        }
    }

    /// <summary>野戰發射陣地：只給工業等級派系。</summary>
    public class SitePartWorker_FieldLaunchSite : SitePartWorker_MissileBase
    {
        public override bool FactionCanOwn(Faction faction)
            => base.FactionCanOwn(faction) && faction != null && faction.def.techLevel == TechLevel.Industrial;

        public override string GetArrivedLetterPart(Map map, out LetterDef preferredLetterDef, out LookTargets lookTargets)
        {
            base.GetArrivedLetterPart(map, out preferredLetterDef, out lookTargets);
            return "DMSE.FieldLaunchSite.ArrivedLetter".Translate();
        }
    }
}
