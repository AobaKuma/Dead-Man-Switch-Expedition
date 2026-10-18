using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈／火砲世界航跡的共用規則：
    ///   - 射程距離一律以「地表格」為單位（兩點球面夾角換算），跨圖層時另加 <see cref="CrossLayerRangeCost"/>。
    ///     不直接用 <c>WorldGrid.ApproxDistanceInTiles(PlanetTile, PlanetTile)</c>：它以第一個 tile 的圖層
    ///     解讀第二個 tile 的編號，兩者不同圖層時算出的是錯誤距離（軌道層讀地表編號還會越界報錯）。
    ///   - 跨圖層打擊（地表 ↔ 軌道）必須具備導引能力；慣性制導與無導引彈藥不行。
    ///   - 抵達時找不到目標就落空，不在空格上生成營地地圖。
    /// </summary>
    public static class MissileWorldUtility
    {
        /// <summary>跨越圖層額外消耗的射程（地表格）。</summary>
        public const float CrossLayerRangeCost = 10f;

        /// <summary>導引頭檢查目標位置的間隔（tick）。</summary>
        public const int TerminalCheckIntervalTicks = 60;

        public static bool SameLayer(PlanetTile a, PlanetTile b)
        {
            return a.Valid && b.Valid && a.Layer == b.Layer;
        }

        public static Vector3 TileCenter(PlanetTile tile)
        {
            return tile.Valid ? Find.WorldGrid.GetTileCenter(tile) : Vector3.zero;
        }

        /// <summary>兩個世界座標之間的地表等效格數（不含圖層成本）。</summary>
        public static float SurfaceTilesBetween(Vector3 a, Vector3 b)
        {
            if (a == Vector3.zero || b == Vector3.zero) { return 0f; }
            return Find.WorldGrid.ApproxDistanceInTiles(GenMath.SphericalDistance(a.normalized, b.normalized));
        }

        /// <summary>射程距離：地表等效格數，跨圖層時加上 <see cref="CrossLayerRangeCost"/>。</summary>
        public static float RangeDistance(PlanetTile from, PlanetTile to)
        {
            if (!from.Valid || !to.Valid) { return 0f; }
            float distance = SurfaceTilesBetween(TileCenter(from), TileCenter(to));
            if (!SameLayer(from, to)) { distance += CrossLayerRangeCost; }
            return distance;
        }

        /// <summary>
        /// 導彈能否從 <paramref name="from"/> 打擊 <paramref name="to"/>。
        /// 跨圖層必須具備導引能力；距離不得超過導彈射程（<see cref="MissileConfig.Range"/> 為 0 表示不限）
        /// 與 <paramref name="maxRange"/>（發射方額外上限，≤ 0 表示不限）。
        /// </summary>
        public static bool CanEngage(MissileConfig config, PlanetTile from, PlanetTile to, float maxRange, out string reason)
        {
            reason = null;
            if (!from.Valid || !to.Valid)
            {
                reason = "MessageTransportPodsDestinationIsInvalid".Translate();
                return false;
            }
            if (!SameLayer(from, to) && (config == null || !config.HasTerminalGuidance))
            {
                reason = "DMSE.Missile.CrossLayerNeedsGuidance".Translate();
                return false;
            }
            float distance = RangeDistance(from, to);
            int missileRange = config != null ? config.Range : 0;
            if (missileRange > 0 && distance > missileRange)
            {
                reason = "DMSE.MissileLauncher.OutOfRange".Translate(missileRange);
                return false;
            }
            if (maxRange > 0f && distance > maxRange)
            {
                reason = "DMSE.MissileLauncher.OutOfRange".Translate(Mathf.RoundToInt(maxRange));
                return false;
            }
            return true;
        }

        /// <summary>
        /// 解析抵達時的目標地圖；回傳 null 表示落空。
        ///   - <paramref name="targetParent"/> 不為 null：目標必須仍在 <paramref name="destination"/> 上，否則落空。
        ///   - 為 null：取該 tile 上既有的地圖或 MapParent；什麼都沒有就落空（不生成營地地圖）。
        ///   - <paramref name="allowGenerateMap"/>：目標存在但尚未生成地圖時（例：敵方據點）是否生成其地圖。
        /// </summary>
        public static Map ResolveArrivalMap(PlanetTile destination, MapParent targetParent, bool allowGenerateMap)
        {
            if (!destination.Valid) { return null; }

            MapParent parent = targetParent;
            if (parent != null)
            {
                if (parent.Destroyed || parent.Tile != destination) { return null; }
            }
            else
            {
                Map existing = Current.Game.FindMap(destination);
                if (existing != null) { return existing; }
                parent = Find.WorldObjects.MapParentAt(destination);
                if (parent == null) { return null; }
            }

            if (parent.HasMap) { return parent.Map; }
            return allowGenerateMap ? GetOrGenerateMapUtility.GetOrGenerateMap(destination, null) : null;
        }
    }
}
