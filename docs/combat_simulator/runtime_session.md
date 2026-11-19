# Runtime network session

Part of [Combat simulator](index.md).

Migration phase 3b, step 6 is implemented (2026-10-23). `NetworkSession` wraps the existing runner and uses the production Network channels.
The session supports two fixed owners. Match setup supplies identical battle definitions, per-owner delays, and datagram limits.
Authentication, negotiation, disconnect handling, and Unity Relay integration remain phase 7 work.

## Session API and tick order

1. Construct `NetworkSession(setup, pilots, transport, options)`. `NetworkSessionOptions` binds local and remote owners and selects the reference clock.
   Each roster owner must match that binding. The local owner must be local in `BattleSetup`; the remote owner must be remote.
2. The session exposes `BattleId`, `Tick`, `Snapshot`, `IsCompleted`, `Clock`, and the existing roster, self-destruct, vanish and allegiance command methods.
   Those commands retain their explicit target ticks. `SubmitControls(entity, originalTick, controls)` accepts player controls without an `IPilot`.
3. `Pump(now)` exchanges network data without executing simulation ticks. It works before clock start and after completion.
   Wait for the follower's `Clock.HasEstimate`, then call `StartClock(now, referenceStartTime)` on both endpoints with the same reference epoch.
4. `Update(now)` executes every tick due under the existing paced clock. `Step(now)` explicitly executes one tick without requiring clock start.
   Both APIs take finite, nondecreasing monotonic seconds. The transport belongs to the caller.
5. Receive callbacks copy payloads into reusable queues. Before each step, Runtime decodes queued records, sends due local controls,
   and submits due controls to Core. After stepping, it queues the tick's reliable batch (hits, owner operations) and any due correction.
6. The final step also sends its batch. Continue calling `Pump` or `Update` after completion to deliver final records and acknowledgements.
   `PendingReliablePackets` exposes the remaining reliable queue. `IsFaulted` reflects the existing reliable-channel fault state.

## Match handshake

Phase 7, step 4 (2026-11-13). `MatchHandshake` (Runtime) agrees on the match over the connection that the battle then uses.
A second `PeerConnection` on the same transport would mix sequence numbers, so the handshake owns the connection and stays its receiver.

1. Create it with `MatchHandshake.Host(transport, identity, buildSetup, battleId, seed)` or `MatchHandshake.Guest(...)`.
   `MatchIdentity` holds the game version, the database hash (`IDatabase.ContentHash`) and the serialized fleet.
   The host is side 0, owner 0 and the clock reference; the guest is side 1, owner 1 (phase 7, decision 6 interim).
2. Call `Update(now)`. Each player waits for 8 clock samples (about 2 s), then sends Hello: protocol version (2), game
   version, database hash, source hash, its input delay D and its fleet. D = min(5, ceil(round trip / 2 × 50)) per sender (N1).
   The source hash (`CoreSourceHash`, phase 7, step 9) is SHA-256 over the `.cs` files of `ModulesShared/CombatSimulation`
   and `Scripts/Combat/Integration/Core`, without `Tests` folders, comments and whitespace. It also hashes the fleet format files
   `FleetWebSerializer.cs` and GameSession's `ShipData.cs` (satellite plan, phase 8). A mismatch rejects with `Source`:
   builds of one game version with different combat code. It hashes source, not binaries, so Mono and IL2CPP builds of one
   source agree. `CoreSourceHashBuildStep` writes it to `Assets/Resources/CoreSourceHash.txt` (git-ignored) before every
   build; the editor computes it from the files.
3. A mismatch sends Reject with a `HandshakeRejection` reason. Both players compare versions and hashes themselves, so both
   show the reason. The protocol version is read first; any later Hello layout still gets the reason `Protocol`.
4. The host builds the setup with `buildSetup(MatchPlan)` and sends Setup: battle ID, seed and `SetupHash`. The guest builds
   its setup and sends Accept with its own hash, or Reject (`SetupFailed`) when the build throws. A different hash only
   sets `SetupHashDiffers` on both players, and the lobby logs it (phase 7, step 9, owner 2026-11-14): Mono (the editor,
   macOS builds) and IL2CPP round some derived values differently in the last bits, from equal versions, database and
   fleets. Owner corrections repair that drift. `SetupMismatch` is no longer sent.
5. The host answers Accept with Start: the clock epoch, 1 s ahead of its own clock. State `Ready` on both.
6. `CreateSession(pilots, now)` creates the `NetworkSession` on the same connection (its `PeerConnection` constructor), replays
   the buffered messages and starts the clock. After that, the session pumps the connection; `Update` throws.
