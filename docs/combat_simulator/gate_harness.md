# Gate harness

Part of [Combat simulator](index.md).

`Experimental/CombatSimulationHarness/` is a throwaway .NET console project. It compiles the engine-free Physics, Core, Network, Runtime, Diagnostics, and Utilites sources directly. The default command runs the physics gate scenes and checks their limits. It is outside `Assets/`, in its own private repository, not in the game repository. Game code never references it.

```
dotnet run -c Release --project Experimental/CombatSimulationHarness
```

It prints a table of measured values against the limits, and it writes the same table to `Results/P1-<date>.md` with the harness version and a state hash per scene. Two runs of the same build give the same hashes. Each scene runs in the arena middle and on the wrap corner. With `-- --replay` it also records each scene, and `Experimental/CombatSimulationHarness/Viewer/index.html` plays the recordings step by step (harness README).

## Network relay and virtual clock

Migration phase 3b, step 7 is implemented (2026-10-23). Run the infrastructure checks with:

```powershell
dotnet run -c Release --project Experimental/CombatSimulationHarness -- --network-smoke
```

1. The relay provides two `ITransport` endpoints, pooled datagrams, and independent seeded `PcgRandom` streams per direction.
2. All six gate profiles use the documented latency, uniform jitter, loss, and sender delays. Selected reordered packets receive 100 ms extra delay.
3. Blackout drops sends and in-flight arrivals in both directions during each 500 ms outage, starting every 30 seconds after match start.
4. The runner advances virtual time by 1 ms without sleeping. It uses separate local clock origins and production `NetworkSession.Update` pacing.
5. Clock probes warm up for four seconds. Both clocks start against a reference epoch at five seconds. Missing synchronization fails explicitly.
6. Samples retain actual peer ticks and pacing rates. The no-jitter asymmetric check exposes the existing two-tick bias.
7. Completed sessions pump until reliable packets are acknowledged. The relay then stops new sends and drains queued traffic, with bounded deadlines.
8. Validation: 22 harness self-checks pass. They cover relay semantics, deterministic replay, reliable fragmentation and recovery, clock drift, and result agreement.
9. Each of the six profiles runs twice for three virtual minutes with a small moving two-ship fixture. Per-peer snapshot and packet hashes repeat.
10. All 752 engine-free tests pass, including the unchanged single-player log. The harness retains five existing build warnings.

Production APIs and sources are unchanged. The harness README specifies scheduling, counters, and timeout diagnostics.
The step 7 fixture has no weapons. Step 8 adds combat scenes and measurements below; the 168-run matrix remains step 9.
Android integration remains step 10. Phase 3b stays in progress. Step 7 adds no submodule change or commit.

## Network combat scenes and diagnostics

Migration phase 3b, step 8 is implemented (2026-10-23). It supplies seven scenes and individual gate measurements.
This does not complete the gate matrix, select a correction interval, or establish Android parity.

```powershell
dotnet run -c Release --project Experimental/CombatSimulationHarness -- --network-scenes
dotnet run -c Release --project Experimental/CombatSimulationHarness -- --network-metric-checks
```

1. `Battle.Observer`, `BattleRunner.Observer`, and `NetworkSession.Observer` expose the same optional `IBattleObserver`.
   Callbacks run synchronously. Observers must not mutate or re-enter the simulation; exceptions propagate to the caller.
2. Observations are immutable value copies. Core retains no diagnostic log and adds no snapshot fields or network bytes.
   Callbacks cover applied corrections, accepted contacts, homing projectile sweeps, processed inbound hits, and applied beam intervals.
3. Correction observations contain source and application ticks, the actual error before shifting history, and a missing-history flag.
   Resource observations compare values before correction with values after recharge and pending-hit replay.
4. Contact observations retain device, shape, owner, time, and entity identity. Homing observations capture motion before physics and removal.
   Hit observations include records processed after the target disappeared. Beam observations distinguish interval ticks from application ticks.
5. Disabled observers allocate nothing. Enabled counter observers allocate zero bytes after warm-up during correction and beam/contact tests.
   Diagnostic callbacks do not change simulation or packet hashes.

The harness runs dogfight, missile salvos, beam duels, ramming, drone churn, homing missiles, and last-ship mutual kill.
Its definitions use the production devices and public commands. Churn destroys drones, retreats depleted carriers, and deploys fresh reserve carriers.
The mutual-kill script fires side B one tick earlier because the runner updates A first. This gives a perfect-link draw.
The same script applies to every profile; actual committed ticks still determine impaired-link results.

1. Workload windows are `[20,170)` seconds for dogfight and `[20,50)`, `[80,110)`, `[140,170)` for churn.
   Both peers must reach median one ship and 20 drones per side, plus at least 900 live projectiles, in every window.
2. Reports show whole-run and window counts. Beams have separate counts. Either peer's underload disqualifies both workload-dependent verdicts.
3. Movement uses the actual correction and hull radius. Missing history and missing homing copies remain visible.
   Armor and shield correction changes are informational. No limit is inferred for them.
4. Reliable comparisons retain original commit ticks and serialized operation payloads. Duplicates and missing hit records have separate counts.
   Incomplete runs cannot pass final bullet-set agreement. No diagnostic cleanup changes the final sets.
5. Beam-start damage excludes measured late-end excess. The excess bound uses the actual end-processing tick minus the end tick and sender delay.
   Reports retain raw excess ticks, including excess under nominally good profiles. They do not equate nominal latency with reliable delivery time.
6. Point-defense strength uses the same numbered salvos over a perfect link with D=0.
   Bandwidth uses decimal KB and non-overlapping one-second datagram windows, including retries, framing, acknowledgements, and clock probes.
