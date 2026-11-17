# Core network authority

Part of [Combat simulator](index.md).

## Core authority and outgoing hits

Migration phase 3b, step 2 is implemented. Core remains transport-free.

1. `BattleSetup` accepts an optional `IReadOnlyCollection<int> localOwners`. It copies and deduplicates nonnegative player indices.
   Omission makes every roster owner local. An empty collection makes none local. `IsLocalOwner(int)` queries the frozen set.
   Owner and side remain separate; local status never changes during a battle. Allegiance (D13) is a third, separate property: see [Allegiance and ship commands](core.md#allegiance-and-ship-commands).
2. For ships and drones, the attacker's owner decides the contact. A local attacker damages a local target, shield first.
   Against a remote target, it emits a resolved outgoing hit. Since step 5 the copy also takes the damage as a prediction (see "Corrections and predicted damage").
   Remote attackers produce presentation predictions without damaging ships or drones from local contacts.
3. A bullet's owner commits every change of that bullet. A contact between two bullets, or between point defense and a
   bullet, counts if either owner's device sees it (network plan 9d; owner, 2026-10-26). Before, only the bullet owner's view
   counted, so one device could lose its missile in a collision the other never saw while the other's missile flew on.
   - Local bullets take damage, stop or disarm normally.
   - Resolver calculations remove a remote bullet by prediction, without changing its hit points. Several weak hits in one
     tick can predict a removal; weak hits across ticks do not accumulate remote damage.
   - When a contact changes a remote bullet, the device that saw it sends an outgoing hit with `TargetIsBullet` to that
     bullet's owner: a local projectile's or ray's damage (`Impact`, `Energy`; a ray sends one `DamageOverTime` record per
     tick), a local point defense's `Disarm`, or `Stop` for a remote projectile that stopped at a local bullet. The attacker is
     always the sender's bullet or ship.
   - The owner applies the record at stage 10 if its bullet is still alive: `Disarm` removes it with `Disposal`, `Stop` with
     `Death`, damage reduces its hit points. A projectile's damage counts once per attacker and bullet (a ray's once per
     tick), whether the owner's own contact or the record supplied it. A record for a bullet that is gone has no effect.
   - Accepted limits (owner, 2026-10-26): the record arrives one one-way delay later. If the bullet hits a ship before that,
     the hit stands, and the device that saw the collision takes a hit from a bullet it no longer shows. Each device runs every
     ship's point defense; a zap that only a ship's copy makes costs the real ship no energy and starts no cooldown until the
     next correction resets the copy's energy and timers.
4. Only local ships and drones commit deaths, retreat completion, forced destruction, and drone inventory losses.
   Public commands cannot deploy, retreat, or self-destruct remote entities. Side destruction affects only local slots.
   Remote drone bays do not launch automatically. Owner records supply remote deployments and launches (step 3).
5. Remote movement, weapon activation, energy, and cooldowns continue to run. Remote armor and shield do not regenerate locally.
   Owner corrections (step 5) repair them.
   Remote projectiles and beams can spawn, stop, expire, or deactivate by prediction.
   `BattleRunner` creates and runs pilots only for local ships and drones. Remote control frames remain accepted.
6. `BattleEvent.Authority` is `Committed` (zero) or `Predicted`.
   A ship-target hit follows attacker authority; a bullet-target hit or disarm follows target authority.
   Bullet spawn and removal events follow that bullet's owner, independently of the hit event.
   A committed hit against a remote ship commits the contact, not the target's health or death.
   Single-player events retain their ordering, text, and field meanings, and all are committed.

`BattleSnapshot.OutgoingHits` is a separate `ReadOnlySpan<ResolvedHit>` with the same borrowed lifetime as the other snapshot spans.
It is empty in single player. Each record copies battle ID, tick, attacker and target IDs and owners, source device index,
target shape order, contact state, impact kind, and amount. No record depends on a live attacker, device, or physics handle.
The source device index is retained on bullets and beams; ramming uses -1. Presentation `BattleEvent.DeviceIndex` keeps its existing point-defense meaning.

1. `HitImpactKind` distinguishes `Impact` (missile and ramming), `Energy` (bolt and point defense), and `DamageOverTime` (beam).
   Discrete hits carry damage and `HitContactState.Start`. Beam records carry damage per second.
   The target owner applies shield and armor rules; the sender resolves ramming damage from pre-step velocities and masses.
