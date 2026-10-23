using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>Developer debug actions: they are registered, and the parts behind their menus work.</summary>
    public static class Tests_DevTools
    {
        private static object Call(System.Type type, string method, params object[] args)
            => type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, args);

        /// <summary>X1: DMSE debug actions exist under their categories; spawn, salvo, neighbor and log actions run.</summary>
        public static IEnumerable<Wait> DebugActions(TestContext t)
        {
            var actions = typeof(DMSEMod).Assembly.GetTypes()
                .SelectMany(ty => ty.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                .Select(m => m.GetCustomAttribute<DebugActionAttribute>()).Where(a => a != null).ToList();
            t.Note("debug actions: " + string.Join(", ", actions.Select(a => a.category + "/" + a.name)));
            t.Check(actions.All(a => a.category.StartsWith("DMSE ")), "every DMSE debug action has its own DMSE category");
            t.Check(!actions.Any(a => a.name.Contains("reserve rounds") || a.name.Contains("volley")), "legacy commands are gone");
            t.Check(actions.Count(a => a.category == "DMSE Missile") == 3 && actions.Count(a => a.category == "DMSE Missile Base") == 3,
                "missile and missile-base tools are registered");

            int before = Find.WorldObjects.AllWorldObjects.Count(o => o.GetComponent<WorldObjectComp_MissileBase>() != null);
            Call(typeof(DebugActions_MissileBase), "Spawn", Home.Tile, 5f, 10f);
            WorldObject site = Find.WorldObjects.AllWorldObjects.LastOrDefault(o => o.GetComponent<WorldObjectComp_MissileBase>() != null);
            t.Require(site != null && Find.WorldObjects.AllWorldObjects.Count(o => o.GetComponent<WorldObjectComp_MissileBase>() != null) == before + 1,
                "Spawn creates one missile base");
            Track(site);
            float distance = MissileWorldUtility.RangeDistance(Home.Tile, site.Tile);
            t.Check(distance >= 5f && distance <= 10f, $"the base is in the chosen distance band ({distance:0.#})");

            Find.WorldSelector.ClearSelection();
            Find.WorldSelector.Select(site);
            int tracks = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_IncomingMissile>().Count();
            Call(typeof(DebugActions_MissileBase), "FireNow");
            var fired = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_IncomingMissile>().Skip(tracks).ToList();
            fired.ForEach(Track);
            t.Check(fired.Count > 0 && fired.All(m => m.Faction == site.Faction), $"Fire salvo now launches world tracks from the selected base ({fired.Count})");
            foreach (WorldObject_IncomingMissile m in fired) { m.Destroy(); }

            int settlements = Find.WorldObjects.Settlements.Count(s => s.Faction == site.Faction);
            Call(typeof(DebugActions_AirDefenseAlert), "SpawnNeighbor");
            Settlement neighbor = Find.WorldObjects.Settlements.LastOrDefault(s => s.Faction == site.Faction);
            t.Check(Find.WorldObjects.Settlements.Count(s => s.Faction == site.Faction) == settlements + 1, "Spawn allied settlement adds one of the same faction");
            Track(neighbor);

            Call(typeof(DebugActions_MissileBase), "LogBases");
            Call(typeof(DebugActions_AirDefenseAlert), "LogStates");
            Call(typeof(DebugActions_AirDefenseAlert), "LogDecisions");
            Call(typeof(DebugActions_Strike), "LogRounds");
            t.Check(true, "log actions run without exceptions");
            Find.WorldSelector.ClearSelection();
            yield return new WaitTicks(2);
        }
    }
}
