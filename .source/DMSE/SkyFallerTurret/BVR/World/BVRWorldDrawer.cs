using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>Cached small circles; no world-grid flood fills or shared radius-ring cache.</summary>
    public class BVRWorldDrawer
    {
        private class Ring { public Vector3 center; public float range; public Vector3[] points; }
        private readonly Dictionary<(MapParent, Faction), Ring> rings = new Dictionary<(MapParent, Faction), Ring>();
        private readonly List<(Vector3 point, float until)> flashes = new List<(Vector3, float)>();
        public void AddFlash(Vector3 point) => flashes.Add((point, Time.realtimeSinceStartup + 1f));
        public void Draw(IEnumerable<BVRNode> nodes, bool showAll)
        {
            var live = new HashSet<(MapParent, Faction)>();
            foreach (BVRNode node in nodes)
            {
                var key = (node.parent, node.faction);
                live.Add(key);
                if (!Find.WorldSelector.IsSelected(node.parent) && !(showAll && node.faction.IsPlayer)) continue;
                float range = node.DetectionRange(BVRTargetProps.Default);
                if (range <= 0) continue;
                if (!rings.TryGetValue(key, out Ring ring) || ring.center != node.Center || ring.range != range)
                {
                    ring = MakeRing(node.Center, range);
                    rings[key] = ring;
                }
                for (int i = 0; i < ring.points.Length; i++)
                    GenDraw.DrawWorldLineBetween(ring.points[i], ring.points[(i + 1) % ring.points.Length]);
            }
            foreach (var stale in rings.Keys.Where(k => !live.Contains(k)).ToList()) rings.Remove(stale);
            foreach (var interceptor in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>())
            {
                // Dashes follow the spherical path, not a chord through the planet.
                Vector3 here = interceptor.DrawPos;
                for (int i = 0; i < 16; i += 2)
                    GenDraw.DrawWorldLineBetween(Vector3.Slerp(here, interceptor.end, i / 16f),
                        Vector3.Slerp(here, interceptor.end, (i + 1) / 16f));
            }
            for (int f = flashes.Count - 1; f >= 0; f--)
            {
                var flash = flashes[f];
                if (Time.realtimeSinceStartup >= flash.until) { flashes.RemoveAt(f); continue; }
                float radius = (1f - (flash.until - Time.realtimeSinceStartup)) * 0.7f + 0.15f;
                Ring ring = MakeRing(flash.point, radius);
                for (int i = 0; i < ring.points.Length; i++)
                    GenDraw.DrawWorldLineBetween(ring.points[i], ring.points[(i + 1) % ring.points.Length]);
            }
        }
        private static Ring MakeRing(Vector3 center, float range)
        {
            float angle = Mathf.Min(Mathf.PI, range / Mathf.Max(0.001f, Find.WorldGrid.ApproxDistanceInTiles(1f)));
            Vector3 normal = center.normalized;
            Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent);
            var points = new Vector3[64];
            for (int i = 0; i < points.Length; i++)
            {
                float phase = i * 2f * Mathf.PI / points.Length;
                points[i] = (normal * Mathf.Cos(angle) + (tangent * Mathf.Cos(phase) + bitangent * Mathf.Sin(phase))
                    * Mathf.Sin(angle)) * center.magnitude;
            }
            return new Ring { center = center, range = range, points = points };
        }
    }
}
