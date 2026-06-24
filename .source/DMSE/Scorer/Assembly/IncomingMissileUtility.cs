using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>建立並發射一枚通用「來襲導彈」世界物件。</summary>
    public static class IncomingMissileUtility
    {
        public const string DefaultWorldObjectDef = "DMSE_WorldObject_IncomingMissile";
        public const string DefaultIncomingDef = "DMSE_Incoming_CruiseMissile";

        public static WorldObject_IncomingMissile Launch(
            PlanetTile source,
            PlanetTile destination,
            MissileConfig config,
            Faction attacker,
            ThingDef incomingDef = null,
            WorldObjectDef worldObjectDef = null)
        {
            WorldObjectDef woDef = worldObjectDef
                ?? DefDatabase<WorldObjectDef>.GetNamedSilentFail(DefaultWorldObjectDef);
            if (woDef == null)
            {
                Log.Error("[DMSE] Missing WorldObjectDef " + DefaultWorldObjectDef);
                return null;
            }

            WorldObject_IncomingMissile wo = (WorldObject_IncomingMissile)WorldObjectMaker.MakeWorldObject(woDef);
            wo.Tile = source;
            wo.destinationTile = destination;
            wo.config = config;
            wo.incomingSkyfaller = incomingDef
                ?? (config != null && config.body != null ? config.body.incomingSkyfaller : null)
                ?? DefDatabase<ThingDef>.GetNamedSilentFail(DefaultIncomingDef);
            if (attacker != null) { wo.SetFaction(attacker); }

            Find.WorldObjects.Add(wo);
            return wo;
        }
    }
}