2. Beam contacts are keyed by attacker, device, target, and target shape. `Start` includes its tick; `End` excludes its tick.
   A held contact emits no repeated outgoing record. `Stay` is reserved; the current devices do not emit it.
   A changed target or shape ends the old interval before starting the new one. Missing contact or release ends at the current tick.
   Attacker removal after damage ends at the next tick, even when the end record is published in the removal tick's snapshot.
   Per-tick presentation hits remain unchanged.
3. Outgoing buffers reuse their capacity. Tests cover zero allocations after warm-up during beam contact churn.
   Snapshot byte measurements include outgoing records.

Validation (2026-10-22): 35 authority cases and two added scripted-battle checks; all 654 engine-free tests pass.
The complete 8,050-line single-player event/state log matches the pre-change baseline byte for byte.
Its SHA-256 is `518251C30445EA37B539A066AE26D06AB4EBCAC868D8A5AF6B2B09E685231DC9`; side 0 wins at tick 3506.
Physics, Core, Runtime, and Core.Tests compile separately with Unity 6000.0.75f1's compiler and .NET Standard 2.1 references.
Production assemblies treat warnings as errors. Core.Tests retains the existing CS0649 warning for `PilotSchedulingTests.Provider.PilotedSide`.
Live Editor import verified (owner, 2026-10-27).

Step 6 adds Runtime scheduling and result agreement. The relay simulator, gate matrix, and platform run remain steps 7-11.
The existing single-player adapter needs no change because all its owners remain local.

## Inbound owner records

Migration phase 3b, step 3 is implemented. Step 2 is committed as `db162db` in the current submodule history.

1. `BattleSetup.ownerDelays` copies fixed delays by owner. Known roster owners accept 0-5 ticks; omitted values default to zero.
   `OwnerDelay(int)` exposes the value. The target uses the attacker's delay for damage over time.
   Runtime schedules input at original tick + sender delay. Core control frames carry an already-selected target tick.
2. `Battle.Submit(InboundHit)` accepts a resolved hit with a sender owner and a positive sequence number.
   Runtime supplies the sender from its peer binding. Core checks battle, owner, target authority, and numeric fields.
   A live attacker must belong to the sender; an absent attacker does not invalidate the hit.
   Bullet targets are ignored. Missing or removed ship/drone targets take no damage.
   Future hits wait for their source tick. Discrete late hits apply during the receipt tick's stage 10, shield first.
3. Damage-over-time intervals use attacker, device, target, and target shape as the contact key.
   They apply over `[start + D, end + D)`. Start/end boundaries available in the receive batch are processed before damage.
   A late start catches up through the receipt tick, or the known exclusive end, as one impact at stage 10.
   Any resulting death commits at that receipt tick. A late end stops further damage without refunding earlier extra ticks.
   Multiple completed intervals received after a blackout remain separate. Held contact needs no repeated records.
4. `Battle.Submit(senderOwner, OwnerOperation)` consumes records from the reliable ordered event stream.
   `RosterChange` includes the related entity spawn/removal in one atomic operation. `DroneLaunch` and `Remove` cover other entity changes.
   Operations carry battle ID, owner, sequence, original tick, and entity identity. Sequence numbers start at one per owner.
   Hit and operation sequences are separate. Duplicate sequences are ignored; sparse sequence tracking retains gaps without retaining the whole history.
   Roster revisions add another guard: duplicates do nothing, and later revisions wait for missing revisions before changing entities or slots.
5. Roster records copy status, reason, deployment count/order, retained armor/shield deficits, and committed damage fraction.
   The receiver preserves the owner's deployment order instead of assigning arrival order. It does not infer committed facts from predicted health.
   A destroyed slot cannot deploy. Reserve destruction creates no entity or removal event.
6. Root and drone spawn records capture the pose at creation, including linear and angular velocity and the original spawn tick.
   Drone records also carry the persistent bay and parent identity. The receiver validates derived identities and uses the agreed definitions.
   Owner choice: late ships and drones start at the recorded pose and velocity, without extrapolation or a new free-position search.
   On-time root deployments apply at stage 1, drone launches at stage 5, and removals at stage 12. Late operations apply before commands.
   Drone launches/losses update persistent inventory once. Mothership removal preserves drones, and redeployment restores their mothership association.
7. `BattleSnapshot.OutgoingOperations` contains local commits captured when they happen, including spawn/removal within one tick.
   `AppliedOperations` contains received commits applied this step, with their original owner ticks for later result agreement.
   Both spans have the snapshot's borrowed lifetime. Received operations produce committed presentation events at the local application tick and never echo outbound.
   A bullet already removed by prediction still publishes its owner's removal in `AppliedOperations`, without repeating the visual removal.
8. Reused buffers keep steady hit and beam-interval processing allocation-free after warm-up.
   Snapshot byte accounting includes both new operation spans. Single-player operation batches remain empty.

