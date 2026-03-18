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
        const float ScreenFadeSeconds = 5f;
        const float MinFadeSeconds = 0.1f;

        private bool _fadeStarted;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _start, "start");
            Scribe_Values.Look(ref _end, "end");
            Scribe_Values.Look(ref _fadeStarted, "fadeStarted");
        }

        public override void Setup(PlanetTile origin, PlanetTile destination)
        {
            _start = origin;
            _end = destination;
            progress = 0f;
            Tile = origin;
            _fadeStarted = false;
        }

        protected override void Tick()
        {
            float prevProgress = progress;
            base.Tick();

            if (isTraveling && !_fadeStarted)
            {
                float progressPerTick = progress - prevProgress;
                if (progressPerTick > 0f)
                {
                    float ticksToArrival = (1f - progress) / progressPerTick;
                    float secondsToArrival = ticksToArrival / 60f;

                    // 進入淡出窗口時觸發；淡出在抵達前（最晚抵達時）完成
                    if (secondsToArrival <= ScreenFadeSeconds)
                    {
                        float fadeDuration = Mathf.Max(MinFadeSeconds, secondsToArrival);
                        ScreenFader.StartFade(Color.white, fadeDuration);
                        _fadeStarted = true;
                    }
                }
            }
        }

        public override void Arrive()
        {
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

            Building_GravEngine engine = Find.Maps.Where(m => m.Tile == worldObject.Tile).First()?.listerBuildings?.AllBuildingsColonistOfClass<Building_GravEngine>()?.FirstOrDefault();
            int thrusterCount = engine != null ? FlightUtility.GetTransferThrusterCount(engine) : 0;

            string time = GenDate.DateFullStringAt(Find.TickManager.TicksGame, Find.WorldGrid.LongLatOf(_end));
            bool colonialFleet = Find.FactionManager.FirstFactionOfDef(DMSE_DefOf.DMS_Army) != null;
            bool toMenu = false;
            StringBuilder sbEscapees = new StringBuilder();
            List<Pawn> aboard = new List<Pawn>();
            string credits = GameVictoryUtility.MakeEndCredits(
                time + "\n" + "DMSE.Impact.Ending.Intro".Translate(),
                "DMSE.Impact.Ending.Outro".Translate(),
                sbEscapees.ToString(),
                "DMSE.Impact.Ending.Aboard".Translate(),
                aboard);

            if (colonialFleet)
            {
                credits = GameVictoryUtility.MakeEndCredits(
                    time + "\n" + "DMSE.Impact.Ending.FleetFailed.Intro".Translate(),
                    "DMSE.Impact.Ending.FleetFailed.Outro".Translate(),
                    sbEscapees.ToString(),
                    "DMSE.Impact.Ending.Aboard".Translate(),
                    aboard);
            }
            if (thrusterCount >= 4)
            {
                credits = GameVictoryUtility.MakeEndCredits(
                    time + "\n" + "DMSE.Impact.Planetkiller.Ending.Intro".Translate(),
                    "DMSE.Impact.Planetkiller.Ending.Outro".Translate(),
                    sbEscapees.ToString(),
                    "DMSE.Impact.Planetkiller.Ending.Aboard".Translate(),
                    aboard);
                toMenu = true;
            }

            ImpactCraterUtility.ApplyImpactCraterAtTile(engine.LabelCap, thrusterCount, _end);
            ImpactCraterUtility.RemoveAllDmsArmySettlements();
            ImpactCraterUtility.MakeAllFactionsHostileToPlayerFromImpact();
            worldObject.Destroy();
            GameVictoryUtility.ShowCredits(credits, SongDefOf.EndCreditsSong,
                exitToMainMenu: toMenu, songStartDelay: ScreenFadeSeconds);

            if (!Destroyed)
                Destroy();
        }
    }
}