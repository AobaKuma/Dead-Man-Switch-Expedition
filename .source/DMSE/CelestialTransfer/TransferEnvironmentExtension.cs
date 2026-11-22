using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 掛在 <see cref="PlanetLayerDef"/> 上，讓非太空圖層也能使用轉移飛行（例如其他模組的水下圖層）。
    /// 未掛此擴充的太空圖層沿用預設：虛空 = <see cref="TerrainDefOf.Space"/>、船體地圖 = DMSE_ShipVoid、父物件 = DMSE_TransferShip。
    /// 未掛此擴充的非太空圖層不能起飛。
    /// </summary>
    public class TransferEnvironmentExtension : DefModExtension
    {
        /// <summary>此圖層的「虛空」地形：不算入船體、分離後原地圖留下的地形、船體地圖的底色。null = Space。</summary>
        public TerrainDef voidTerrain;

        /// <summary>船體地圖的生成器；應只鋪 <see cref="voidTerrain"/>。null = DMSE_ShipVoid。</summary>
        public MapGeneratorDef shipMapGenerator;

        /// <summary>船的世界物件；worldObjectClass 必須是 <see cref="WorldObject_TransferShip"/> 或其子類。null = DMSE_TransferShip。</summary>
        public WorldObjectDef shipWorldObject;

        /// <summary>航線：距離、路徑與額外的目的地檢查；null = 太空的直線（<see cref="TransferRouteWorker"/> 的預設）。</summary>
        public TransferRouteWorker routeWorker;

        /// <summary>能源：出發時收多少燃料、規劃估算怎麼調整；null = DMSE 的燃料公式。</summary>
        public TransferEnergyWorker energyWorker;

        /// <summary>降落：額外的不能降落條件；null = 沒有。</summary>
        public TransferLandingWorker landingWorker;

        /// <summary>演出：音效、震動、火花、尾焰傷害、離場動畫；null = 太空船（<see cref="TransferPresentation.Space"/>）。</summary>
        public TransferPresentation presentation;

        /// <summary>
        /// 分離時把船體放到船體地圖正中央（預設保留原地圖座標）。船體貼著原地圖邊緣時，船體地圖上看得到的四周會一邊寬一邊窄；
        /// 例如會把船外畫成一片水域的圖層，置中後四周看起來才一樣。
        /// </summary>
        public bool centerHullOnShipMap;

        /// <summary>
        /// 這個圖層的地圖也畫地形崖壁（原版 <see cref="SectionLayer_TerrainEdges"/>，地形的 <c>spaceEdgeGraphicData</c>）。
        /// 原版只在太空圖層畫；例如虛空是深淵的水下圖層要讓海床邊緣也有崖壁。見 <see cref="Patch_TerrainEdgesOnTransferLayers"/>。
        /// </summary>
        public bool drawTerrainEdges;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (shipWorldObject != null && !typeof(WorldObject_TransferShip).IsAssignableFrom(shipWorldObject.worldObjectClass))
            {
                yield return $"shipWorldObject {shipWorldObject.defName} must use {nameof(WorldObject_TransferShip)} or a subclass as worldObjectClass.";
            }
            if (presentation != null)
            {
                foreach (string e in presentation.ConfigErrors())
                {
                    yield return e;
                }
            }
        }
    }

    /// <summary>依圖層取得轉移飛行的環境設定（<see cref="TransferEnvironmentExtension"/>）。</summary>
    public static class TransferEnvironment
    {
        public static TransferEnvironmentExtension For(PlanetLayerDef layer)
        {
            return layer?.GetModExtension<TransferEnvironmentExtension>();
        }

        /// <summary>此圖層可以起飛：太空圖層，或掛了 <see cref="TransferEnvironmentExtension"/>。</summary>
        public static bool AllowsTransfer(PlanetLayerDef layer)
        {
            return layer != null && (layer.isSpace || For(layer) != null);
        }

        public static bool AllowsTransfer(Map map)
        {
            return map != null && AllowsTransfer(map.Tile.LayerDef);
        }

        public static TerrainDef VoidTerrain(PlanetLayerDef layer)
        {
            return For(layer)?.voidTerrain ?? TerrainDefOf.Space;
        }

        public static TerrainDef VoidTerrain(Map map)
        {
            // 海上平台地圖（SeaPlatformMapExtension）的「空」是高空，不看圖層：拆平台、船體分離都鋪回高空
            return SeaPlatformUtility.Ext(map)?.voidTerrain ?? VoidTerrain(map?.Tile.LayerDef);
        }

        public static MapGeneratorDef ShipMapGenerator(PlanetLayerDef layer)
        {
            return For(layer)?.shipMapGenerator ?? DMSE_DefOf.DMSE_ShipVoid;
        }

        public static WorldObjectDef ShipWorldObject(PlanetLayerDef layer)
        {
            return For(layer)?.shipWorldObject ?? DMSE_DefOf.DMSE_TransferShip;
        }

        public static TransferRouteWorker RouteWorker(PlanetTile tile) => tile.Valid ? For(tile.LayerDef)?.routeWorker : null;

        public static TransferEnergyWorker EnergyWorker(Map map) => map != null && map.Tile.Valid ? For(map.Tile.LayerDef)?.energyWorker : null;

        public static TransferLandingWorker LandingWorker(PlanetTile tile) => tile.Valid ? For(tile.LayerDef)?.landingWorker : null;

        /// <summary>這張地圖所在圖層的演出（沒掛時是太空船的預設）。</summary>
        public static bool CenterHullOnShipMap(PlanetLayerDef layer) => For(layer)?.centerHullOnShipMap ?? false;

        /// <summary>這個圖層的地圖畫地形崖壁：原版的太空圖層，加上設了 <see cref="TransferEnvironmentExtension.drawTerrainEdges"/> 的圖層。</summary>
        public static bool DrawsTerrainEdges(PlanetLayerDef layer) => layer != null && (layer.isSpace || (For(layer)?.drawTerrainEdges ?? false));

        public static TransferPresentation Presentation(Map map) => (map != null && map.Tile.Valid ? For(map.Tile.LayerDef)?.presentation : null) ?? TransferPresentation.Space;
    }

    /// <summary>
    /// 轉移飛行的航線（掛在圖層的 <see cref="TransferEnvironmentExtension.routeWorker"/>；XML 用 <c>Class=</c>）。
    /// 預設與太空相同；子類別只覆寫需要的部分（例如海洋層只走海洋格）。
    /// </summary>
    public class TransferRouteWorker
    {
        /// <summary>兩格的航行距離（格）。到不了時回傳很大的數，超過任何航程。</summary>
        public virtual int Distance(PlanetTile from, PlanetTile to)
        {
            return Find.WorldGrid.TraversalDistanceBetween(from, to, passImpassable: true);
        }

        /// <summary>航線上的格子（含兩端）；回傳 null 用 DMSE 的貪婪鄰格路徑。</summary>
        public virtual List<PlanetTile> BuildPath(PlanetTile from, PlanetTile to)
        {
            return null;
        }

        /// <summary>額外的目的地檢查，在 DMSE 的射程檢查之前；回傳 null 表示可以，否則是要顯示的訊息（已翻譯）。</summary>
        public virtual string DestinationBlockReason(PlanetTile origin, PlanetTile tile)
        {
            return null;
        }
    }

    /// <summary>
    /// 轉移飛行的能源（<see cref="TransferEnvironmentExtension.energyWorker"/>）：出發時收多少燃料（整備檢查與出發共用），
    /// 以及規劃畫面的估算（例如改成以引擎耗電計的航程）。
    /// </summary>
    public class TransferEnergyWorker
    {
        public virtual float FuelRequired(TransferShipSurvey survey, int distance)
        {
            return TransferFlightMath.FuelRequired(survey.MassWeight, survey.Eta, distance);
        }

        /// <summary>DMSE 算完估算後呼叫，可以改寫 <see cref="TransferEstimate.maxDistance"/>、<see cref="TransferEstimate.fuelRequired"/> 等欄位。</summary>
        public virtual void AdjustEstimate(TransferShipSurvey survey, TransferEstimate estimate)
        {
        }
    }

    /// <summary>轉移飛行的降落限制（<see cref="TransferEnvironmentExtension.landingWorker"/>），掛在目的地的圖層上。</summary>
    public class TransferLandingWorker
    {
        /// <summary>回傳 null 表示可以降落，否則是要顯示的訊息（已翻譯）。</summary>
        public virtual string LandingBlockReason(WorldObject_TransferShip ship, PlanetTile target)
        {
            return null;
        }
    }
}
