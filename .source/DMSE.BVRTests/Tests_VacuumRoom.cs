using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using Verse;

static partial class Test
{
    // Workshop feedback 2026-11-02: the vacuum room map had no terrain (ReGrowth Core / Simple FX: Splashes NRE on
    // topGrid in Map.FinalizeInit, so every entry made another map), and the filled entrance spawned ownerless.
    static void VacuumRoom()
    {
        var floor = new TerrainDef { defName = "TestSealedBase" };
        var other = new TerrainDef { defName = "TestOther" };
        var room = new Map
        {
            generatorDef = new MapGeneratorDef { genSteps = new List<GenStepDef> { new GenStepDef { genStep = new GenStep_Prefab { terrain = floor } } } },
            terrainGrid = Empty<TerrainGrid>(),
        };
        room.terrainGrid.topGrid = new TerrainDef[] { null, other, null };
        GenStep_Prefab.FillMissingTerrain(room);
        Check(room.terrainGrid.topGrid.SequenceEqual(new[] { floor, other, floor }), "null cells get the gen step terrain, laid terrain stays");

        var cave = new Map
        {
            generatorDef = new MapGeneratorDef { genSteps = new List<GenStepDef> { new GenStepDef { genStep = new GenStep_Prefab() } } },
            terrainGrid = Empty<TerrainGrid>(),
        };
        cave.terrainGrid.topGrid = new TerrainDef[] { null };
        GenStep_Prefab.FillMissingTerrain(cave);
        Check(cave.terrainGrid.topGrid[0] == null, "a prefab gen step without terrain leaves the map alone");
        var noGenerator = new Map { terrainGrid = Empty<TerrainGrid>() };
        noGenerator.terrainGrid.topGrid = new TerrainDef[] { null };
        GenStep_Prefab.FillMissingTerrain(noGenerator);
        Check(noGenerator.terrainGrid.topGrid[0] == null, "a map without a generator def is skipped");

        Check(ForceDeconstruct(true, null), "an ownerless filled entrance from an old save can be deconstructed");
        Check(!ForceDeconstruct(true, Faction(true)), "an owned filled entrance uses the normal faction rule");
        Check(!ForceDeconstruct(false, null), "the open entrance still has to be filled first");

        var comp = new CompPropertiesLandFillable();
        foreach (string key in new[] { comp.landfillText, "DMSE.Landfill.Reopen", "DMSE.Landfill.Cancel" })
        {
            Check(key.CanTranslate() && (key + "Desc").CanTranslate(), "landfill gizmo text is keyed: " + key);
        }
    }

    static bool ForceDeconstruct(bool deconstructible, Faction faction)
    {
        var parent = Parent(faction);
        parent.def.building = new BuildingProperties();
        Field(parent.def.building, "deconstructible", deconstructible);
        return new CompLandFillable { parent = parent, props = new CompPropertiesLandFillable() }.CompForceDeconstructable();
    }

    // Workshop feedback 2026-10-27: with a stack-size mod a launcher took a stack of 20 as one missile.
    static void MissileStacks()
    {
        ThingDef def = RoleDef(MissileRole.Dual);
        Field(def, "stackLimit", 20);
        def.thingClass = typeof(ThingWithComps);
        def.comps = new List<CompProperties>();
        def.destroyable = true; // Empty<ThingDef> skips field initialisers

        var rack = new Building_MissileRack { def = def, thingIDNumber = nextThingId++ };
        var stack = Round(def);
        stack.stackCount = 3;
        rack.GetDirectlyHeldThings().TryAdd(stack);
        Check(rack.StoredCount == 3 && rack.SpaceRemainingFor(def) == 1 && !rack.Full, "a stack of 3 counts as 3 missiles");
        var full = new Building_MissileRack { def = def, thingIDNumber = nextThingId++ };
        var four = Round(def);
        four.stackCount = 4;
        full.GetDirectlyHeldThings().TryAdd(four);
        Check(full.Full && full.SpaceRemainingFor(def) == 0, "a stack that fills the capacity makes the rack full");

        var big = Round(def);
        big.stackCount = 10;
        var emptyLauncher = new Building_MissileRack { def = def, thingIDNumber = nextThingId++ };
        Check(MissileRackUtil.LoadCount(big, emptyLauncher) == 4, "loading moves only what fits");
        Check(MissileRackUtil.LoadCount(big, rack) == 1, "loading tops up the remaining space");
        Check(MissileRackUtil.LoadCount(Round(def), emptyLauncher) == 1, "a single missile loads as one");

        Field(Current.Game, "uniqueIDsManager", new UniqueIDsManager());
        MissileRackUtil.ConsumeOne(stack);
        Check(stack.stackCount == 2 && rack.GetDirectlyHeldThings().Contains(stack) && rack.StoredCount == 2,
            "firing takes one missile off a stack and leaves the rest loaded");
        var last = Round(def);
        var single = new Building_MissileRack { def = def, thingIDNumber = nextThingId++ };
        single.GetDirectlyHeldThings().TryAdd(last);
        MissileRackUtil.ConsumeOne(last);
        Check(last.Destroyed && single.StoredCount == 0, "firing the last missile removes and destroys it");
    }
}
