using System;

static partial class Test
{
    public static int Main()
    {
        Setup();
        Run("AirDefenseAlert", AirDefenseAlert);
        Run("InertialDistribution", InertialDistribution);
        Run("ShahedGuidanceOptions", ShahedGuidanceOptions);
        Run("ShortBoosterEffects", ShortBoosterEffects);
        Run("CiwsAimDirections", CiwsAimDirections);
        Run("InterceptDebrisGeometry", InterceptDebrisGeometry);
        Run("FxSmokeMotion", FxSmokeMotion);
        Run("LaunchFxTimeline", LaunchFxTimeline);
        Run("DepartureTurn", DepartureTurn);
        Run("MissileMapEntry", MissileMapEntry);
        Run("Detection", Detection);
        Run("Solver", Solver);
        Run("FireControl", FireControl);
        Run("LedgerAndTerminal", LedgerAndTerminal);
        Run("InventoryAndPersistence", InventoryAndPersistence);
        Run("StrikeP0", StrikeP0);
        Run("StrikeP1", StrikeP1);
        Run("StrikeP2", StrikeP2);
        Run("StrikeP3", StrikeP3);
        Run("StrikeP4", StrikeP4);
        Run("MissileContent", MissileContent);
        Run("AreaDefense", AreaDefense);
        Run("AssemblyBench", AssemblyBench);
        Run("InfoCard", InfoCard);
        foreach (string failure in failures) { Console.WriteLine("FAIL: " + failure); }
        Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAILED")}: {count} BVR checks passed, {failures.Count} failed (real game/DMSE assemblies).");
        return failures.Count == 0 ? 0 : 1;
    }
}