7. The peer's reliable messages after its last handshake message (Accept from the guest, Start from the host) belong to the
   session. Until `CreateSession` they wait in a buffer of at most 256 datagrams, as do input and unreliable datagrams.
8. After a Reject, keep calling `Update` until `PendingReliablePackets` is 0, then close the transport.
9. Timeout (step 8, Q4): while Measuring or Exchanging, a peer without any datagram for `LinkTimeoutSeconds` (10 s) ends the
   handshake as Rejected with `Timeout`. The reason is local; no Reject is sent.

`SetupHash.Compute(setup)` walks the setup's object graph by reflection: the public properties and fields of each class and
all fields of each struct (a struct's properties are derived, and `Vec2.Normalized` returns the struct again). Floats count
by their bits. Collections count their elements only. The local owners are left out; the owner delays are added. Shared
objects are hashed once; a cycle counts as a marker. A graph deeper than 64 throws with its member path.
`link.xml` (`Assets/ModulesShared/CombatSimulation/`) keeps the Core and Physics members for IL2CPP.

## Session thread

Phase 7, step 5 (2026-11-14). `SessionThread` (Runtime) runs one `NetworkSession` and its transport on a worker thread, so
Unity frames do not pace the ticks (gate D1). Single-player battles keep the main-thread `BattleRunner`.

1. Create it with `SessionThread(session, transport, clock)` after `CreateSession`. The transport is an `IPumpedTransport`
   (Network): `Update` receives, `Flush` sends. `clock` returns monotonic seconds from the source the handshake used.
2. After `Start`, only the worker touches the session and the transport. `Dispose` stops and joins the worker; the caller then
   owns both again. The main thread never reads `session.Snapshot` while the worker runs.
3. Commands: `Post(SessionCommand)` queues a deployment, retreat, self-destruction, surrender or player controls. The worker
   runs them at the session's tick at the start of its next iteration. Controls that the session refuses (an invalid
   entity, throttle or course) count in `Status.RejectedCommands`.
4. Snapshots: after each tick the worker copies the snapshot (`BattleSnapshot.CopyTo`) into a pooled copy and queues it.
   `TryTake` returns the oldest copy, so no tick's events are lost; `Release` returns it to the pool. The copy is complete
   before it is queued, and the worker never writes a copy that the reader holds. A copy into a warm target allocates
   nothing. A reader more than `MaxQueuedSnapshots` (500, 10 s) behind stops the worker with an error.
5. Loop, as R1: commands, `transport.Update`, `Pump`, then `Step` and publish for each due tick on its own, then
   `transport.Flush`. After the result it calls `Update` until `Dispose`, so the final packets reach the peer.
6. Wait (owner, 2026-11-14): sleep 1 ms at a time until about 2 ms before the next due tick
   (`TickClock.SecondsUntil`), then spin. A sleep is skipped when the longest recent sleep would end inside that margin.
7. `Status`: last published tick, completion, queued copies, catch-up ticks, the largest loop gap and step lateness, round
   trip and clock error.
8. Link loss (step 8): before the result, each iteration checks the link. The first failed check sets `Status.LinkLoss`,
   and the thread stops without an error. It stops pumping, so the peer sees silence. In order:
   `ReaderStalled` (no `TryTake` for the link timeout, counted from the first one, so a scene load does not count: the app
   is in the background or its main thread stalls); `TransportClosed` (`IPumpedTransport.IsClosed` for more than
   `ClosedGraceSeconds`, 0.5 s, so the ticks still step a result that the leaving peer's last batch completes); `Silence` (no datagram
   for the link timeout, default `MatchHandshake.LinkTimeoutSeconds`, 10 s); `Backlog` (`PendingReliableBytes` above
   `MaxReliableBacklogBytes`, 1 MiB: a one-way link, where the peer still sends but receives nothing).

`RelayTransport` sets Transport's limits to the same 10 s: a guest stops connecting after 10 attempts of 1 s, and Transport
closes a link without any packet for 10 s (defaults: 60 attempts, 30 s). `TimedOut` tells such a close from a peer that left.

