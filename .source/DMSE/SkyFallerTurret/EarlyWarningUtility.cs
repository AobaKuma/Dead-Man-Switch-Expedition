using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DMSE
{
    public static class EarlyWarningUtility
    {
        public const string RadarBuildingTag = "DMSE_InterceptRadar";

        private static List<ThingDef> cachedRadarDefs;

        private static List<ThingDef> RadarDefs
        {
            get
            {
                if (cachedRadarDefs == null)
                {
                    cachedRadarDefs = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(def =>
                            def.building != null &&
                            def.building.buildingTags != null &&
                            def.building.buildingTags.Contains(RadarBuildingTag) &&
                            def.comps != null &&
                            def.comps.Any(c => c.compClass == typeof(CompInterceptRadar)))
                        .ToList();
                }

                return cachedRadarDefs;
            }
        }

        public static bool HasRadarBuildingOnMap(Map map)
        {
            if (map == null)
            {
                return false;
            }

            List<ThingDef> defs = RadarDefs;
            for (int i = 0; i < defs.Count; i++)
            {
                if (map.listerThings.ThingsOfDef(defs[i]).Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static int ResolveDelay(Map map)
        {
            if (map == null)
            {
                return 0;
            }

            int delay = 0;
            bool foundActiveRadar = false;

            List<ThingDef> defs = RadarDefs;
            for (int i = 0; i < defs.Count; i++)
            {
                List<Thing> radars = map.listerThings.ThingsOfDef(defs[i]);
                for (int j = 0; j < radars.Count; j++)
                {
                    CompInterceptRadar radarComp = radars[j].TryGetComp<CompInterceptRadar>();
                    if (radarComp != null && radarComp.Active)
                    {
                        foundActiveRadar = true;

                        delay += radarComp.DelayTicks;
                    }
                }
            }
            return foundActiveRadar ? delay : 0;
        }

        public static bool IsHostileDropThing(Thing innerThing)
        {
            if (innerThing == null)
            {
                return false;
            }

            if (innerThing is Pawn pawn
                && pawn.Faction != Faction.OfPlayer
                && pawn.Faction?.HostileTo(Faction.OfPlayer) == true)
            {
                return true;
            }

            if (innerThing is ActiveTransporter transporter
                && transporter.Contents != null
                && transporter.Contents.SingleContainedThing is Pawn pawn2
                && pawn2.Faction != Faction.OfPlayer
                && pawn2.Faction?.HostileTo(Faction.OfPlayer) == true)
            {
                return true;
            }

            return false;
        }
    }
}