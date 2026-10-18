# BVR in-game acceptance (RimWorld 1.7)

Plays the BVR flow inside the real game: map-layer drop pods, SAM/CIWS, world-layer
detection and interception, NPC abstract defense, seekers, laser designation and
save/load. The checks in `.source/DMSE.BVRTests` cover formulas with a fixture;
this runner covers what needs the full game loop (skyfallers, world objects, power
nets, pawns, rendering, Scribe with live references).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .source/DMSE.BVRAcceptance/run.ps1 -OutDir <dir> [-TimeoutMinutes 45] [-Only S3,W6]
```

- Close RimWorld first (it locks `1.7/Assemblies`). Build `.source/DMSE` first: the
  runner references `1.7/Assemblies/DMSE.dll` and tests whatever is there.
- `run.ps1` copies `DMSE.BVRAcceptance.dll` into `1.7/Assemblies` for this run only
  and removes it afterwards, also on a crash or timeout. Never ship that DLL.
- The game starts with `-quicktest` (new Crashlanded colony, 250 map) and the
  active mod list as is. The runner deletes its temporary save
  (`DMSE_BVR_Acceptance`) and quits the game when done. Your own saves are untouched.
- A full run takes about 20 minutes, mostly at ultrafast speed. `-Only` takes comma-separated
  test ids and runs just those (in suite order), which is handy while fixing one test.

## Output (`<dir>`)

| File | Content |
|---|---|
| `bvr_acceptance_results.tsv` | One row per test: id, PASS/FAIL, title, detail |
| `bvr_acceptance_log.txt` | Every check (`ok:` / `FAIL:`), notes and tick stamps |
| `DMSE_BVR_*.png` | Screenshots of key moments (CIWS burst, laser mark, world track and interceptor, TV targeter) |
| `Player.log` | Game log of the run |

## Tests

| Id | Covers |
|---|---|
| M0 | Defs, components, a hostile faction |
| M1–M2 | Device registration, Active (power, switch, manning), channel capacity |
| M3 | Drop pod capture, warning window formula, landing without interception |
| M4 | Fire-control lock, SAM consumes a real missile and reloads, midcourse kill (no debris) |
| M5 | CIWS burst in the terminal window, barrel wear, multi-pawn pod debris |
| M6 | Two terminal waves: CIWS starts on the one landing first |
| M7 | No radar: pod lands normally |
| M8 | Map waves, world tracks and artillery share one channel ledger |
| S1 | Seeker target rules (anti-radiation, EM, heat, laser, inertial, TV) |
| S2 | Laser designator: mark position, duration, indestructible, expiry, no weapon tags |
| S3 | Anti-radiation and EM seekers on a base map generated at arrival, before its power net switched anything on |
| W1 | World detection at the ring edge, one letter, red track, alert, coverage ring (selected / always-show), ETA within 15 ticks, landing without CIWS |
| W2 | World lock, interceptor launch, channel kept in flight, kill at the meeting tick, flash registered |
| W3 | Forced miss, re-lock while time remains, second kill |
| W4 | World survivor arrives as a terminal-only wave; CIWS kills it, SAM stays idle |
| W5 | Radar loss clears detection and locks; the launched interceptor still resolves |
| W6 | NPC missile base intercepts from stock, hands over to the generated map, resumes after removal; EM seeker hits the phased array |
| W7 | TV-guided arrival pauses for the player's target |
| W8 | World airburst: extra rolls kill the companion missile, spare other destinations and tracks outside the radius |
| W9 | Target moves: reroute within one terminal check, lock restarts, in-flight interceptor misses, re-engagement on the new route |
| W10 | CIWS off during a terminal wave: wave stays terminal, SAM idle; CIWS resumes when powered |
| W11 | ETA while paused and at 1×/2×/3× speed, in map and world view |
| K1 | Wave strike P0: strike rounds skip interceptors; reservation; SAM and single-shot skip reserved missiles; out-of-range refusal; per-round launch, cooldown, ripple continuation |
| K2 | Reserved missiles offer no assembly work; an assembled ballistic silo is its own round and can be reserved |
| K3 | World defense gives free channels to the soonest arrivals, not the first-created track |
| K4 | Plan B decoys (8 light, then 2 cruise timed to arrive last) against an NPC base's abstract stock: no cruise engaged while an earlier decoy waits; logs stock drained before the first arrival |
| K5 | No interceptor ammo, no lock (world track and map wave); a reserved missile does not arm the SAM; firing the last interceptor releases the other pending lock |
| K6 | Wave strike P1 auto console: candidates (out of range, interceptors excluded), wave limit, confirm reserves 12 missiles, computing blocks firing, ripple launches 30 ticks apart with one cooldown, tracks tagged, channels booked then held for the guidance period, cancel drops queued launches and releases reservations |
| K7 | Strike mission saved between two ripple launches: mission, slot states, guidance hold, queued launch and reservations restored; queued launch fires after loading; the mission completes after wave 2 |
| K8 | Strike window draws in planning mode (auto-assign through the window) and execution mode, and closes itself when the mission ends |
| L1 | Save and reload with a detected track and an interceptor in flight |
| L2 | Save and reload after a reroute, with the old-route interceptor still flying |

## Test environment

- Storyteller incidents are off and `DebugSettings.unlimitedPower` is on. Buildings
  still wait for their power net to switch them on (`BVRFixture.WaitPowered`).
- Tests build on a cleared, paved pad and clean up after themselves (`CleanupAfterTest`).
- Enemy world missiles use laser guidance with a permanent enemy mark on a far corner
  of the map, so arrivals never hit the test buildings.
- Some timings use forced hit/miss rolls; real hit chances are only logged as notes.

To add a test, write an `IEnumerable<Wait>` method taking a `TestContext`, then add
it to `AcceptanceSuite.Build()` in `Tests_Seekers.cs`. Use `TickWatch` for events
shorter than a frame at ultrafast speed. A site map you generate yourself (not through
a missile arrival) must be registered in `GameComponent_MissileEngage.times` as
`OnArrived` does; otherwise vanilla removes the empty map and it stops ticking.
