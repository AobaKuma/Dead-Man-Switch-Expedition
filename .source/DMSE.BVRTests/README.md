# BVR checks (RimWorld 1.7)

Runs the production DMSE assembly against the real 1.7 reference assemblies with a minimal
game fixture (no game or render loop). Each `static void` section in `Program.cs` covers one
system: detection and stealth, intercept solver, fire control, channel ledger, NPC stock and
handoff, Scribe output and defaults, wave strike P0–P4, missile content, area defense, NPC
air-defense alert, map entry headings, intercept debris and launch FX math, inertial scatter,
Shahed guidance, and the assembly bench.

Use .NET 10 and point `RimWorldDir` at the game reference directory (`Assembly-CSharp.dll`,
`UnityStubs.dll`, `Waylith.dll`). Restore once, then:

```sh
dotnet run --project .source/DMSE.BVRTests/DMSE.BVRTests.csproj -c Release --no-restore -p:RimWorldDir=/path/to/rimworld/ -p:OutputPath=/tmp/dmse-bvr-tests/
python .source/Tools/check_bvr_world.py .
```

The output override keeps test-only DLLs out of the mod's `Assemblies` folder. The fixture
checks save output and defaults, not full game loading (that needs Steamworks and the game's
logging assemblies). Anything that needs rendering, live world objects or real save/load is in
`.source/DMSE.BVRAcceptance`.
