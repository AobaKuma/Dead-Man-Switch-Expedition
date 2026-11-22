using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 海上平台地圖（2026-11-23 從 DepthRim 的海面地圖簡化而來）：掛在 <see cref="MapGeneratorDef"/> 上。
    /// <para>和 DepthRim 不同，這裡只有一張地圖（DepthRim 的「平台層」），沒有可以走動的水面層：
    /// 整張地圖的「空」是高空 <see cref="voidTerrain"/>（不畫、不可通行），往下 <see cref="seaDrop"/> 格看到的海由
    /// <see cref="SectionLayer_SeaPlatformSea"/> 畫。玩家站在支柱（<see cref="Building_SeaPlatformPillar"/>）撐起的平台上。</para>
    /// <para>沒有的東西：水面層、往下看的快照、浮船、舷梯、跨層電力、惡劣海況天氣、航跡、鏡頭擺動。</para>
    /// </summary>
    public class SeaPlatformMapExtension : DefModExtension
    {
        /// <summary>高空：平台以外的地形。拆平台、平台崩塌、船體分離後都鋪回這個。</summary>
        public TerrainDef voidTerrain;

        /// <summary>支柱撐起的甲板（支撐判定只看這種地形）。</summary>
        public TerrainDef deckTerrain;

        /// <summary>甲板離海面幾格（畫面上海往南偏的量）。要和柱身的入水處對齊（<see cref="SeaPlatformPillarExtension"/>）。</summary>
        public float seaDrop = 14.1f;

        public override System.Collections.Generic.IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (voidTerrain == null || deckTerrain == null)
            {
                yield return "voidTerrain and deckTerrain are required";
            }
        }
    }

    /// <summary>海上平台地圖的共用判斷。</summary>
    public static class SeaPlatformUtility
    {
        /// <summary>這張地圖的海上平台設定（不是海上平台地圖時 null）。</summary>
        public static SeaPlatformMapExtension Ext(Map map)
        {
            return map?.generatorDef?.GetModExtension<SeaPlatformMapExtension>();
        }

        public static bool IsSeaPlatformMap(Map map)
        {
            return Ext(map) != null;
        }

        /// <summary>這一格看得到下方的海（地形是高空）。</summary>
        public static bool ShowsSea(IntVec3 c, Map map, SeaPlatformMapExtension ext)
        {
            return ext != null && c.InBounds(map) && map.terrainGrid.TerrainAt(c) == ext.voidTerrain;
        }

        public static bool IsDeck(IntVec3 c, Map map, SeaPlatformMapExtension ext)
        {
            return ext != null && c.InBounds(map) && map.terrainGrid.BaseTerrainAt(c) == ext.deckTerrain;
        }
    }

    /// <summary>海上平台地點：有玩家建築、擋住移除的 Pawn，或玩家正在看時保留地圖。</summary>
    public class WorldObject_SeaPlatformSite : MapParent
    {
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = true;
            if (!HasMap)
            {
                return false;
            }
            return Map.listerBuildings.allBuildingsColonist.Count == 0 && !Map.mapPawns.AnyPawnBlockingMapRemoval && Find.CurrentMap != Map;
        }
    }

    /// <summary>
    /// 海上平台地圖生成：整張鋪高空；<see cref="platformSize"/> &gt; 0 時在中央鋪一塊起始平台，中間放一根支柱撐住它。
    /// </summary>
    public class GenStep_SeaPlatform : GenStep
    {
        public IntVec2 platformSize = new IntVec2(13, 13);

        /// <summary>起始平台中間的支柱；null 時不放（平台會在第一次檢查時因為沒有支撐而崩塌）。</summary>
        public ThingDef pillarDef;

        public override int SeedPart => 0x5EA9A7;

        public override void Generate(Map map, GenStepParams parms)
        {
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(map);
            if (ext == null)
            {
                Log.Error($"[DMSE] GenStep_SeaPlatform used by {map.generatorDef?.defName}, which has no SeaPlatformMapExtension.");
                return;
            }
            TerrainGrid grid = map.terrainGrid;
            foreach (IntVec3 c in map.AllCells)
            {
                grid.SetTerrain(c, ext.voidTerrain);
            }
            if (platformSize.x <= 0 || platformSize.z <= 0)
            {
                return;
            }
            CellRect rect = CellRect.CenteredOn(map.Center, platformSize.x, platformSize.z).ClipInsideMap(map);
            foreach (IntVec3 c in rect)
            {
                grid.SetTerrain(c, ext.deckTerrain);
            }
            if (pillarDef != null)
            {
                Thing pillar = ThingMaker.MakeThing(pillarDef);
                pillar.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(pillar, rect.CenterCell, map, Rot4.North);
            }
        }
    }

    public static class DebugActions_SeaPlatform
    {
        /// <summary>在點到的地表水域格建立海上平台地點並切過去（測試用；正式的前往方式由使用這套系統的內容決定）。</summary>
        [DebugAction("DMSE", "Create sea platform map", actionType = DebugActionType.ToolWorld, allowedGameStates = AllowedGameStates.PlayingOnWorld)]
        public static void CreateSeaPlatformMap()
        {
            PlanetTile tile = GenWorld.MouseTile();
            if (!tile.Valid || !tile.Tile.WaterCovered)
            {
                Messages.Message("DMSE.SeaPlatform.NeedWaterTile".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (Find.WorldObjects.AnyMapParentAt(tile))
            {
                Messages.Message("DMSE.SeaPlatform.TileOccupied".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            WorldObjectDef siteDef = DefDatabase<WorldObjectDef>.GetNamed("DMSE_SeaPlatformSite");
            MapParent parent = (MapParent)WorldObjectMaker.MakeWorldObject(siteDef);
            parent.Tile = tile;
            parent.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(parent);
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, Find.World.info.initialMapSize, siteDef);
            Current.Game.CurrentMap = map;
            CameraJumper.TryJump(map.Center, map);
        }
    }
}
