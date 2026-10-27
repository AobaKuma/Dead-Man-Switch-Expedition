using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>God-mode instant assembly (mod setting): pending refits apply at once, free, with no colonist work.</summary>
    public static class Tests_Assembly
    {
        private static ThingDef BallisticSilo => ThingDef.Named("DMSE_Building_BallisticSilo");
        private static MapComponent_StrikePlanner Planner => Home.GetComponent<MapComponent_StrikePlanner>();
        // DMSEMod.Settings is internal; GetSettings returns the same cached instance.
        private static PlayerConfigSettings Settings => LoadedModManager.GetMod<DMSEMod>().GetSettings<PlayerConfigSettings>();

        private static Thing Load(Building_MissileRack rack)
        {
            Thing missile = ThingMaker.MakeThing(LightMissileItem);
            if (!rack.GetDirectlyHeldThings().TryAdd(missile)) { missile.Destroy(); return null; }
            missile.TryGetComp<CompMissileConfig>().EnsureInit();
            return missile;
        }

        private static int CountOnMap(ThingDef def) => Home.listerThings.ThingsOfDef(def).Sum(x => x.stackCount);

        public static IEnumerable<Wait> GodModeInstantAssembly(TestContext t)
        {
            bool oldGodMode = DebugSettings.godMode;
            bool oldSetting = Settings.godModeInstantAssembly;
            try
            {
                Building_MissileRack rack = (Building_MissileRack)SpawnBuilding(LightLauncher, 10, 2);
                Thing refit = Load(rack), stripped = Load(rack), reserved = Load(rack);
                Thing silo = SpawnBuilding(BallisticSilo, 20, 10);
                // Strike launches need free strike channels (weighted channels, 2026-10-22: a ballistic round costs 2);
                // two phased arrays give 4.
                SpawnBuilding(PhaseArray, 2, 30);
                SpawnBuilding(PhaseArray, 8, 30);
                yield return WaitPowered(); yield return new WaitTicks(2);
                t.Require(refit != null && stripped != null && reserved != null, "three light missiles in the rack");
                MissilePartDef rod = Part("DMSE_Light_Warhead_ContinuousRod");
                MissilePartDef penetrator = Part("DMSE_Ballistic_Warhead_TungstenPenetrator");
                CompMissileConfig refitCfg = refit.TryGetComp<CompMissileConfig>();
                refitCfg.pending.SetPart(MissilePartCategory.Warhead, rod);

                DebugSettings.godMode = true;
                Settings.godModeInstantAssembly = false;
                yield return new WaitTicks(300);
                t.Check(refitCfg.NeedsAssembly, "with the setting off, god mode leaves the refit pending");
                DebugSettings.godMode = false;
                Settings.godModeInstantAssembly = true;
                yield return new WaitTicks(300);
                t.Check(refitCfg.NeedsAssembly, "outside god mode the setting changes nothing");

                CompMissileConfig strippedCfg = stripped.TryGetComp<CompMissileConfig>();
                strippedCfg.config.SetPart(MissilePartCategory.Warhead, rod);
                strippedCfg.pending.CopyFrom(strippedCfg.config);
                strippedCfg.pending.SetPart(MissilePartCategory.Warhead, null);
                CompMissileConfig reservedCfg = reserved.TryGetComp<CompMissileConfig>();
                reservedCfg.pending.SetPart(MissilePartCategory.Warhead, rod);
                t.Require(Planner.TryReserve(reserved, MapComponent_StrikePlanner.DebugOwner), "one missile is reserved");
                CompMissileConfig siloCfg = silo.TryGetComp<CompMissileConfig>();
                CompMissileLauncher_Ballistic ballistic = silo.TryGetComp<CompMissileLauncher_Ballistic>();
                siloCfg.EnsureInit();
                siloCfg.pending.SetPart(MissilePartCategory.Warhead, penetrator);
                t.Require(!ballistic.IsLoaded && siloCfg.NeedsAssembly, "the silo waits for assembly");
                int steel = CountOnMap(ThingDefOf.Steel);
                int alloy = CountOnMap(ThingDef.Named("DMSE_TantastenAlloy"));

                DebugSettings.godMode = true;
                var applied = new WaitUntil(() => !refitCfg.NeedsAssembly && ballistic.IsLoaded, 400);
                yield return applied;
                t.Require(!applied.TimedOut, "god mode assembles the pending refits by itself");
                t.Check(refitCfg.config.PartFor(MissilePartCategory.Warhead) == rod && refitCfg.delivered.Count == 0
                    && !refitCfg.assemblyConfirmed, "the refit applies without confirmation or hauling");
                t.Check(!strippedCfg.NeedsAssembly && strippedCfg.config.PartFor(MissilePartCategory.Warhead) == null,
                    "removing a part applies too");
                t.Check(CountOnMap(ThingDefOf.Steel) == steel && CountOnMap(ThingDef.Named("DMSE_TantastenAlloy")) == alloy,
                    "no resources are used or refunded");
                t.Check(reservedCfg.NeedsAssembly, "a missile reserved by a strike mission keeps its pending refit");
                t.Check(ballistic.BuildFiringConfig(silo)?.PartFor(MissilePartCategory.Warhead) == penetrator,
                    "the silo is assembled and loaded with its pending warhead");

                PlanetTile target = TileAtDistance(8.5f, 9.5f);
                t.Require(target.Valid, "a target ~9 tiles away");
                ScorerProjectile_WorldObject track = ballistic.TryLaunchRound(new GlobalTargetInfo(target), silo, false, out string reason);
                t.Require(track != null, $"the silo fires ({reason ?? "ok"})");
                t.Check(ballistic.IsLoaded && ballistic.BuildFiringConfig(silo)?.PartFor(MissilePartCategory.Warhead) == penetrator,
                    "the silo reloads at once after firing");

                Planner.ReleaseAll(MapComponent_StrikePlanner.DebugOwner);
                var released = new WaitUntil(() => !reservedCfg.NeedsAssembly, 400);
                yield return released;
                t.Check(!released.TimedOut && reservedCfg.config.PartFor(MissilePartCategory.Warhead) == rod,
                    "once released, the reserved missile is assembled");
            }
            finally
            {
                DebugSettings.godMode = oldGodMode;
                Settings.godModeInstantAssembly = oldSetting;
            }
        }
    }
}