`RelayTransport` (OnlineDuel) can run on the worker. Unity allows `Allocator.Temp` only on the main thread and inside jobs.
`BeginSend` and `PopEvent` allocate with it, and `NetworkSettings(Allocator.Temp)` too. So `TrySend` queues datagrams in
persistent native lists, and a job sends them before the driver update or flush. The driver uses `PopEventForConnection`,
`Accept` and `Disconnect` directly, which do not allocate. Job scheduling and `Complete` from the worker work in Unity
6000.0.75f1 (EditMode `TransportThreadTests`). Unity documents scheduling only from the main thread, so an IL2CPP build
must confirm it (step 9).

Known limit: a 1 ms sleep takes about 15.6 ms on Windows without a raised timer resolution. The worker then pumps the network
about once per tick: ticks stay on time, but each direction gets up to one tick of extra delay (engine-free test host: round
trip 10-14 ms over an in-memory link).

## Duel combat scene

Phase 7, step 6 (2026-11-14). An online duel runs in `CoreCombatScene` with a `CoreDuelBattle` bound (Q2).

1. `CoreDuelBattle` (Assembly-CSharp) is created on the main thread when the handshake is `Ready`. It creates the session and starts
   its `SessionThread` at once (queue limit 1,500 copies, 30 s), so the network runs while the scene loads. It owns the thread and
   the transport; `Dispose` stops the thread, then closes the transport. `OnlineDuelCombatState` owns it.
2. The battle scene reuses the mapper and the slots of `CoreDuelSetup`'s last `Build`: each `Map` call makes new definitions,
   and the views look up the session's definitions in the mapper that made them. `DuelCombatModel` holds the fleets as
   `ShipInfo` (local ships `CombatSide.Player`) and gives no loot and no experience.
3. `CoreCombatAdapter` with a duel: each frame its `CoreBattleHost` takes every snapshot copy, oldest first, through the same handling as a
   single-player step. Commands go to `SessionThread.Post`. The render alpha is the time since the newest copy was published.
   No enemy commander, kill-all, reinforcement, asteroids or effect preview.
4. Threads: `PlayerPilot` and the player pilot map of `CorePilotProvider` sit behind locks. Player input reaches the trees
   through `SessionThread`'s `beforeStep` hook; ShowMessage texts reach the main thread through a locked queue with their tick.
5. Deployment (owner, 2026-11-14): the player does not wait for the peer's ship. The ship panel opens at once; the first ready
   ship deploys 30 s after it first opened if none is selected, and the timer panel shows the countdown (alarm in the last
   10 s). A posted deployment counts as pending for 1 s. A player has 3 retreats.
6. No pause: `IApplication.AlwaysRunning` keeps `timeScale` at 1 while the menu is open and without focus.
7. Deaths and the result: the core already shows only committed facts. Ship deaths and roster changes come from the owner's
   records, predicted armor 0 never kills, and `Result` is final only after both watermarks. Only bullet removals are predicted.
8. `BattleRenderer.SmoothCorrections`: for ships and drones of other sides, a jump that the last snapshot's velocities do not
   explain becomes a drawn offset that fades with a 0.1 s time constant; satellites take their ship's offset. A jump over
   5 units and a teleport snap. Missiles are not smoothed.
9. A stopped session thread (step 8): the adapter handles the copies already published, then ends the battle. A link loss
   sets `CoreDuelBattle.LinkLoss`; the player's outcome is a draw (decision 3). A thread error ends without a result. Both
   freeze the battle and exit after the usual 3 s. The peer decides for itself: it sees the same loss, or silence.

## Duel lobby

Phase 7, step 7 (2026-11-14). The main menu's *Online duel* button (shown when `CombatSettings.UseNewCombatSimulator` is on)
opens `DuelLobbyScene` as a modal state over `MainMenuState`.

1. `DuelLobby` (`Gui.MainMenu`) hosts or joins over Relay (join code) or a direct link (address, port; also in builds). It
   pumps the transport on the main thread and runs the handshake with the active ship group (Q3); the host draws the battle ID
   and seed. An empty group cannot host or join. While a link or a Relay request is open, the mode toggles and the start
   controls are disabled.
2. At handshake `Ready` the lobby creates the `CoreDuelBattle`, fires `StartOnlineDuelSignal` and closes. A modal state's parent
   cannot load a state while suspended, so `MainMenuState` keeps the duel state in `_loadOnResume` and loads it when the lobby
   unloads. The session thread runs meanwhile; its queue holds 30 s.
3. Back: `CoreCombatAdapter.OnCompleted` sets `CoreDuelBattle.Result`. `OnlineDuelCombatState` stores the outcome in the
   singleton `OnlineDuelResult` and loads the main menu, which reopens the lobby; the lobby shows the outcome in its status
   line. A session that stopped without a result shows "no result".