7. `Results/N-<date>.md` records source hashes, repository versions and dirty state, per-peer state and packet hashes, samples, limits, and verdicts.
   The README defines commands and metric applicability. Harness sources and results remain outside Git.

Validation: 760 engine-free tests, 22 infrastructure checks, and eight metric checks pass.
The single-player digest remains `518251C30445EA37B539A066AE26D06AB4EBCAC868D8A5AF6B2B09E685231DC9`.
Physics, Network, Core, Runtime, Core.Tests, and Runtime.Tests compile independently with Unity 6000.0.75f1's compiler and .NET Standard 2.1 references.
Production warnings are errors. Live Editor import verified (owner, 2026-10-27).

Focused validation comprises 20 scene runs and two additional point-defense baselines, at 150 ms and seed 1.
All seven scenes run on Perfect and At cap. Extra runs cover blackout beam boundaries, above-cap drift, asymmetric clocks, and Good point defense.
Blackout and asymmetric repeats retain identical state and packet hashes.

The gate does **not** pass. Workload traffic reaches about 151–153 KB/s per direction against the 16 KB/s median limit.
At-cap dogfight and churn exceed the completion deadline with reliable records still queued; above-cap dogfight also times out.
At-cap churn misses one peer's final drone-count requirement. Shooter-advantage and homing comparisons fail or lack required copies.
At-cap mutual kill leaves different final bullet sets. The asymmetric run reports a three-tick p99 offset against the one-tick limit.
These findings remain visible for the gate review. Step 8 changes diagnostics only; it does not redesign the protocol.

## Network gate matrix and roster extension

Migration phase 3b, step 9 adds a desktop batch runner in the local harness.
Production APIs, gameplay rules, and the wire format are unchanged.

```powershell
dotnet run -c Release --project Experimental/CombatSimulationHarness -- --network-gate
dotnet run -c Release --project Experimental/CombatSimulationHarness -- --network-roster
dotnet run -c Release --project Experimental/CombatSimulationHarness -- --network-gate-checks
```

1. The matrix runs seven scenes across six profiles and seeds 1–3 at 150 ms: 126 runs.
   At cap also runs all seven scenes and seeds at 100 and 200 ms: 42 runs.
2. Nine Perfect-link D=0 salvo baselines are cached by seed and interval. These auxiliary runs do not count toward the matrix.
3. Each run appends detailed evidence before the next run starts. Failed limits and simulation timeouts do not stop the matrix.
   Summaries retain separate failed, unmeasured, and underloaded row counts. They never average failures across seeds.
4. Interval eligibility requires every applicable At-cap row to pass for all seven scenes and three seeds.
   Selection also requires passing 150 ms cross-profile coverage and roster checks. Otherwise, the result is no passing correction interval.
   Cross-profile behavior at 100 and 200 ms is not established by this matrix.
5. The owner selected At cap, seeds 1–3, and 150 ms for the six roster case groups.
   Eight session subcases cover deploy/retreat/redeploy, two surrender variants, duplicates, two delayed-deploy variants, a ready reserve, and a missing record.
6. Intermediate assertions run before terminal cleanup. They compare committed revisions, deployment order, retained resources, active IDs, and result events.
   Combat destroys ships through the production beam device. Reserve surrender must not create synthetic entity events.
7. Harness-only transport decorators force duplicate delivery and hold operation sequence 1 while later terminal traffic arrives.
   The held packet and its retries remain blocked for 12 match-relative seconds. Standard matrix runs do not install these decorators.
8. Nine direct Core probes separately test duplicate revisions, non-owner rejection, and watermark delivery before a roster gap.
   These use public `Submit` and `Step`; they make no transport claim.
9. One missing-record session repeats to verify both state hashes, relay hashes, and injected-delivery hashes.
   Roster reporting therefore contains 34 verdicts: 24 sessions, nine Core probes, and one repeat check.

The harness is its own private Git repository (`PavelZinchenko/event-horizon-combat-simulation`, since 2026-10-27), with
`Results/` tracked. The game repository does not include it.

Since 9d P3 the matrix also runs the HomingDrones scene on the same 24 profile, seed and interval cells: 192 runs, reported apart
from the original 168. `--network-gate --tag T` writes `Results/N-<date>-T.md`.

## Network gate result

Migration phase 3b is done (2026-10-28). Every limit has a verdict; the full table, the changes that made limits pass and the
accepted defects are in `TODO/battle_simulator_network.md`, "Gate result".

1. Desktop (`Results/N-2026-10-27-9e.md`): 187 of 201 runs pass (192 matrix runs and 9 D=0 baselines). Roster 34/34.
   Outcome agreement, exactly once, bullet sets, drift, ramming, homing, recovery, point defense, beams and bandwidth pass in
   every run. Bandwidth median at most 9.6 KB/s, payload plus Relay header (limit 16).
2. Failing rows: shooter advantage at cap in 3, 5 and 7 of 42 rows at 100, 150 and 200 ms (accepted, owner); one Above-cap
   position row; one Above-cap tick offset row. The asymmetric tick offset is an expected failure.
3. Correction interval: none passes every row. The owner chose 150 ms (2026-10-27); it is the session default.
4. R1 (`Results/R1-2026-10-27-spin.md`): `--r1-host` on the desktop, `--r1-peer` on the phone over TCP and `adb reverse`, At cap,
   150 ms, Dogfight and Churn. 4 of 8 runs pass; only shooter advantage differs from the desktop verdict. The phone's session
   is replayed on the desktop: 0 differing tick hashes in 19 phone runs, so the full core is bit-identical on IL2CPP ARM64.
5. A coarse session loop raises shooter advantage and tick offset (R1 D1). A session driven by Unity frames needs its own thread
   with short loop gaps: [Session thread](runtime_session.md#session-thread) (phase 7, step 5).
