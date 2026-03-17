using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class WorldObject_ImpactGravship : TravelingObject
    {
        public Map sourceMap;

        private float traveledPct;

        private const float TravelSpeed = 0.00035f;
        private const float ScreenFadeSeconds = 5f;

        private Vector3 Start => Find.WorldGrid.GetTileCenter(_start);
        private Vector3 End => Find.WorldGrid.GetTileCenter(_end);

        public override Vector3 DrawPos => Vector3.Slerp(Start, End, traveledPct);

        private float TraveledPctStepPerTick
        {
            get
            {
                Vector3 a = Start;
                Vector3 b = End;
                if (a == b) return 1f;
                float dist = GenMath.SphericalDistance(a.normalized, b.normalized);
                if (dist == 0f) return 1f;
                return TravelSpeed / dist;
            }
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            traveledPct += TraveledPctStepPerTick * delta;
            if (traveledPct >= 1f)
            {
                traveledPct = 1f;
                Arrive();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _start, "start");
            Scribe_Values.Look(ref _end, "end");
            Scribe_Values.Look(ref traveledPct, "traveledPct", 0f);
            Scribe_References.Look(ref sourceMap, "sourceMap");
        }

        public override void Setup(PlanetTile origin, PlanetTile destination)
        {
            _start = origin;
            _end = destination;
            traveledPct = 0f;
            Tile = origin;
        }

        public override void Arrive()
        {
            if (_end.Valid && !_end.LayerDef.isSpace)
            {
                ImpactCraterUtility.ApplyImpactCraterAtTile(_end);
            }

            Settlement settlement = Find.WorldObjects.SettlementAt(_end);
            if (settlement != null && settlement.Faction != Faction.OfPlayer)
            {
                DestroyedSettlement ds = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(
                    settlement.Tile.LayerDef.DestroyedSettlementWorldObjectDef);
                ds.Tile = settlement.Tile;
                ds.SetFaction(settlement.Faction);
                Find.WorldObjects.Add(ds);
                settlement.Destroy();
            }

            StringBuilder sbEscapees = new StringBuilder();
            List<Pawn> aboard = new List<Pawn>();
            if (sourceMap != null)
            {
                foreach (Pawn p in sourceMap.mapPawns.FreeColonistsSpawned.ToList())
                {
                    aboard.Add(p);
                    sbEscapees.AppendLine("   " + p.LabelCap);
                }
            }

            string credits = GameVictoryUtility.MakeEndCredits(
                "DMSE.Impact.Ending.Intro".Translate(),
                "DMSE.Impact.Ending.Outro".Translate(),
                sbEscapees.ToString(),
                "DMSE.Impact.Ending.Aboard",
                aboard);

            ScreenFader.StartFade(Color.white, ScreenFadeSeconds);
            GameVictoryUtility.ShowCredits(credits, SongDefOf.EndCreditsSong,
                exitToMainMenu: true, songStartDelay: ScreenFadeSeconds);

            if (!Destroyed)
                Destroy();
        }
    }
}