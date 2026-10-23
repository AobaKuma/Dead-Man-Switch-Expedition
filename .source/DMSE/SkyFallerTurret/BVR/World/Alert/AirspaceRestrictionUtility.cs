using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>一個正在執行中的防空封鎖圈（§6.1）。</summary>
    public class BlockadeZone
    {
        public MapParent site;
        public Faction faction;
        public PlanetTile tile;
        public Vector3 center;
        public float radius;
    }

    /// <summary>航行查詢結果：是否受限、封鎖者、原因與範圍。</summary>
    public struct AirspaceRestriction
    {
        public bool Restricted;
        public BlockadeZone zone;
        public string Reason;

        public static AirspaceRestriction None => default;
    }

    /// <summary>
    /// 統一的航行許可查詢（§6），供穿梭機、運輸艙、重力船、轉移艦、NPC 空投與介面共用。
    /// 第一版是提交前航行管制：進入受限圈的航線不可提交；已起飛的載具沿既有核准航線完成旅程。
    /// </summary>
    public static class AirspaceRestrictionUtility
    {
        private static readonly List<BlockadeZone> zones = new List<BlockadeZone>();
        private static int cacheTick = -1;

        /// <summary>階段、關係或設備改變時呼叫；下一次查詢重建封鎖圈。</summary>
        public static void Invalidate() => cacheTick = -1;

        /// <summary>目前生效的封鎖圈（每 tick 最多重建一次）。</summary>
        public static List<BlockadeZone> ActiveZones
        {
            get
            {
                int now = Find.TickManager?.TicksGame ?? 0;
                if (cacheTick != now) { Rebuild(); cacheTick = now; }
                return zones;
            }
        }

        /// <summary>配置的世界攔截半徑（不看目前能力）。</summary>
        public static float NominalRadius(MapParent parent)
        {
            float configured = parent?.GetComponent<WorldObjectComp_AirDefense>()?.Props.worldInterceptRange ?? -1f;
            return configured >= 0f ? configured : AirDefenseAlertTuningDef.Current.interceptRadiusTiles;
        }

        private static void Rebuild()
        {
            zones.Clear();
            if (!WorldComponent_FactionAirDefenseAlert.Enabled || Find.World == null) { return; }
            WorldComponent_FactionAirDefenseAlert alert = WorldComponent_FactionAirDefenseAlert.Current;
            WorldComponent_BVRNetwork network = WorldComponent_BVRNetwork.Current;
            if (alert == null || network == null) { return; }
            List<BVRNode> nodes = network.Nodes;
            foreach (BVRNode node in nodes)
            {
                if (node.parent == null || node.faction == null || node.faction.IsPlayer || node.parent.Faction != node.faction) { continue; }
                if (alert.StageOf(node.parent) < (int)AirDefenseStage.Blockade) { continue; }
                BlockadeZone zone = ZoneFor(node, nodes, out _);
                if (zone != null) { zones.Add(zone); }
            }
        }

        /// <summary>
        /// 有效封鎖半徑 = min(配置的攔截半徑, 對標準運輸目標的可用追蹤覆蓋)。需要火控與至少一枚可用攔截彈；
        /// 通道占用與裝填冷卻不影響，避免封鎖圈反覆閃爍。本地雷達被壓制時看同陣營資料鏈的覆蓋。
        /// </summary>
        public static BlockadeZone ZoneFor(BVRNode node, List<BVRNode> all, out string unavailableKey)
        {
            unavailableKey = null;
            if (!node.HasInterceptorAmmo) { unavailableKey = "DMSE.AirAlert.Unavailable.NoAmmo"; return null; }
            if (!node.HasFireControl) { unavailableKey = "DMSE.AirAlert.Unavailable.NoFireControl"; return null; }
            float tracking = node.EquipmentDetectionRange(BVRTargetProps.Default);
            if (tracking <= 0f)
            {
                foreach (BVRNode other in all)
                {
                    if (other == node || other.faction != node.faction || !MissileWorldUtility.SameLayer(other.Tile, node.Tile)) { continue; }
                    float covered = other.DetectionRange(BVRTargetProps.Default) - MissileWorldUtility.SurfaceTilesBetween(other.Center, node.Center);
                    tracking = Mathf.Max(tracking, covered);
                }
            }
            if (tracking <= 0f) { unavailableKey = "DMSE.AirAlert.Unavailable.NoTracking"; return null; }
            float radius = Mathf.Min(NominalRadius(node.parent), tracking);
            if (radius <= 0f) { unavailableKey = "DMSE.AirAlert.Unavailable.NoTracking"; return null; }
            return new BlockadeZone { site = node.parent, faction = node.faction, tile = node.Tile, center = node.Center, radius = radius };
        }

        /// <summary>同陣營與盟友免受封鎖；無陣營視為非盟友。</summary>
        public static bool AllowedThrough(BlockadeZone zone, Faction traveller)
            => traveller != null && AirDefenseAlertPolicy.IsFriendly(zone.faction, traveller);

        /// <summary>
        /// 航線查詢：同圖層檢查整段航線（線段與覆蓋區相交，不只查起終點）；跨圖層檢查出發點，
        /// 以及在目的地圖層實際飛行的航段（從最接近出發點的格子到目的地），不把另一圖層的 tile 編號拿來量距離。
        /// </summary>
        public static AirspaceRestriction CheckRoute(Faction traveller, PlanetTile from, PlanetTile to)
        {
            if (!to.Valid) { return from.Valid ? CheckPoint(traveller, from) : AirspaceRestriction.None; }
            if (!from.Valid) { return CheckPoint(traveller, to); }
            if (ActiveZones.Count == 0) { return AirspaceRestriction.None; }
            if (from.Layer == to.Layer)
            {
                return CheckSegment(traveller, from.Layer, MissileWorldUtility.TileCenter(from), MissileWorldUtility.TileCenter(to));
            }
            AirspaceRestriction takeoff = CheckPoint(traveller, from);
            if (takeoff.Restricted) { return takeoff; }
            PlanetTile entry = to.Layer.GetClosestTile_NewTemp(from);
            Vector3 start = entry.Valid ? MissileWorldUtility.TileCenter(entry) : MissileWorldUtility.TileCenter(to);
            return CheckSegment(traveller, to.Layer, start, MissileWorldUtility.TileCenter(to));
        }

        /// <summary>單點查詢（起飛、降落、空投落點）。</summary>
        public static AirspaceRestriction CheckPoint(Faction traveller, PlanetTile tile)
        {
            if (!tile.Valid || ActiveZones.Count == 0) { return AirspaceRestriction.None; }
            Vector3 c = MissileWorldUtility.TileCenter(tile);
            return CheckSegment(traveller, tile.Layer, c, c);
        }

        private static AirspaceRestriction CheckSegment(Faction traveller, PlanetLayer layer, Vector3 a, Vector3 b)
        {
            foreach (BlockadeZone zone in ActiveZones)
            {
                if (zone.tile.Layer != layer || AllowedThrough(zone, traveller)) { continue; }
                if (SegmentDistanceTiles(zone.center, a, b) <= zone.radius)
                {
                    return new AirspaceRestriction
                    {
                        Restricted = true,
                        zone = zone,
                        Reason = "DMSE.Airspace.Blocked".Translate(zone.faction.Name, zone.site.LabelCap, zone.radius.ToString("0.#")),
                    };
                }
            }
            return AirspaceRestriction.None;
        }

        /// <summary>點到大圓弧線段的最短距離（地表等效格）。</summary>
        public static float SegmentDistanceTiles(Vector3 point, Vector3 a, Vector3 b)
        {
            if (point == Vector3.zero || a == Vector3.zero || b == Vector3.zero) { return float.MaxValue; }
            Vector3 p = point.normalized, u = a.normalized, v = b.normalized;
            float best = Mathf.Min(GenMath.SphericalDistance(p, u), GenMath.SphericalDistance(p, v));
            float arc = GenMath.SphericalDistance(u, v);
            if (arc > 0.00001f)
            {
                Vector3 normal = Vector3.Cross(u, v).normalized;
                float off = Vector3.Dot(p, normal);
                Vector3 projected = p - off * normal;
                if (projected.sqrMagnitude > 1e-10f)
                {
                    projected.Normalize();
                    if (GenMath.SphericalDistance(u, projected) + GenMath.SphericalDistance(projected, v) <= arc + 0.0001f)
                    {
                        best = Mathf.Min(best, Mathf.Asin(Mathf.Clamp01(Mathf.Abs(off))));
                    }
                }
            }
            return Find.WorldGrid.ApproxDistanceInTiles(best);
        }

        /// <summary>拒絕時給玩家的訊息（揭露管制原因，不只回「無法發射」）。</summary>
        public static void Reject(AirspaceRestriction restriction)
        {
            if (!restriction.Restricted) { return; }
            Messages.Message(restriction.Reason, restriction.zone?.site, MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}
