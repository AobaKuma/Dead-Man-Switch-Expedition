using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 結構樁放置（設計稿 §3-7）：畫出預定覆蓋範圍、連結範圍與範圍內可架設鋼樑的既有樁
    /// （只是提示，放置後不會自動連結）；與既有網路重疊處以橘色提示。中心 3×3 不可落在虛空上。
    /// 不提供連結範圍的樁（linkRange = 0）只能建在已建成、提供範圍的己方樁的連結範圍內。
    /// 帶地形篩選的樁，佔地的基底地形也必須通過篩選。
    /// </summary>
    public class PlaceWorker_StructuralPile : PlaceWorker
    {
        private static readonly List<IntVec3> tmpCoverage = new List<IntVec3>();

        private static readonly List<IntVec3> tmpRange = new List<IntVec3>();

        private static readonly HashSet<IntVec3> tmpRangeSet = new HashSet<IntVec3>();

        private static readonly List<IntVec3> tmpOverlap = new List<IntVec3>();

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            ThingDef def = checkingDef as ThingDef;
            if (def == null || map == null)
            {
                return true;
            }
            CompProperties_StructuralPile props = def.GetCompProperties<CompProperties_StructuralPile>();
            if (props != null && props.spaceMapOnly && !map.Tile.LayerDef.isSpace)
            {
                return new AcceptanceReport("DMSE.Pile.Place.SpaceOnly".Translate());
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, def.size))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                if (!PileNetwork.IsSolidCell(map, c))
                {
                    return new AcceptanceReport("DMSE.Pile.Place.OverVoid".Translate());
                }
                // 樁本身必須落在自己會算入船體的地形上
                if (props != null && !props.AcceptsTerrainAt(map, c))
                {
                    return new AcceptanceReport("DMSE.Pile.Place.WrongTerrain".Translate(map.terrainGrid.BaseTerrainAt(c).LabelCap));
                }
            }
            if (props != null && !props.ProvidesLinkRange && !PileLinkUtility.InAnyProviderRange(map, props, loc, thingToIgnore))
            {
                return new AcceptanceReport("DMSE.Pile.Place.NeedProviderRange".Translate());
            }
            return true;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            // 已建成的樁由選取疊加層負責（CompStructuralPile.PostDrawExtraSelectionOverlays）
            if (thing != null && thing.TryGetComp<CompStructuralPile>() != null)
            {
                return;
            }
            Map map = Find.CurrentMap;
            CompProperties_StructuralPile props = def.GetCompProperties<CompProperties_StructuralPile>();
            if (map == null || props == null)
            {
                return;
            }

            tmpCoverage.Clear();
            foreach (IntVec3 c in props.CoverageCells(center, map))
            {
                if (props.AcceptsTerrainAt(map, c))
                {
                    tmpCoverage.Add(c);
                }
            }
            GenDraw.DrawFieldEdges(tmpCoverage, PileResources.CoverageGhostColor);

            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(map);

            tmpRange.Clear();
            if (props.ProvidesLinkRange)
            {
                tmpRange.AddRange(props.LinkRangeCells(center, map));
            }
            else if (comp != null)
            {
                // 不提供範圍的樁：改畫可建造的區域（所有提供範圍的己方樁的連結範圍）
                tmpRangeSet.Clear();
                List<CompStructuralPile> all = comp.Piles;
                for (int i = 0; i < all.Count; i++)
                {
                    CompStructuralPile p = all[i];
                    if (p.Props.ProvidesLinkRange && p.parent.Faction == Faction.OfPlayerSilentFail)
                    {
                        tmpRangeSet.AddRange(p.Props.LinkRangeCells(p.parent.Position, map));
                    }
                }
                tmpRange.AddRange(tmpRangeSet);
            }
            if (tmpRange.Count > 0)
            {
                GenDraw.DrawFieldEdges(tmpRange, PileResources.LinkRangeColor);
            }

            if (comp == null)
            {
                return;
            }

            float alt = PileResources.OverlayAltitude;
            Vector3 from = GenThing.TrueCenter(center, rot, def.size, alt);
            List<CompStructuralPile> piles = comp.Piles;
            for (int i = 0; i < piles.Count; i++)
            {
                CompStructuralPile p = piles[i];
                if (p.parent == thing || p.parent.Faction != Faction.OfPlayerSilentFail)
                {
                    continue;
                }
                if (PileLinkUtility.InLinkRange(props, center, p.Props, p.parent.Position))
                {
                    GenDraw.DrawLineBetween(from, p.parent.TrueCenter().WithY(alt), PileResources.CandidateLineMat);
                }
            }

            tmpOverlap.Clear();
            List<PileNetwork> networks = comp.Networks;
            for (int i = 0; i < tmpCoverage.Count; i++)
            {
                IntVec3 c = tmpCoverage[i];
                for (int n = 0; n < networks.Count; n++)
                {
                    if (networks[n].covered.Contains(c))
                    {
                        tmpOverlap.Add(c);
                        break;
                    }
                }
            }
            if (tmpOverlap.Count > 0)
            {
                GenDraw.DrawFieldEdges(tmpOverlap, PileResources.ContestedColor);
            }
        }
    }
}
