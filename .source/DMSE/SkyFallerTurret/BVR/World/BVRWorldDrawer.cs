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
        // Lines exactly on the sphere are hidden by the terrain mesh; vanilla world paths and radius rings
        // (WorldPath.DrawPath, GenDraw.DrawWorldRadiusRing) lift every point by 0.08 and draw rings at 5x width.
        private const float Lift = 0.08f;

        private static void Line(Vector3 a, Vector3 b, Material material, float width)
            => GenDraw.DrawWorldLineBetween(a + a.normalized * Lift, b + b.normalized * Lift, material, width);

        private static Material RingMaterial(PlanetTile tile)
            => tile.LayerDef.isSpace ? tile.LayerDef.WorldLineMaterialHighVis : tile.LayerDef.WorldLineMaterial;

        private class Ring { public Vector3 center; public float range; public Vector3[] points; }

        /// <summary>圈的種類：探測（實線）、攔截（點線）、封鎖（粗虛線）。用線型區分，不只靠顏色。</summary>
        private enum RingKind { Detection, Intercept, Blockade }
        private readonly Dictionary<(MapParent, Faction, RingKind), Ring> rings = new Dictionary<(MapParent, Faction, RingKind), Ring>();
        private readonly List<(Vector3 point, float until)> flashes = new List<(Vector3, float)>();
        private static Material interceptMaterial, blockadeMaterial;
        public void AddFlash(Vector3 point) => flashes.Add((point, Time.realtimeSinceStartup + 1f));

        private void DrawRing(HashSet<(MapParent, Faction, RingKind)> live, MapParent parent, Faction faction, RingKind kind,
            Vector3 center, PlanetTile tile, float range, Material material, float width, int dashOn, int dashPeriod)
        {
            var key = (parent, faction, kind);
            live.Add(key);
            if (range <= 0) return;
            if (!rings.TryGetValue(key, out Ring ring) || ring.center != center || ring.range != range)
            {
                ring = MakeRing(center, range);
                rings[key] = ring;
            }
            width *= tile.LayerDef.lineWidthFactor;
            for (int i = 0; i < ring.points.Length; i++)
            {
                if (i % dashPeriod >= dashOn) continue;
                Line(ring.points[i], ring.points[(i + 1) % ring.points.Length], material, width);
            }
        }

        public void Draw(IEnumerable<BVRNode> nodes, bool showAll)
        {
            // Created on the main thread at first draw (MaterialPool is not safe from static constructors here).
            interceptMaterial ??= MaterialPool.MatFrom(GenDraw.OneSidedLineOpaqueTexPath, ShaderDatabase.WorldOverlayAdditiveTwoSided, new Color(1f, 0.6f, 0.15f), 3590);
            blockadeMaterial ??= MaterialPool.MatFrom(GenDraw.OneSidedLineOpaqueTexPath, ShaderDatabase.WorldOverlayAdditiveTwoSided, ColorLibrary.RedReadable, 3590);
            var live = new HashSet<(MapParent, Faction, RingKind)>();
            foreach (BVRNode node in nodes)
            {
                bool selected = Find.WorldSelector.IsSelected(node.parent);
                if (!selected && !(showAll && node.faction.IsPlayer)) continue;
                DrawRing(live, node.parent, node.faction, RingKind.Detection, node.Center, node.Tile,
                    node.DetectionRange(BVRTargetProps.Default), RingMaterial(node.Tile), 5f, 1, 1);
                // 選取的 NPC 據點：攔截圈（配置半徑，第三階段以上才會攔截）。
                if (selected && node.UsesAlertRules && node.alertStage >= (int)AirDefenseStage.Passive)
                    DrawRing(live, node.parent, node.faction, RingKind.Intercept, node.Center, node.Tile,
                        AirspaceRestrictionUtility.NominalRadius(node.parent), interceptMaterial, 3f, 1, 3);
            }
            // 封鎖圈：玩家不能通行者一律顯示（已公告）。
            foreach (BlockadeZone zone in AirspaceRestrictionUtility.ActiveZones)
            {
                if (AirspaceRestrictionUtility.AllowedThrough(zone, Faction.OfPlayer)) continue;
                DrawRing(live, zone.site, zone.faction, RingKind.Blockade, zone.center, zone.tile, zone.radius, blockadeMaterial, 7f, 2, 3);
            }
            foreach (var stale in rings.Keys.Where(k => !live.Contains(k)).ToList()) rings.Remove(stale);
            foreach (var interceptor in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>())
            {
                // Dashes follow the spherical path, not a chord through the planet.
                Vector3 here = interceptor.DrawPos;
                Material material = RingMaterial(interceptor.Tile);
                float width = 3f * interceptor.Tile.LayerDef.lineWidthFactor;
                for (int i = 0; i < 16; i += 2)
                    Line(Vector3.Slerp(here, interceptor.end, i / 16f),
                        Vector3.Slerp(here, interceptor.end, (i + 1) / 16f), material, width);
            }
            for (int f = flashes.Count - 1; f >= 0; f--)
            {
                var flash = flashes[f];
                if (Time.realtimeSinceStartup >= flash.until) { flashes.RemoveAt(f); continue; }
                float radius = (1f - (flash.until - Time.realtimeSinceStartup)) * 0.7f + 0.15f;
                Ring ring = MakeRing(flash.point, radius);
                for (int i = 0; i < ring.points.Length; i++)
                    Line(ring.points[i], ring.points[(i + 1) % ring.points.Length],
                        Find.WorldGrid.Surface.Def.WorldLineMaterialHighVis, 3f);
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
