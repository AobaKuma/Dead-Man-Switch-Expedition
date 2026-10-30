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
| M5 | CIWS burst in the terminal window, barrel wear, multi-pawn pod debris landing next to the predicted pod-debris cell |
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
| D1 | Datalink and area defense: a track crossing a node's envelope (not heading to it) is intercepted by a node it was not aimed at |
| D2 | Synthetic settlement air defense: a plain enemy settlement gets one by tech level; an anti-radiation strike takes stock and the radar down |
| D3 | Undetected hostile tracks are hidden and unselectable until a radar sees them |
| D4 | Drop-pod ghost track interpolates from the ring edge; degrading a real abstract node removes stock and suppresses the radar |
| X1 | Developer debug actions: every DMSE action sits in a `DMSE …` category, legacy commands are gone, and base spawn (distance band), salvo from the selected base, allied-settlement spawn and the log actions work |
| A1 | NPC alert: a missile base starts at stage 3, records intel on a missile aimed at an empty tile but lets it pass; at stage 4 it engages the same track |
| A2 | NPC alert: hits add local pressure and repeat-wave bonuses, a nearby settlement gets regional pressure only, the blockade is announced, takes effect after the notice, restricts player takeoff/landing/routes (not the owner), and stops being enforced when interceptors run out |
| K1 | Wave strike P0: strike rounds skip interceptors; reservation; SAM and single-shot skip reserved missiles; out-of-range refusal; per-round launch, cooldown, ripple continuation |
| K2 | Reserved missiles offer no assembly work; an assembled ballistic silo is its own round and can be reserved |
| K3 | World defense gives free channels to the soonest arrivals, not the first-created track |
| K4 | Plan B decoys (8 light, then 2 cruise timed to arrive last) against an NPC base's abstract stock: no cruise engaged while an earlier decoy waits; logs stock drained before the first arrival |
| K5 | No interceptor ammo, no lock (world track and map wave); a reserved missile does not arm the SAM; firing the last interceptor releases the other pending lock |
| K6 | Wave strike P1 auto console: candidates (out of range, interceptors excluded), wave limit, confirm reserves 12 missiles, computing blocks firing, ripple launches 30 ticks apart with one cooldown, tracks tagged, channels booked then held for the guidance period, cancel drops queued launches and releases reservations |
| K7 | Strike mission saved between two ripple launches: mission, slot states, guidance hold, queued launch and reservations restored; queued launch fires after loading; the mission completes after wave 2 |
| K8 | Strike window draws in planning mode (auto-assign through the window), execution mode and adjust mode, plus the rename window, and closes itself when the mission ends |
| K9 | Wave strike P2 manual planning table (no power): confirming asks for 3100 work; work giver and pending alert (no researcher); a researcher computes so wave 1 fires before wave 2 is solved; progress survives an interruption; XP; the job ends by itself |
| K10 | Wave strike P3 adjusting waves: blocked while launching; a moved missile keeps its solution; added missiles are reserved and computed (auto countdown; manual needs only their 400 work); removed missiles are released; launched ones cannot be removed |
| K11 | Wave strike P3 timeline flags a fast ballistic wave arriving before a light wave; wave-ready alert; strike tracks grouped in the player-missile alert; world-wide mission ids; random mission names and renaming carry into the report and alert; outcome report counts launches, misses, a hit and a terminal kill and sends a results letter |
| K12 | Wave strike P4 timed launch: a ballistic wave set to auto-fire waits past its first possible launch and lands after the light wave plus the spacing |
| K13 | Wave strike P4: a 2-tile target move keeps the firing solutions; a 6–7-tile move voids them, blocks the wave and the auto console recomputes |
| K14 | Wave strike P4: a decoy payload doubles the radar size; with one channel a threat-assessing base locks the decoy first, a soonest-first base the plain missile |
| K15 | Wave strike P4: an unloaded NPC missile base fires a three-missile salvo at one colony |
| N1 | Industrial field launch site: only industrial factions own it; threatScale 0.3 vs 2.2 generates one central search radar, more structures/launchers at high difficulty, garrison 5 vs 10 (threatPoints 1000, `crewByTier`); a timed drop delivers survival meals while a radar runs and none arrive (incl. the crew LordJob low-stock drop) once every radar is switched off; no Concrete/PavedTile laid (natural terrain kept); every enemy rack spawns loaded; an emptied launcher is refilled by a crew member hauling from an ammo storage (`DMSE_LoadMissileLauncher`); an emptied ammo storage gets a resupply pod (next one ~2 days later) that the crew stows after the load delay; the map has vanilla vegetation and wildlife (MapCommonBase); packed-dirt roads put every world launcher on one connected road network |
| N2 | Field launch site incident `DMSE_FieldLaunchSiteDeployed`: executes at 1000 points, adds one site owned by a hostile industrial faction within 15 tiles with those threat points, sends a letter targeting it, and refuses a second site while one exists |
| N3 | Field site gunners (`LordJob_MissileStationCrew`, duty `DMSE_ManMissileStations`): a rotary missile launcher, an early fire-control radar and a legacy flak gun placed on the site are all manned by the station crew lord; a gunner set to 20 % food leaves its post to eat survival meals, then mans a station again while the fed gunners stay at theirs |
| N4 | `MissileAlertThrottle` (incoming/detected missile alerts): the first alert of a kind is a letter, repeats within `CooldownTicks` (1 h) become messages, another kind still gets a letter, and after the cooldown the same kind is a letter again |
| G1 | God-mode instant assembly setting: off (or outside god mode) leaves refits pending; on, rack missiles and a silo are assembled with no confirmation, hauling or resources (removed parts refund nothing), a reserved missile waits until released, and the silo reloads right after firing |
| I1 | Intercept airburst FX (docs/攔截殘骸特效_實作與技術美術計畫.md): terminal/midcourse/drop pod at several headings, screenshots at 3 and 45 ticks; the drawn burst stays inside the map margin |
| I2 | Fleck render A/B for the art review: intercept smoke via own fleck system vs vanilla system vs vanilla dust puff |
| I3 | Intercept major debris chunks: spawn with a hulk, flight state survives save/load, all land or are culled within the lifetime, damage only with the setting (roofs stop it), trail screenshots |
| I4 | Intercept drop-pod debris profile: no fireball, pod chunk/fragment counts and tint, the hulk draws the pod texture; missiles keep the missile profile |
| I5 | Intercept FX draw above fog of war: own materials sit between the fog and map-edge render queues, vanilla materials untouched, burst-over-fog screenshots |
| I6 | Intercept FX budget: density setting scales fragment counts; a 40-burst salvo stays under the live chunk/fragment caps (frame time logged) |
| I7 | Smoke instancing A/B: the same seeded 40-burst salvo in one session with smoke/trail/fireball instancing on, off, on; frame times logged |
| I8 | Remote terminal intercepts: approach-0 bursts from N/E/S/W are virtual points beyond the entry edge with no airburst; every chunk reaches the screen before leaving, fragments fade in from the edge band; edge screenshots at 30/60/90 ticks |
| V0 | Launch FX L0 (docs/導彈發射特效_重製計畫.md): 3000 smoke quads drawn as Transparent, Transparent instanced and Mote instanced; screenshots show whether per-instance colour survives, CPU submit time logged |
| V1 | Launch FX L2: the plume emitter registers, reaches full thrust and is drawn; old vs new solid-booster launch screenshots at the same ticks |
| V2 | Launch FX L3: a cold vertical launch leaves a continuous smoke column (max gap / puff size < 0.8) that still hangs in the air 4 s after the missile left |
| V3 | Launch FX L4: hot ground cloud at ignition, cold ejection puff before ignition and air ignition after the delay, booster separation drops debris and the cruise missile drops to its sustainer |
| V4 | Launch FX L5: every DMSE_Launch_* def carries a launch FX profile; one screenshot per body at the same tick |
| V5 | Launch FX L6: a 12-missile ripple (30 ticks apart) at medium and high density stays under the smoke cap; canvas draw CPU logged |
| V6 | Lighting diagnostic: smoke tinted 100/60/35 % on Mote (instanced) vs Transparent, sky colour logged (dark tints darken on Mote; Transparent at the default queue is lit by the overlay) |
| V7 | Smoke and debris follow ambient light (FxLighting): midnight darker than noon, brighter next to a torch lamp; launch and intercept screenshots at noon and midnight (jumps the clock with the debug tick setter) |
| T1 | Three-generation (docs/三代裝備體系_實作計畫.md §14-2): the launch tower starts with V2; its backblast burns and stuns an animal two cells away and starts fires, but leaves the tower and a wall unharmed; the strategic silo hurts nobody |
| T2 | Rotary launch rail: Loitering + Light storage and taxonomy; unmanned it neither strikes nor intercepts; V1 and Termite fly their own airframes (MissileItemExtension); the SAM never takes them; a Rheintochter arms only the SAM (hit factor 0.6); the launch rail splits roles the same way |
| T3 | The SAM skips a strike-only Tomahawk in a tilt container and fires the SA-19 next to it |
| T4 | Shipped channels (phased array 24, fire control 4, early 1, field air defence 2; the early set needs manning, the automatic field air defence does not), 9000 W phased array, search radar, unpowered acoustic radar, 4-round SAM launcher and light VLS |
| T5 | Termite built-in anti-ship guidance (automatic, 3 terminal updates, mobile targets) vs V1 built-in TV; default-part ranges of every new body; ballistic body and thermonuclear research gates; anti-ship research and recipes |
| T6 | Interceptor hit factors per round, and the ×1.25 Sparrow factor in a real map-layer shot |
| T7 | Millennium works unmanned, Flak 37 only manned; the Millennium stops three pods and burns 24 rounds per burst |
| T8 | Man-portable SAM: gizmo and ground shot disabled without missiles; engageable tracks; direct fire uses one missile and starts the cooldown; the ordered aiming job fires the second |
| T9 | Field air defence on-map cannon: automatic, locks an enemy due east while the head still faces north and holds fire, fires (spending cannon rounds) only once the head points at the target, holds its firing heading after the target is gone, drops the target when switched off |
| T10 | Rotary launch rail manned through the real job (two acoustic radars, manned early fire control, Rheintochters in loitering racks): fires at a pod wave, the gunner reloads from a rack and fires again; every shot has a hit chance and resolves before the pods land |
| T11 | Same with a manned Flak 43: a 12-pod wave is inside the 1800-tick terminal window from the start, and the SAM still fires, reloads and fires again next to the CIWS (2026-10-27 report) |
| T12 | Missile and radar research progression, merged ballistic-platform/V2 unlocks, research costs, radar-network progression, fire-control guidance, phased-array gate, and separation from aerospace research |
| F1 | Skyfaller → Projectile migration (docs/Missile_Skyfaller轉Projectile_計畫.md §8.6): every ascent def × 4 rotations, an old skyfaller and a runtime `Projectile_MissileBase` twin spawned on the same cell; timeline, pose, ignition tick and exhaust inputs equal tick for tick. Full trace in `missile_flight_trace.tsv` |
| F2 | Old/new ascent screenshots at 10/30/60 ticks (light E, ballistic N, cruise W, SAM E) for visual comparison |
| F3 | Entry heading equals the world track's arrival tangent (120 sampled tile pairs, within 2°) and maps to the same edge as `PlanetLayer.GetRotFromTo`; a sideways reroute changes the heading to the last leg's |
| F4 | Every missile body has its own launch projectile (`MissileBodyDef.launchProjectile`): texture loads, ascent flight, launch effect ignites and exhausts, launchers pick it over the platform skyfaller; the four bodies copied from a platform skyfaller replay it tick for tick in 4 rotations |
| F5 | Departure cruise (plan §8.3): every body × 5 world headings × 4 rotations hands off mid-ascent, turns smoothly (no step/direction jump), flies out of the edge the heading points at with nose and motion on the heading, cruise speed follows speedCurve(progress)/speedCurve(handoff), exhaust stays lit with a held progress, and `DepartureTicksOf` equals the real flight |
| F6 | Screenshots of an east-launched light missile turning to a north-west heading |
| F7 | Incoming missiles (plan §8.4): every incoming def × 5 headings starts off-map, enters through the edge the heading points at, flies straight on the heading with the nose on it and reaches the target (disarmed just before impact); without a heading the old fall is unchanged; a BVR terminal hold respawns the missile with its heading |
| F8 | Screenshots of an advanced cruise missile arriving from the north-east |
| F9 | High-angle shields (plan §6, Royalty `ShieldGeneratorMortar`): an enemy missile without a launcher is stopped on the ring on its approach side, the shield loses HP, the strike report counts "stopped by shields"; the player's own shield lets a player missile through; an enemy shield stops a player missile with a launcher and that explosion is not attributed; a ground (bullet) shield never stops missiles |
| F10 | Launcher attribution (plan §7): the impact explosion is logged against the launching building on the same map and on a generated enemy base map; hitting a neutral faction's pawn costs goodwill; a destroyed launcher leaves no instigator |
| F11 | Interceptors turn after the ascent toward the incoming bearing (a missile's entry heading, a pod's landing cell; world interceptions toward the meeting point), leave the map on it and resolve their (forced) hits; two fired in the same tick spawn at least `SpacingTicks` apart |
| F12 | Launch stagger (`MapComponent_MissileLaunchQueue`): four launches requested in one tick spawn at +0/+6/+12/+18 ticks and the queue drains |
| R1 | Empero long-range gun (`DMSE_Turret_Empero`, CompArtilleryStrike): defs, 10x4 head on FacilityBase, targeting rules (own tile, range 20, other layer), inspect line without a radar, an empty gun stops sustained fire |
| R2 | Unguided Empero shot at an NPC base: 300-tick warmup turns the head to the world bearing, one shell used, departure → world track → base map generated at arrival and kept (`GameComponent_MissileEngage`), shell enters at the edge facing the gun and aims within scatter 14 of the centre |
| R3 | Radar-guided Empero shots: a fire-control radar's free artillery channel is held per shot (one per gun, also on a re-fire before release), scatter 3 around a hostile turret / powered building picked by `FindGuidedTarget`, release after the hold time; with the pool full the gun still fires, unguided |
| R4 | Empero local fire (`CompBuildingTurretGun` + `DMSE_Gun_Empero`): the head turns onto a raider 30 cells away, a reduced-charge plunging shell from the shared shell stock lands near it; world sustained fire clears the local target and holds local fire |
| L1 | Save and reload with a detected track and an interceptor in flight |
| L2 | Save and reload after a reroute, with the old-route interceptor still flying |
| L3 | Old saves (plan §9): a save whose incoming missiles are rewritten to the skyfaller-era `DMSE.MissileIncoming` class loads cleanly; the flying one continues as a `Projectile_MissileIncoming` on its target with config, attacker and strike id; the BVR-held one is converted on load; old departure and interceptor skyfallers still finish. Last in the suite (it reloads); a third-party crash on an earlier reload can cut it, run it alone with `-Only L3` |

## Test environment

- Storyteller incidents are off and `DebugSettings.unlimitedPower` is on. Buildings
  still wait for their power net to switch them on (`BVRFixture.WaitPowered`).
- Tests build on a cleared, paved pad and clean up after themselves (`CleanupAfterTest`).
- The mechanics tests (every id not starting with `T`) pin the radar values they were written against: phased array
  and early fire-control radar 2 channels each, NPC missile base 2 channels (`BVRFixture.PinLegacyRadarValues`).
  T tests restore the shipped values first (24 / 1 / 4), also after a test reloaded the game.
- Enemy world missiles use laser guidance with a permanent enemy mark on a far corner
  of the map, so arrivals never hit the test buildings.
- Some timings use forced hit/miss rolls; real hit chances are only logged as notes.

To add a test, write an `IEnumerable<Wait>` method taking a `TestContext`, then add
it to `AcceptanceSuite.Build()` in `Tests_Seekers.cs`. Use `TickWatch` for events
shorter than a frame at ultrafast speed. A site map you generate yourself (not through
a missile arrival) must be registered in `GameComponent_MissileEngage.times` as
`OnArrived` does; otherwise vanilla removes the empty map and it stops ticking.
