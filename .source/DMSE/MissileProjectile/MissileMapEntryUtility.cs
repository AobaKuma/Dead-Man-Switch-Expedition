using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 世界航跡方位 ↔ 地圖邊緣進出點（docs/Missile_Skyfaller轉Projectile_計畫.md §5）。
    ///
    /// 方位一律用原版 <see cref="PlanetLayer.GetHeadingFromTo(Vector3, Vector3)"/>（0 = 北、順時針，北方由圖層的北極決定），
    /// 與原版商隊離圖（<c>WorldGrid.GetRotFromTo</c> → <c>Pawn.ExitMap(exitDir)</c>）同一套慣例：
    /// heading 0 對應地圖 +Z 邊、90 對應 +X 邊。地圖格座標的方向向量 = (sin h, 0, cos h)。
    ///
    /// 原版 skyfaller 的 <c>angle</c> 也是同一套羅盤角：<c>SkyfallerDrawPosUtility.PosAtDist</c> 的位移方向
    /// <c>FromAngleFlat(angle - 90)</c> = (sin angle, 0, cos angle)，所以「從方位 h 飛來」的墜落角就是 h。
    /// </summary>
    public static class MissileMapEntryUtility
    {
        /// <summary>兩點在球心的夾角小於此值（度）時視為正上方，沒有水平方位，退回垂直墜落。</summary>
        public const float MinHorizontalSeparationDegrees = 0.05f;

        /// <summary>地圖格座標中，方位 <paramref name="heading"/> 的單位向量（x = 東、z = 北）。</summary>
        public static Vector3 HeadingVector(float heading)
        {
            float rad = heading * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        }

        /// <summary>向量 (x, z) 的方位角（0 = 北、順時針，0–360）。</summary>
        public static float HeadingOf(Vector3 dir)
        {
            float h = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            return (h % 360f + 360f) % 360f;
        }

        /// <summary>
        /// 世界上 <paramref name="from"/> → <paramref name="to"/> 是否有可用的水平方位：
        /// 同一圖層且兩點在球心的夾角夠大。跨圖層（軌道 → 地表）或幾乎重合時回傳 false。
        /// </summary>
        public static bool HasHorizontalHeading(PlanetLayer fromLayer, PlanetLayer toLayer, Vector3 from, Vector3 to)
        {
            if (fromLayer == null || toLayer == null || fromLayer != toLayer) { return false; }
            if (from == Vector3.zero || to == Vector3.zero) { return false; }
            return Vector3.Angle(from, to) >= MinHorizontalSeparationDegrees;
        }

        /// <summary>
        /// 進入方位：在目標點 <paramref name="end"/>，指向世界航跡最後一段起點 <paramref name="lastLegStart"/> 的方位
        /// （導彈從這個方向飛來）。有終端更新時必須傳改飛後那一段的起點。
        /// </summary>
        public static float EntryHeading(PlanetLayer layer, Vector3 lastLegStart, Vector3 end)
        {
            return layer.GetHeadingFromTo(end, lastLegStart);
        }

        /// <summary>出發方位：在發射點 <paramref name="start"/>，指向目標 <paramref name="end"/> 的方位。</summary>
        public static float DepartureHeading(PlanetLayer layer, Vector3 start, Vector3 end)
        {
            return layer.GetHeadingFromTo(start, end);
        }

        /// <summary>
        /// 從 <paramref name="from"/>（地圖格座標，需在地圖內）沿 <paramref name="heading"/> 射出，與地圖外框
        /// [0, size.x] × [0, size.z] 的交點（精確點，不取整到四邊）。
        /// </summary>
        public static Vector3 EdgePoint(IntVec3 mapSize, Vector3 from, float heading)
        {
            Vector3 d = HeadingVector(heading);
            float t = float.PositiveInfinity;
            if (d.x > 1e-6f) { t = Mathf.Min(t, (mapSize.x - from.x) / d.x); }
            else if (d.x < -1e-6f) { t = Mathf.Min(t, -from.x / d.x); }
            if (d.z > 1e-6f) { t = Mathf.Min(t, (mapSize.z - from.z) / d.z); }
            else if (d.z < -1e-6f) { t = Mathf.Min(t, -from.z / d.z); }
            if (float.IsInfinity(t) || t < 0f) { t = 0f; }
            Vector3 p = from + d * t;
            p.x = Mathf.Clamp(p.x, 0f, mapSize.x);
            p.z = Mathf.Clamp(p.z, 0f, mapSize.z);
            p.y = from.y;
            return p;
        }

        /// <summary>把邊緣點往地圖內縮 <paramref name="inset"/> 格，作為可生成（InBounds）的位置。</summary>
        public static Vector3 InsetFromEdge(IntVec3 mapSize, Vector3 edge, float inset = 0.5f)
        {
            edge.x = Mathf.Clamp(edge.x, inset, mapSize.x - inset);
            edge.z = Mathf.Clamp(edge.z, inset, mapSize.z - inset);
            return edge;
        }

        /// <summary>落點從方位 <paramref name="heading"/> 飛來時的 skyfaller 墜落角（同一套羅盤角，見類別說明）。</summary>
        public static float SkyfallerAngleForHeading(float heading) => heading;

        /// <summary>方位對應的地圖邊（與原版 <c>PlanetLayer.GetRotFromTo</c> 的分界相同）。</summary>
        public static Rot4 EdgeRot(float heading)
        {
            heading = (heading % 360f + 360f) % 360f;
            if (heading >= 315f || heading < 45f) { return Rot4.North; }
            if (heading < 135f) { return Rot4.East; }
            if (heading < 225f) { return Rot4.South; }
            return Rot4.West;
        }
    }
}
