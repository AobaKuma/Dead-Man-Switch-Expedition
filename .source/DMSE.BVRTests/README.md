# BVR checks (RimWorld 1.7)

These run the production DMSE assembly against the real 1.7 reference assemblies,
with a minimal game fixture rather than the game/render loop. They cover detection
radii and stealth/size modifiers, spherical head-on solutions and late-launch
rejection, fire-control formulas, per-faction shared channels (artillery/local/world),
terminal-only wave classification, NPC inventory/cooldown/restock and map handoff,
Scribe save output, missing-state defaults and lock release, plus wave-strike P0
missile roles, reservations, rail/silo strike rounds and soonest-arrival engagement order, interceptor-ammo
gating, wave-strike P1 channel booking, config fingerprints, auto-assign and mission save output,
P2 manual computation work and missile-by-missile solving (with P3 incremental additions), the P3 arrival
timeline (order warnings, suggested delays, ripple and cooldown timing, channel holds, in-flight ETAs), and P4
decoy signature factors and threat-assessment engagement order.

Use .NET 10 and supply the game reference directory (Assembly-CSharp.dll,
UnityStubs.dll and Waylith.dll). Restore dependencies once, then:

```sh
dotnet run --project .source/DMSE.BVRTests/DMSE.BVRTests.csproj -c Release --no-restore -p:RimWorldDir=/path/to/rimworld/ -p:OutputPath=/tmp/dmse-bvr-tests/
python .source/Tools/check_bvr_world.py .
```

The output override keeps test/runtime reference DLLs outside the mod's Assemblies
folder. Full Scribe loading needs the game's Steamworks and logging assemblies;
this fixture verifies save output/defaults, not full game loading. Rendering,
actual world launches, skyfaller handoff/impact, diplomacy and save/load with live
world-object references still require the in-game matrix in the design document.