Validation (2026-10-22): 32 inbound cases; all 686 engine-free tests pass. Tests include direct exchange between two cores,
late beam boundaries, revision gaps, retained resources, drone inventory, bullet removals, and zero warmed allocations.
The single-player event/state digest remains `518251C30445EA37B539A066AE26D06AB4EBCAC868D8A5AF6B2B09E685231DC9`.
Core, Runtime, and Core.Tests compile separately with Unity 6000.0.75f1's compiler and .NET Standard 2.1 references.
Production warnings are errors. Live Editor import verified (owner, 2026-10-27).

## Fire confirmations

Migration phase 3b, step 4 is committed as `ab26117`; step 3 as `60a0ac4`.
The receiver's bullet and beam set converges on the owner's set. Its copies of remote weapons still fire from predicted input and energy.

1. The owner captures one `OwnerOperation.BulletFired` per projectile and beam, in `OutgoingOperations`.
   It carries tick, entity ID, parent ID, device index, barrel index, and spawn sequence: the inputs of the derived ID.
   A plain projectile also carries its launch (`HasLaunchPose`, 9d P1): the muzzle position and launch angle in `Pose`.
   Beams and homing projectiles carry no pose.
   It shares the per-owner operation sequence, so it stays ordered with removals. Single player captures nothing.
2. `Battle.Submit(senderOwner, OwnerWatermark)` states: "all my operations through tick W are sent; the last has sequence N".
   W takes effect only when every operation sequence up to N has been submitted. Older or repeated watermarks are ignored.
   Runtime sends it as the header of each tick batch (step 6, revised in step 9b). Result agreement in step 6 reuses the same watermark.
3. Each remote ship and drone keeps 16 spawn samples: position, rotation, angle, and linear velocity at stage 5, before spawns.
   Stage 4 changes velocity, so this is the exact muzzle state the owner used. Samples exist only when the battle has remote owners.
4. A fire record applies at the end of stage 5 of its tick, after local spawns; an overdue one applies at stage 1.
   It must name a live parent of the sender and a cannon or beam device with the same barrel. The ID must match the derivation.
   A matching live copy becomes `Confirmed`. A bullet this device removed by prediction, an expired projectile,
   or a bullet whose owner removal is in the same batch is not created. The record still enters `AppliedOperations`.
5. A missing projectile starts at the muzzle of the parent's sample for its spawn tick, with spread from that tick.
   With a launch pose it starts at the owner's muzzle and angle; the inherited velocity still comes from the sample.
   **Re-aim (P1).** A live copy with a launch pose moves to the owner's line: launch pose, sample velocity, advanced from the
   spawn tick to the present without hit tests. Contacts the copy already made stay. The re-aim adds no event.
   It advances on a line, one physics step per missed tick, without hit tests or homing steering. A spawn older than the
   samples uses the oldest one. A missing continuous beam attaches to the remote beam device; a missing shot beam that has
   expired here is skipped. Its spawn event is committed.
   **Adoption (P2).** Before adding a missing bullet, the receiver looks for an unconfirmed copy of the same shot fired at
   another tick (an overdue fire input fires the copy late, with another derived ID). The copy must have the same parent,
   device, barrel and spawn sequence.
   - Continuous beam: the device's attached beam, at any start tick. The owner stopped any earlier beam before this one, so
     the attached beam is a prediction.
   - Projectile or shot beam (phase 5, step 4d): spawn tick within the weapon's shortest shot spacing − 1 ticks (max of
     cooldown and barrel cooldown), so the copy can be no other owner shot. The nearest spawn tick wins, then the earlier one. A wider window would shift every
     copy of a held burst.
   The copy takes the owner's ID, spawn tick and expiry and becomes `Confirmed`; a plain projectile is then re-aimed (P1).
   The body, beam attachment and contacts so far stay. The battle emits a committed `Rename` event (`PreviousId` → `EntityId`);
   the scene moves the bullet view to the new ID. Confirmation changes no recoil or energy; corrections own those (step 5).
   **Timer re-sync (G1).** A confirmed shot at tick T sets the copy's cannon (or shot beam) last shot to T, unless the copy's last shot is at or after
   T + cooldown (a later predicted shot). The barrel shot tick becomes the later of its value and T.
   Reason: an overdue fire input shifts a held weapon's cooldown phase for the whole burst.
6. At the end of stage 5, each live remote bullet whose spawn tick is at or below min(W, present) and is not confirmed
   leaves at once with `RemovalReason.Unconfirmed` and a predicted event. Owners never send this reason.
