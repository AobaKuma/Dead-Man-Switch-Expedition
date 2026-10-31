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
            return VoidTerrain(map?.Tile.LayerDef);
        }

        public static MapGeneratorDef ShipMapGenerator(PlanetLayerDef layer)
        {
            return For(layer)?.shipMapGenerator ?? DMSE_DefOf.DMSE_ShipVoid;
        }

        public static WorldObjectDef ShipWorldObject(PlanetLayerDef layer)
        {
            return For(layer)?.shipWorldObject ?? DMSE_DefOf.DMSE_TransferShip;
        }
    }
}
