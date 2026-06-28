using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈陣地霧化生成步驟。<br/>
    /// 先將整張地圖完全霧化，再從「可達地圖邊緣的開放格」進行洪水填充揭霧，
    /// 使所有非封閉的開放地形可見，而 FFF 結構的封閉房間（<c>MakeFog=true</c> 牆壁圍成的空間）
    /// 繼續保持霧化，玩家進入後才逐步揭開。<br/>
    /// <br/>
    /// 與原版 <see cref="GenStep_Fog"/> 的差異：<br/>
    /// ‧ 不存取 <see cref="MapGenerator.PlayerStartSpot"/>（NPC 陣地地圖無玩家出生點，存取會拋出例外）。<br/>
    /// ‧ 直接走原版 <c>UnfogMapFromEdge</c> 的邏輯路徑，以邊緣可達格為揭霧起點。<br/>
    /// ‧ 仍處理 <see cref="MapGenerator.rootsToUnfog"/>（任務指定揭霧格，對陣地通常為空）。<br/>
    /// <br/>
    /// 應排在 FFF 結構（order 400）與 Pawn 生成（order 450）之後（order 460），
    /// 確保牆壁已在地圖上，洪水填充能正確被建築阻擋；
    /// 且 Pawn 已落地，揭霧時 <see cref="FloodFillerFog.FloodUnfog"/> 能呼叫 <c>pawn.mindState.Active = true</c>。
    /// </summary>
    public class GenStep_MissileBaseFog : GenStep
    {
        public override int SeedPart => 0x4F574746; // "FOWG"

        public override void Generate(Map map, GenStepParams parms)
        {
            // 1. 全圖霧化
            map.fogGrid.Refog(map.BoundsRect());

            // 2. 從可達邊緣的開放格洪水揭霧（不封閉的地形全部可見）
            if (TryFindUnfogRoot(map, out IntVec3 startCell))
            {
                FloodFillerFog.FloodUnfog(startCell, map);
            }
            else
            {
                // 極端情況（全圖被屋頂覆蓋或無可達格）：回退到地圖中心
                Log.Warning("[DMSE GenStep_MissileBaseFog] 找不到合適的揭霧起點，回退到地圖中心。");
                FloodFillerFog.FloodUnfog(map.Center, map);
            }

            // 3. 處理任務指定揭霧格（rootsToUnfog，對 NPC 陣地通常為空，保留以防萬一）
            var roots = MapGenerator.rootsToUnfog;
            for (int i = 0; i < roots.Count; i++)
            {
                FloodFillerFog.FloodUnfog(roots[i], map);
                map.fogGrid.Unfog(roots[i]);
            }
        }

        /// <summary>
        /// 尋找適合作為揭霧起點的格子：可站立、無屋頂、能抵達地圖邊緣。<br/>
        /// 搜索優先順序：地圖中心附近 → 邊緣隨機格 → 全圖隨機。
        /// </summary>
        private static bool TryFindUnfogRoot(Map map, out IntVec3 result)
        {
            // 可站立、非屋頂、能不穿閉門走到地圖邊緣（等同原版 UnfogMapFromEdge 的篩選條件）
            bool Validator(IntVec3 c)
                => c.Standable(map)
                && !c.Roofed(map)
                && map.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.NoPassClosedDoorsOrWater));

            // 優先在中心附近找（FFF 結構通常置於中央，周邊仍有開放地形）
            if (CellFinder.TryFindRandomCellNear(map.Center, map, 30, Validator, out result))
                return true;

            // 次選：直接從邊緣格搜索
            if (CellFinder.TryFindRandomEdgeCellWith(Validator, map, 0f, out result))
                return true;

            // 最後：全圖隨機掃描
            if (CellFinder.TryFindRandomCell(map, Validator, out result))
                return true;

            result = IntVec3.Invalid;
            return false;
        }
    }
}
