using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 建立並發射一枚通用「來襲導彈」世界物件。
    /// <paramref name="target"/>：鎖定的目標（供終端更新追蹤）；null 表示打擊固定 tile。
    /// </summary>
    public static class IncomingMissileUtility
    {
        public const string DefaultWorldObjectDef = "DMSE_WorldObject_IncomingMissile";
        public const string DefaultIncomingDef = "DMSE_Incoming_Moskit"; // 原 DMSE_Incoming_CruiseMissile，2026-10-27 併入 Moskit

        public static WorldObject_IncomingMissile Launch(
            PlanetTile source,
            PlanetTile destination,
            MissileConfig config,
            Faction attacker,
            ThingDef incomingDef = null,
            WorldObjectDef worldObjectDef = null,
            MapParent target = null)
        {
            WorldObject_IncomingMissile wo = Make(source, destination, config, attacker, incomingDef, worldObjectDef, target);
            if (wo != null) { Find.WorldObjects.Add(wo); }
            return wo;
        }

        /// <summary>
        /// 同 <see cref="Launch"/>，但不加入世界：交給地圖上的離場彈，飛出地圖邊緣時才加入
        /// （<see cref="Projectile_MissileLaunch.worldObject"/>）。
        /// </summary>
        public static WorldObject_IncomingMissile Make(
            PlanetTile source,
            PlanetTile destination,
            MissileConfig config,
            Faction attacker,
            ThingDef incomingDef = null,
            WorldObjectDef worldObjectDef = null,
            MapParent target = null)
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
            wo.targetParent = target;
            wo.incomingSkyfaller = incomingDef
                ?? (config != null && config.body != null ? config.body.incomingSkyfaller : null)
                ?? DefDatabase<ThingDef>.GetNamedSilentFail(DefaultIncomingDef);
            if (attacker != null) { wo.SetFaction(attacker); }
            return wo;
        }
    }
}