4. Timeouts (step 8): a guest on a dead address gets "does not answer" after about 10 s (Transport's connect limit), as
   does a peer that goes silent in the handshake. A lost link in the battle shows "connection lost, draw"
   (`OnlineDuelOutcome.Disconnected`). The host waits for a guest without a limit.

## Controls

1. Existing pilot delay remains separate from network delay (owner decision, 2026-10-23).
   A pilot reading snapshot N schedules controls for `t = N + PilotSchedule.Delay`. Runtime sends them at t.
   Both copies apply them at `t + BattleSetup.OwnerDelay(sender)`. Pilot periods and offsets remain unchanged.
2. Player input through `SubmitControls` uses its supplied original tick directly. Controls carry course, throttle, and system bits.
   Self-destruction remains an owner command at the delayed apply tick; the resulting removal travels reliably.
3. Runtime runs pilots only for local entities. The remote copy keeps its last applied controls between frames.
   Each remote entity tracks the original tick of its last applied frame. Duplicate and older frames are discarded.
4. Future frames wait for their own apply ticks, regardless of arrival order. Among overdue frames, only the newest applies at the present tick.
   A frame scheduled for the present tick takes precedence over overdue frames. A wholly overdue press/release pair can disappear remotely.
5. Runtime assigns fresh core command IDs locally and checks the sender against the target owner.
   An inbound roster deployment can receive controls in the same tick. Missing entities cannot be controlled.
   An entity whose received removal or roster exit is overdue takes no controls: the exit applies in stage 1, before controls.
6. `BattleRunner` remains the single-player entry point, with no peer, correction traffic, or added network delay.
   The same runner performs simulation and pilot dispatch in both modes. Existing pilot timing and the single-player digest remain unchanged.
7. **Quantized, change-only input (owner decision, 2026-10-24; network sessions only).** Bandwidth step 9a measured raw input at 83% of the traffic.
   - The course is rounded to a whole degree, the throttle to 1/127 steps. The owner applies the rounded frame too, so both copies fly the same controls.
   - Hysteresis: the held degree stays while the raw course is within 0.75° of it. A course near a half-degree boundary does not alternate.
   - Runtime applies and sends a frame only when its rounded value changes. The core keeps the last frame in force, so an equal frame is a no-op.
   - A change goes into the next 3 input datagrams. Each held frame is repeated once per 25 ticks, staggered by control index.
     The repeat keeps its original tick, so a receiver that already applied it drops it. It repairs a change whose 3 copies were all lost.
   - A control index names a ship or drone. Each peer counts the sender's spawn operations (deployments, drone launches) in sequence order.
     The reliable stream is ordered, so both counts agree; no index travels on the wire. Inputs drain after the batch's reliable records.
     A record whose index has no spawn yet is dropped; a copy or the repeat delivers it later.
   - Self-destruction stays local: a frame that only sets it is applied by the owner and not sent.
   - Measured (dogfight, owner A): input 119.8 → 2.5 KB/s, 149 changes per second. Late changes now move the remote copy by a whole degree.
     At cap, correction errors grow about 50–80% and still pass; shooter advantage, already failing, grows (see the network plan).

## Session wire format and corrections

1. Runtime messages are bit-packed (`BitWriter`, `VarUInt`): input and corrections are unreliable sections, tick batches are reliable messages.
   Network sends all of one tick's messages in one datagram (G3).
2. Input uses its own bit-packed section (`InputCodec`, version 1), not this header: version (8 bits), battle ID low 32 bits, base tick,
   then records until zero padding. A record holds control index, base tick − original tick, course (9 bits; 511: none), throttle (7 bits)
   and systems. Index, tick offset and systems are `VarUInt`. The sender is the owner of every record.
   **Tick batch (reliable, G2, `SessionCodec` version 13).** One reliable message per tick holds the tick's operations in sequence
   order, then its hits. Header: version (8 bits), tick and last operation sequence (`VarUInt`). No battle ID: the connection is
   bound to one battle. Version policy (owner, 2026-11-06): nothing is released, so format changes do not bump the
   version; it stays 13 until the first release sets one version and drops the per-version history below. The receiver
   accepts only its own version, so going back to an older number would let mismatched builds misread each other.
   Then records until zero padding: kind (4 bits from version 8, from 1), record tick − batch tick (zigzag `VarUInt`; a beam
   end record is one tick ahead), then the record fields. The sender is the owner of every record.
   - **Names by stream count.** The stream is ordered and exactly-once, so both peers count its records the same way (`StreamNames`).
     A ship or drone is named by its control index (spawn-record order). A bullet is named by its age: the latest bullet index minus
     its own; each bullet-spawn record takes the next index. A removed bullet keeps its index until the end of its batch, so a hit of
     the same tick can still name it. Operation sequences are consecutive and end at the header's value; a hit's sequence is its
     position in the stream.
   - Bullet spawn: parent (1 kind bit: control index or bullet age), device, barrel and spawn sequence (`VarUInt`), launch flag
     (1 bit). With the flag (plain projectiles, 9d P1): muzzle position per axis on the correction grid (1/1024 unit, bit count
     from the arena) and launch angle (14 bits); 51 bits in a 200-unit arena. The receiver derives the entity ID with
     `SpawnIdentity.CreateChild`; Core checks it. A child bullet (phase 5) names its parent by bullet age, has the trigger index
     as its device and barrel 0, and always has the launch flag. Its tick can be the batch tick + 1 (see [Bullet triggers](core_weapons.md#bullet-triggers)).
     Then a charge flag (1 bit; version 10, phase 5 step 4f) and, with it, the charge ticks of a charging weapon's shot
     (`VarUInt`; 1000 ticks take 16 bits). A child takes its parent's charge, so it never sends one.
   - Removal: 1 kind bit, reason (3 bits; `Vanish` from version 8), control index or bullet age. A drone's bay comes from its launch record.
   - Hit: attacker (1 kind bit plus index or age), target (1 kind bit, then a control index in the receiver's stream or, for a
     bullet, its 64-bit entity ID: the sender may know only a predicted copy, which no stream names), shape and device + 1
     (`VarUInt`), contact (2 bits), impact (3 bits), amount (32-bit float: the target owner commits damage from it), damage
     type (`VarUInt`, below `DamageTypeTable.MaxTypes`; version 6, D4). The target's core rejects a type its table lacks.
     Then an impulse flag (1 bit) and, with the flag, the push impulse (two 32-bit floats; version 7, phase 4 E4). Only a
     projectile's start record on a ship carries one; the target's core rejects it elsewhere and applies it on arrival.
     Then the effect (4 bits, `HitEffect`; version 11, widened in phase 5 step 5e): damage, drain, motion, progressive
     damage, repair or recharge. Repair and recharge name a ship target, with damage type 0 and no impulse.
     Version 12 adds shield bypass: `DamageIgnoringShield` keeps the damage type and optional projectile push.
     `ProgressiveDamageIgnoringShield` keeps the ramp time. Both name ship targets; bullets and segments use ordinary damage.
     Version 13 adds `Teleport`: distance on entry for a ship, destruction for a tail segment. It has no damage type or impulse.
   - Retreat: ship control index. Roster changes and drone launches are rare and keep full-width IDs, records and poses.
   - Missile target (kind 7, 9d P3): missile bullet age, a segment flag (1 bit; version 11, phase 5 step 5a), then the target
     control index + 1 in the receiver's stream (`VarUInt`; 0: no target), or with the flag a sticky bullet's tail segment as
     its 64-bit entity ID. About 1.5 bytes in a real batch.
   - Allegiance (kind 8, version 8, phase 4 D13): ship control index, then the allegiance (3 bits). About 1 byte.
   - Device use (kind 9, version 9, phase 5 step 7a): ship control index, device index (`VarUInt`, below 64), started (1 bit).
     About 2 bytes. Its tick can be the batch tick + 1 (an InfinityStone use that lethal damage started).
   - The sender throws on a record its streams cannot name or on a sequence gap: that is a session bug.
   - The receiver decodes the whole batch before it submits any record, so a malformed batch submits nothing. Its records named
     entities by count, so the stream stops there: every later batch is dropped too. Its operation sequences stay missing, so the
     result cannot finalize.
   - Measured (dogfight, owner A, Perfect): bullet spawn 39 → 1.9 bytes, removal 21 → 3.0, hit 44 → 8.2, header with length prefix
     10.5 → 7.2. Reliable channel 4.05 → 0.86 KB/s, most of it datagram framing; G3 removed that framing.
   **The batch is the watermark.** Its header says "all my records through this tick are sent; the last operation is N".
   A tick without hits or operations sends a batch only every 5 ticks (100 ms heartbeat), and every tick while a candidate
   or final result exists. Cost: a remote bullet the owner never fired leaves up to 4 ticks later in quiet periods.
   Measured reason (step 9a): a separate watermark every tick was 49 messages/s, and each message used its own datagram.
3. Owner-operation payloads preserve roster records, spawn poses and velocities, drone bay identities, removal reasons, fire identity inputs,
   retreat starts (entity and tick only), missile targets (missile, target, tick) and allegiance changes (ship, allegiance, tick). Hit payloads preserve contact boundaries, authority fields, and resolved damage.
4. **Corrections (G1 and 9d P3, `CorrectionCodec`, version 2).** One unreliable section per batch: version (8 bits), battle ID low
   32 bits, batch tick, applied hit sequence and missile count (`VarUInt`), the missiles, then bodies in increasing control index
   order until zero padding.
   The configured interval is 100, 150, or 200 milliseconds; default 100. At 50 Hz, 150 milliseconds alternates eight and seven ticks.
   - A body is named by its control index, as the gap to the previous index (`VarUInt`). No counts travel: both peers take the
     definition from the spawn record of that index.
   - Pose: position on a 1/1024-unit grid (18 bits per axis in the 200-unit arena; the bit count follows the arena size), angle 14 bits,
     linear velocity 15 bits per axis on a 1/128 unit/s grid, angular velocity 14 bits on a 1/512 rad/s grid. Velocity field 0 escapes to a 32-bit float.
   - Armor, shield, energy: no bits for a maximum of 0; 1 bit full; if not full, 1 bit "delay running" (only for a resource that recharges) and an 8-bit level.
   - Point-defense timers only, stop then query: 1 bit ready, otherwise the age in ticks (`VarUInt`). Other devices and barrels send nothing.
   - Missile (P3), in increasing bullet index order: the absolute bullet index of the sender's stream as the gap to the previous
     one (`VarUInt`; the stream age does not work on an unreliable channel), position on the body grid, angle 14 bits, a velocity
     flag and, with it, velocity as for a body. Without the flag the missile flies at `Speed` along its angle: the owner sends the
     velocity only while it differs from that by more than 1/64 unit/s. 52 bits without velocity, about 83 with it. The receiver
     skips an index it does not know.
   - Missile budget: `NetworkSessionOptions.MissileCorrectionBytes`, default 300 (a placeholder; the owner sets it from a playable
     scene), at most half of the datagram payload. Runtime takes Core's missiles in priority order while their bits fit, counting
     each with its absolute index. The missiles go in the batch's first section only.
   - A batch that does not fit continues in more self-contained sections. Both endpoints use the same datagram size, default 1,200 bytes.
     The batch byte limit defaults to 1 MiB and is configurable. An empty batch is not sent.
5. Each section is submitted on its own. Core drops a body that is not newer than the correction it holds or applied, so a late or
   repeated section changes nothing. Reading stops at the first index without a spawn record: later indices are unknown too.
   A lost section is repaired by a later correction, without reliable retransmission.
6. Measured, dogfight sender A (`Results/N-bandwidth-2026-10-25-g1.md`): corrections 13.3 → 1.8 KB/s, 273 bytes per batch for 21 bodies.
7. Invalid versions, headers, lengths, owners, enum values, booleans, or numeric payloads are rejected before submission and counted in `InvalidMessages`.
   Core retains its semantic authority checks. `SentCorrectionBatches` counts sent batches; `ReceivedCorrectionBatches` counts newer decoded batch ticks.
8. Buffers, receive slots, and scheduling lists retain capacity. Stable warmed sessions with active pilots and corrections allocate zero bytes.

Validation: 37 added cases cover codecs, input selection, delay combinations, correction fragmentation, clocks, result agreement, and actual combat traffic.
The lossless combat test includes cannon fire, homing missiles, beams, point defense, drone launches and deaths, and compares every owner operation
through the result tick with its received copy. Full gate scenes and network profiles remain steps 7-9.
All 752 engine-free tests pass. The single-player log retains SHA-256 `518251C30445EA37B539A066AE26D06AB4EBCAC868D8A5AF6B2B09E685231DC9`.
Physics, Network, Core, Runtime, Core.Tests, and Runtime.Tests compile separately with Unity 6000.0.75f1's compiler and .NET Standard 2.1 references.
Production warnings are errors. Core.Tests retains its existing CS0649 fixture warning. Unity metadata is supplied; live Editor import is not verified.
