# BVR in-game acceptance (RimWorld 1.7)

Runs the mod's combat flows inside the real game loop: skyfallers, world objects, power
nets, pawns, rendering and Scribe with live references. Formula-level checks live in
`.source/DMSE.BVRTests`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .source/DMSE.BVRAcceptance/run.ps1 -OutDir <dir> [-TimeoutMinutes 45] [-Only S3,W6] [-SaveDataFolder <dir>]
```

- Close RimWorld first (it locks `1.7/Assemblies`) and build `.source/DMSE` first: the runner
  tests whatever `1.7/Assemblies/DMSE.dll` is there.
- `run.ps1` copies `DMSE.BVRAcceptance.dll` into `1.7/Assemblies` for the run only and removes
  it afterwards, also on a crash or timeout. Never ship that DLL.
- The game starts with `-quicktest` and the active mod list. The runner deletes its temporary
  save (`DMSE_BVR_Acceptance`) and quits when done. A full run takes about 20 minutes.
- `-Only` takes comma-separated ids and runs them in suite order.
- K7, L1–L3 write to the game's `Saves` folder; a sandbox without file access fails them.

## Output (`<dir>`)

| File | Content |
|---|---|
| `bvr_acceptance_results.tsv` | One row per test: id, PASS/FAIL, title, detail |
| `bvr_acceptance_log.txt` | Every check (`ok:` / `FAIL:`), notes and tick stamps |
| `DMSE_BVR_*.png` | Screenshots of key moments |
| `missile_flight_trace.tsv` | F1 per-tick flight trace |
| `Player.log` | Game log of the run |

## Tests

Each test's one-line title is in `AcceptanceSuite.Build()` (`Tests_Seekers.cs`); the design doc
for each series has the details.

| Ids | File | Covers |
|---|---|---|
| M0–M8 | `Tests_Map.cs` | Defs, device registration and Active, drop pod window, SAM lock and kill, CIWS bursts and debris, shared channel ledger |
| S1–S4 | `Tests_Seekers.cs` | Seeker target rules, laser designator, seekers on a freshly generated map, loiter flight control |
| W1–W11, D1–D4 | `Tests_World.cs`, `Tests_WorldEdge.cs`, `Tests_P4.cs` | World detection, lock, intercept, airburst, reroute, terminal handoff, NPC abstract defense, datalink, settlement defense, hidden tracks, pod ghost tracks, ETA at every speed |
| A1–A2, X1 | `Tests_AirAlert.cs`, `Tests_DevTools.cs` | NPC alert stages and blockade; debug action categories |
| K1–K15 | `Tests_Strike.cs` | Wave strike P0–P4 ([doc](../../docs/BVR_波次打擊規劃.md)) |
| N1–N4 | `Tests_FieldLaunchSite.cs` | Field launch site layout, incident, gunners, alert throttle |
| G1 | `Tests_Assembly.cs` | God-mode instant assembly |
| T1–T13 | `Tests_Generations.cs` | Three-generation equipment, manned launchers, research tree, Sabre ([doc](../../docs/三代裝備體系_實作計畫.md)) |
| I1–I8 | `Tests_InterceptFx.cs` | Intercept FX ([doc](../../docs/攔截殘骸特效_實作與技術美術計畫.md)) |
| V0–V8 | `Tests_LaunchFx.cs` | Launch FX, lighting, missile size tuner ([doc](../../docs/導彈發射特效_重製計畫.md)) |
| F1–F12 | `Tests_MissileFlight.cs` | Skyfaller → Projectile migration ([doc](../../docs/Missile_Skyfaller轉Projectile_計畫.md)) |
| R1–R5 | `Tests_Artillery.cs` | Empero long-range gun |
| AB1–AB3 | `Tests_AssemblyBench.cs` | Assembly bench ([doc](../../docs/導彈組裝台與裝配方案_實作計畫.md)) |
| L1–L3 | `Tests_Seekers.cs`, `Tests_WorldEdge.cs`, `Tests_MissileFlight.cs` | Save/load with tracks in flight; old skyfaller-era saves. L3 is last (it reloads); run it alone with `-Only L3` if a third-party mod crashes on reload |

## Test environment

- Storyteller incidents are off and `DebugSettings.unlimitedPower` is on; buildings still wait
  for their power net (`BVRFixture.WaitPowered`).
- Tests build on a cleared, paved pad and clean up after themselves (`CleanupAfterTest`).
- Mechanics tests (ids not starting with `T`) pin the radar values they were written against
  (`BVRFixture.PinLegacyRadarValues`: phased array and early fire control 2 channels, NPC base 2).
  T tests restore the shipped values (24 / 1 / 4), also after a reload.
- Enemy world missiles use laser guidance with an enemy mark on a far corner, so arrivals never
  hit the test buildings.
- Some timings force hit/miss rolls; real hit chances are only logged as notes.
- The runner closes `forcePause`/`absorbInputAroundWindow` dialogs every frame; clear those flags
  before a screenshot of such a window. A real mouse click on the game window can open dialogs mid-test.

To add a test, write an `IEnumerable<Wait>` method taking a `TestContext` and add it to
`AcceptanceSuite.Build()`. Use `TickWatch` for events shorter than a frame at ultrafast speed.
A site map you generate yourself (not through a missile arrival) must be registered in
`GameComponent_MissileEngage.times` as `OnArrived` does, or vanilla removes the empty map.