7. A remote bullet removed locally before confirmation (contact, point defense, expiry, beam stop) enters a predicted-removal set.
   The entry ends when min(W, present) passes its spawn tick: any confirmation for it has applied by then.
8. Owner removal of a live remote beam detaches it from the receiver's beam device. Its cooldown starts at the removal record's tick (G1).

Validation (2026-10-22): 13 fire-confirmation cases; all 699 engine-free tests pass. Tests include matching IDs and positions
on two cores, late additions at the owner's pose, the history fallback, watermark gaps, beam replacement, and zero warmed allocations.
The single-player event/state digest remains `518251C30445EA37B539A066AE26D06AB4EBCAC868D8A5AF6B2B09E685231DC9`.
Physics, Core, Runtime, and Core.Tests compile separately with Unity 6000.0.75f1's compiler and .NET Standard 2.1 references.
Production warnings are errors. Live Editor import verified (owner, 2026-10-27).

## Corrections and predicted damage

Migration phase 3b, step 5 is implemented. Core still has no wire format and no send interval; Runtime adds both in step 6.

1. `Battle.WriteCorrections(owner, peerOwner, CorrectionBatch)` runs after `Step` and describes the last completed tick t.
   It writes each ship and drone of the local owner: pose, linear and angular velocity (`SpawnPose`), armor, shield and energy
   (`ResourceLevel`, G1), and one `PointDefenseTimers` per point-defense device.
   The header carries `AppliedHitSequence`: the last hit sequence from peerOwner that this core has applied by tick t.
2. `ResourceLevel`: full, or an 8-bit level (Level / 256 of the maximum) and whether the recharge delay runs. Armor rounds up
   (at most level 255), so a living ship never reads as empty; shield and energy round to the nearest level. A resource with
   maximum 0 is full; only a recharging resource has a delay.
   `PointDefenseTimers`: ages in ticks at t since the activation stop and since the last query start, or `Ready` when that timer
   no longer blocks any tick after t. A ready import only unblocks; an age sets the timer and marks the device started.
   Enabled is not sent: the copy derives it from the owner's controls and its energy. Cannons, beams and barrels follow fire
   confirmations and removals; a drone bay runs only on its owner. Retreat is the reliable `OwnerOperation.RetreatStarted`.
3. `Battle.Submit(senderOwner, CorrectionBatch)` validates and holds one correction per remote entity. A correction must name a live entity of the sender,
   carry one timer per point-defense device with ages below their ready thresholds, a finite pose, and levels valid for the resource.
   Own entities are never corrected.
   A correction that is not newer than the held or the last applied one is dropped, so a receive batch applies only the newest.
   It applies at stage 1, after received operations and before commands, once its tick has run locally.
4. Each remote ship and drone keeps 16 end-of-tick body states (stage 14) and the engine input of each tick (stage 4).
   Movement error = sent state − local state at tick t. Position uses the minimum image, the angle wraps to ±π, and velocities subtract.
   C1 (owner, 2026-10-27): the error grows while the correction waits, for example when owner and copy turn in opposite
   directions near the opposite heading. Both states at t are re-run to the last completed tick with the copy's engine input and
   free motion (the solver's integration without contacts, force or damping). The difference of the two runs is the error at each
   later tick: it is added to the present body, the body states from t and the spawn samples after t, so a later correction
   measures only the remaining error. The copy keeps its own contact and recoil response; only the error is carried.
   A correction older than the history skips movement and still applies the gameplay part.
5. Resources (G1). Each gets a candidate: the owner's level at t, recharged for the ticks from t + 1 to the last completed tick,
   then minus the replayed damage (item 8). The candidate replaces the copy's value only when they differ by more than 1/256 of
   the maximum. The delay always follows the flag: 0 clears it; 1 keeps a running local delay, or starts the full delay at t.
   A full resource has delay flag 0. Point-defense timers are set from the ages at t.
   Retreat (G1): the owner sends "retreat started" (entity, tick) on the reliable tick batch; it applies at stage 1. The copy sets the
   absolute completion tick; an overdue record turns off its engine and devices from receipt. The owner's roster record completes it.
6. Prediction (N3). A local attacker's hit on a remote ship or drone also damages the copy at once, shield first.
   A predicted armor of 0 never kills; the owner's records decide deaths and roster facts. Remote shield and armor still do not regenerate locally.
7. `ResolvedHit.Sequence` numbers outgoing records per attacker owner from 1; `InboundHit` uses it and has no own sequence.
   The predicted-damage ledger keeps each sent discrete hit and each beam interval until the target owner acknowledges it.
8. After a correction with acknowledgement A at tick t, the copy replays the damage and drains the owner has not applied:
   - A discrete hit replays while its sequence is above A.
   - A beam interval [s, e) shows rate × (min(e, present) − s) locally. The owner applies [s + D, e + D) with the shooter's delay D.
     The replay is the shown ticks minus the owner's ticks through t; the owner counts only the boundaries it has acknowledged.
   - Entries leave when acknowledged (a beam: when its end is acknowledged and t + 1 ≥ e + D) or when the target is removed.
9. Without remote owners, nothing is recorded or replayed. Correction, ledger and history buffers allocate nothing after warm-up.

Validation (2026-10-22): 16 new cases; all 715 engine-free tests pass. Tests cover the seam, a history shift that applies an error once,
stale and held corrections, out-of-history corrections, recharge after a correction, retreat, device timers, rejected records,
late-input drift, discrete and beam replay with D = 3, and zero warmed allocations.
The single-player event/state digest remains `518251C30445EA37B539A066AE26D06AB4EBCAC868D8A5AF6B2B09E685231DC9`.
Physics, Core, Runtime, and Core.Tests compile separately with Unity 6000.0.75f1's compiler and .NET Standard 2.1 references.
Production warnings are errors. Live Editor import verified (owner, 2026-10-27).

## Homing missile sync (9d P3)

Plain projectiles fly straight, so their spawn and launch pose fix their path (P1). A homing copy steers on the receiver's
world, so its target choice and its path drift from the owner's. Measured without P3 (`Results/N-homing-drift-2026-10-26-p3a.md`,
At cap): position error p95 1-3 units by 2-4 s of flight, and 1-7% of the samples on another target, with errors of 10-100 units.

