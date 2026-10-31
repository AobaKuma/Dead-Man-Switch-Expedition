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
        private static Material interceptMaterial, blockadeMaterial, blockadeFillMaterial;

        /// <summary>封鎖圈內的地塊（紅色半透明填色）；圈心或半徑改變才重建網格（一次 flood fill）。</summary>
        private class ZoneFill { public Vector3 center; public float radius; public Mesh mesh; }
        private readonly Dictionary<(MapParent, Faction), ZoneFill> fills = new Dictionary<(MapParent, Faction), ZoneFill>();

        private void DrawFill(HashSet<(MapParent, Faction)> live, BlockadeZone zone)
        {
            var key = (zone.site, zone.faction);
            live.Add(key);
            if (!fills.TryGetValue(key, out ZoneFill fill) || fill.center != zone.center || fill.radius != zone.radius)
            {
                if (fill?.mesh != null) UnityEngine.Object.Destroy(fill.mesh);
                fill = new ZoneFill { center = zone.center, radius = zone.radius, mesh = MakeFillMesh(zone) };
                fills[key] = fill;
            }
            if (zone.tile.Layer != Find.WorldSelector.SelectedLayer) return;
            Graphics.DrawMesh(fill.mesh, Vector3.zero, Quaternion.identity, blockadeFillMaterial, WorldCameraManager.WorldLayer);
        }

        private static Mesh MakeFillMesh(BlockadeZone zone)
        {
            var tiles = new List<PlanetTile>();
            zone.tile.Layer.Filler.FloodFill(zone.tile,
                t => MissileWorldUtility.SurfaceTilesBetween(zone.center, MissileWorldUtility.TileCenter(t)) <= zone.radius,
                (PlanetTile t) => tiles.Add(t));
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var tileVerts = new List<Vector3>();
            foreach (PlanetTile tile in tiles)
            {
                Find.WorldGrid.GetTileVertices(tile, tileVerts);
                int start = verts.Count;
                for (int i = 0; i < tileVerts.Count; i++)
                {
                    verts.Add(tileVerts[i] + tileVerts[i].normalized * 0.02f);
                    if (i < tileVerts.Count - 2)
                    {
                        // 與原版 WorldDrawLayer_SingleTile 相同的扇形三角化與繞序。
                        tris.Add(start + i + 2);
                        tris.Add(start + i + 1);
                        tris.Add(start);
                    }
                }
            }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
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
            blockadeFillMaterial ??= MaterialPool.MatFrom(new MaterialRequest(BaseContent.WhiteTex, ShaderDatabase.WorldOverlayTransparent,
                new Color(1f, 0.1f, 0.05f, 0.25f)) { renderQueue = 3560 });
            var live = new HashSet<(MapParent, Faction, RingKind)>();
            var liveFills = new HashSet<(MapParent, Faction)>();
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
            // 封鎖圈：會攔截玩家者一律顯示（已公告），圈內地塊標紅。
            foreach (BlockadeZone zone in AirspaceRestrictionUtility.ActiveZones)
            {
                if (AirspaceRestrictionUtility.AllowedThrough(zone, Faction.OfPlayer)) continue;
                DrawFill(liveFills, zone);
                DrawRing(live, zone.site, zone.faction, RingKind.Blockade, zone.center, zone.tile, zone.radius, blockadeMaterial, 7f, 2, 3);
            }
            foreach (var stale in rings.Keys.Where(k => !live.Contains(k)).ToList()) rings.Remove(stale);
            foreach (var stale in fills.Keys.Where(k => !liveFills.Contains(k)).ToList())
            {
                UnityEngine.Object.Destroy(fills[stale].mesh);
                fills.Remove(stale);
            }
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