1. **Target records.** Each change of a homing projectile's target at an owner search (every 2 s, and at once after a target loss)
   captures `OwnerOperation.MissileTargetChanged` (missile, target or null, tick). Single player captures nothing.
   On the receiver it applies before the steering of its tick (stage 4); an overdue one at stage 1. The copy takes the target and
   the owner's schedule: next search at tick + 2 s. Between records the copy searches locally. A record for a missile that is
   already gone commits without effect; a target that is gone here makes the copy search at once.
2. **Search phase on adoption.** A copy adopted under P2 moves its next search by the same amount as its spawn tick.
3. **Corrections.** `WriteCorrections` lists every live homing projectile of the owner (`CorrectionBatch.Missiles`), highest priority
   first. A missile never corrected comes first, older first: the early correction after spawn. Otherwise the priority is the ticks
   since its last sent correction times its weight: 4 within one second of flight of a ship of the peer or inside that ship's
   point-defense range, 1 elsewhere. Equal weights give round-robin. Runtime reports each sent missile back
   (`MissileCorrectionSent`). Ships and drones keep every-batch corrections; the interval is shared.
4. **Replacement.** A held missile correction applies at stage 1, after ship corrections, once its tick has run locally. The copy
   takes the owner's position, angle and velocity at tick t, then the homing steering runs again from t + 1 to the last completed
   tick toward the target's present position, without searches or hit tests (`HomingSteering.Advance`). There is no per-missile
   history, so a correction cannot overshoot. Contacts the copy made stay; no event. An older or repeated correction is dropped.
5. The observer reports a missile correction with `IsMissile`; the position and angle errors are the change of the present state.

Measured (`Results/N9d-P3.log`, At cap, seeds 1-3): Homing shooter advantage 0.003-0.018 (P2: up to 0.069), hit position p95
0.20-0.24 radius (P2: 0.51-0.84). The HomingDrones scene (fixture N8-5, 20 drones per side as extra targets; not in the gate
matrix): shooter advantage 0.11-0.18 → 0.030-0.066, hit position p95 1.4-3.0 → 0.36-0.50. Copies older than 0.26 s: target
mismatch 0%, position p95 0.05-0.20 units. About +0.4 KB/s in HomingDrones (6 live missiles). Dogfight gameplay rows are unchanged.
The MissileSwarm stress scene (fixture N8-6, about 100 live missiles per side; not in the matrix) saturates the 300-byte budget at
19-24 missiles per batch; position error p95 stays 0.05-0.32 units up to 6 s of age, and hit position p95 is 0.45-0.49 radius.
Missile-against-missile contacts: before authority rule 3 counted a contact seen on either device, a receiver removed a remote
copy at a contact with its own missile while the owner's missile flew on and hit (0.3-0.7% of the hits in MissileSwarm). With the
rule, 2-13 such hits remain per peer and seed, all within 1-14 ticks of the contact: the owner's hit came before the record.
