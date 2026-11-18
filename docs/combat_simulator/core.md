# Core foundation

Part of [Combat simulator](index.md).

`BattleSetup` holds immutable battle ID, match seed, the initial units, arena dimensions and the game rules (`IBattleRules`, [Battle modes](battle_modes.md)). Dimensions default to 200 × 200 and must be positive and at most `MaxWorldSize` (2048).
The units are a read-only copy of the caller's list; the list can be empty, because a mode can add units. Each `UnitSetup` has a unique roster ID, a side (0 or 1), an owner (player index), a `ShipDefinition` and an allegiance (default `Allegiances.OfSide(side)`). `RosterRules` needs at least one unit on each side.
`ShipDefinition` holds the hull, mass, the engine, an optional engine without energy, maximum armor, the shield and energy `ResourceDefinition` (maximum, recharge rate, recharge delay), barrels and devices.

The hull is a list of `HullShape` (phase 4, D5): 1 point is a circle, 2 points a capsule, 3 or more an outline. Only circles and capsules have a radius. The `HullShape` constructor validates the shape and splits an outline with `ConvexDecomposition`, so an invalid shape throws at setup. `HullParts` lists the physics parts (`HullPart`: circle, capsule or convex polygon) in shape order, each shape's parts in decomposition order. Mass is set by the definition; the centre of mass and the inertia come from the parts. `HullRadius` is the farthest hull point from the body origin. `HullHash` hashes every part's geometry bit by bit, in order; the phase 7 handshake compares it so both duel peers build the same hull. A second constructor takes a radius and extra circles, for code fixtures.
`EngineDefinition` holds derived stats in core units: propulsion (u/s²), maximum velocity (u/s), turn rate (rad/s²) and maximum angular velocity (rad/s). Phase 4 derives them from engine power and weight.
Each `Battle` owns a separate `PhysicsWorld`. Physics state is internal; the public surface exposes setup, battle ID, tick, `IsCompleted`, `IsStopped`, `Ceasefire`, `Rules`, `Snapshot`, the `Submit` overloads and `Step()`.

`Tick` identifies the next tick to execute. It starts at zero. The first `Step()` processes tick zero and leaves `Tick` at one.
The stage order is commands, systems, modifiers, motion, spawn requests, queries, physics, contacts, response, damage, deaths, structure, battle flow, publish.
Stage 3 collects the device statuses (see [Device statuses](core_devices.md#device-statuses)). Stage 6 queries beam rays and point-defense overlaps before physics advances by `0.02f` seconds with eight substeps.
Publish writes the snapshot and event batch of the current tick, then increments it.
Runtime owns pilot scheduling and network pacing through `TickClock`.

## Identity

`BattleId` and `EntityId` wrap unsigned 64-bit values. `UnitId` wraps an unsigned 32-bit value.
They provide value equality, unsigned comparison and allocation-free hash codes. Entity ID zero means no entity.
Battle and roster ID zero remain valid. The caller supplies battle IDs; the core does not generate them from a clock.

Internal `SpawnIdentity` hashes these fields using 64-bit FNV-1a, then applies the Murmur3 `fmix64` finalizer:

| Spawn | Fields, in order |
| --- | --- |
| Root | Tag byte 1, battle ID (64 bits), roster ID (32 bits), deployment number (32 bits), spawn tick (64 bits) |
| Child | Tag byte 2, parent entity ID (64 bits), spawn tick (64 bits), source index (32 bits), slot (32 bits), sequence (32 bits) |
| Asteroid | Tag byte 4, battle ID (64 bits), index in `BattleSetup.Asteroids` (32 bits) |
| Zone | Tag byte 5, battle ID (64 bits), index in `BattleSetup.Zones` (32 bits) |

All numeric fields use little-endian byte order. The offset basis is `14695981039346656037`; the prime is `1099511628211`.
Arithmetic wraps at 64 bits. A zero result becomes one.
FNV-1a alone leaves each low output bit a function of the same low input bits. The finalizer is a bijection that mixes every bit, so `Value mod P` is safe for think offsets.
The source index is the device index on a ship parent and the trigger index on a bullet parent. The slot is the barrel or bay index; a bullet parent uses 0.
The sequence counts spawns with the same parent, tick, source and slot, in creation order. It covers a device that fires more than once in a tick.
On a bullet parent the sequence counts all children of the parent with the same spawn tick, across its triggers (phase 5): unique, and simpler to keep than one count per trigger. Spawn ticks must be nonnegative; a child requires a nonzero parent ID.
A roster deployment creates one root, so its spawn slot is implicit. The caller changes the deployment number for each redeployment. The spawn tick is redundant with the deployment number; it stays so that roots and children use the same tick rule.
The hash uses no allocation history, shared random sequence, or runtime hash code. Match seed affects future random behavior, not identity.
Fixed test vectors pin the byte layout. The network gate must still validate this derivation.
The `StableHash` helper holds the FNV-1a and `fmix64` code; spawn identity and counter-based random share it.

Ships are pooled `ShipEntity` objects in a `ShipStore` with a LIFO free list. The internal storage handle is kind, index and generation. `EntityRegistry` maps each live `EntityId` to its handle.
Registering an ID that is already alive is a defect. It throws when the internal `ThrowOnDefect` flag is set (tests set it). Otherwise it logs through `GameDiagnostics.Trace` and the spawn fails: the deployment is rejected and the slot stays `Ready`.
A removed entity's ID leaves the registry and never returns to life. Stages visit roster ships in setup order, then drones in entity ID order. Storage allocation order does not determine processing order.

## Counter-based random

`CounterRandom.Create(matchSeed, entityId, tick, purpose)` returns a `Utilites.RandomState` seeded with `PcgRandomAlgorithm.InitializeState` of a `StableHash` over tag byte 3, seed (64 bits), entity ID (64 bits), tick (64 bits) and purpose (32 bits).
The caller draws a fixed sequence from that state. A draw never depends on how many draws other entities made. Purposes are appended to `RandomPurpose`; existing values never change.

## Units

A unit (code: `Unit`, `UnitId`, `UnitStatus`; "roster slot" before the 2026-11-17 rename) is one fleet slot: a ship that can deploy, retreat and deploy again. The text below still says "slot" and "roster" for it.

A slot is `Ready`, `Active` or `Destroyed`. Its status is stored; it is never inferred from whether an entity exists. A pending deployment is a command, not a status.
Each slot counts its deployments, records its battle-wide deployment order (the last activated ship has the highest) and increments a revision on every transition.

| Transition | When | Entity |
| --- | --- | --- |
| `Ready` → `Active` | Deploy, stage 1 | Spawn with root ID `(battle, roster, deployment count + 1, tick)` |
| `Active` → `Ready` | Retreat completion, stage 12 of tick `start + 350` | Removed; the slot keeps the armor and shield deficits (B4) |
| `Active` → `Destroyed` | Death, stage 12 | Removed |
| Any → `Destroyed` | Surrender (own side) or kill-all (other side), stage 12 | Only active entities removed; reserves get no entity |

Stage 12 applies one batch in roster order: deaths, then retreat completions, then surrender and kill-all, then the mode's `DestroyUnit` actions. A mode can also add a unit and return a destroyed unit to `Ready` ([Battle modes](battle_modes.md)). A death in the completion tick wins over the retreat. A repeated destruction is a no-op.
A deployment in the surrender tick is destroyed in the same batch. Death has no damage source yet: the internal `QueueDeath(EntityId)` feeds stage 11, and the damage stage will use the same list.

Spawn placement follows legacy `Scene.FindFreePlace` and `ShipInfo.Create`, with counter-based random (purpose `SpawnPlacement`, the new root ID and the tick):

1. Rotation: a whole degree from 0 to 359.
2. Up to 100 uniform candidates over the torus. The first candidate at 40 units or more from every ship of the other side wins; otherwise the candidate farthest from them.
3. `TryFindFreePosition` moves the body up to 40 units to clear any overlap, allies included.

## Unit commands

`UnitCommand` carries battle ID, command ID, target tick and kind: `Deploy(rosterId)`, `Retreat(entityId)`, `Surrender(side)` or `KillAll(requestingSide)`.
`Submit` rejects a wrong battle ID, a past tick, an unknown kind, an invalid side and a command ID that is already pending. It returns `false` and logs. It throws after the result.
The buffer stays sorted by target tick and command ID. Stage 1 applies the commands of its tick in command ID order and validates them against committed state:

1. Deploy: the slot is `Ready`, and the mode allows it (`IBattleMode.AllowDeploy`; `RosterMode`: the side limit, then `OnlyOneShip`).
2. Retreat: the entity is a live roster ship with no retreat running. The completion tick is fixed at `tick + 350`.
3. Surrender and kill-all: the side is marked for stage 12.

A rejection logs a warning and increments the internal `RejectedCommandCount`. Phase 2 has no rejection event.

## Control frames

`ControlFrame` carries battle ID, command ID, target tick, entity ID, a course in radians or none, a throttle and 64 system bits (bit = device index).
Command IDs share one pending namespace with roster commands and self-destruct commands.
`Submit` also rejects a null entity, a throttle outside [0, 1] or NaN, a non-finite course and a second frame for the same entity and tick.
The entity is checked at stage 1, after the roster commands: a frame can name the derivable root ID of a ship that deploys in the same tick.
A frame for an unknown or removed entity is dropped and counted in `RejectedCommandCount`, without a log: a pilot's frame applies at its think tick + delay, so a ship that dies in between leaves one in every battle.

1. A frame stays in force until the next frame for that entity. A deployment starts with empty controls.
2. At stage 1, before the frames, each ship copies its system bits to `PreviousSystems`. `PressedSystems` and `ReleasedSystems` give the edges; the driver sends no events.
3. `RequestedSystems` keeps the frame's bits. `Systems` equals it, except during a ceasefire ([Battle modes](battle_modes.md#battle-continuity)).

## Ship resources

`ResourcePool` ports legacy `Energy` (`IResourcePoints.cs`) with absolute values. A pool starts full with no delay.

1. Stage 2 updates energy, armor and shield, in that order, before any device. Only the part of the step after the delay recharges.
2. A spend with a positive rate restarts the full delay. A failed `TryConsume` changes nothing. A cost of 0 or less always passes and changes nothing, even with an empty or zero-capacity pool, for every device (owner, 2026-11-06). Legacy `Energy.TryGet(0)` fails there, and only some legacy weapons bypass it (`WeaponBase.TryConsumeEnergy`); the owner treats that as a legacy bug. `Consume` clamps and returns the removed amount; a negative amount adds.
3. A negative rate drains (net consumption). A zero maximum means no resource.
4. Armor repairs at `ShipDefinition.ArmorRepairRate` after `ArmorRepairDelay` (Q7; phase 5, step 13). The mapper gives legacy `ArmorRepairRate` and `ArmorRepairCooldown`: bionic ships (`Features.Regeneration`, 1% of the maximum armor per second), `BionicArmor` components and the infected ship modification. Every other ship has rate 0: its armor never repairs and never starts a delay. Only the owner repairs; a remote copy follows corrections, as for the shield.
5. Armor damage restarts the repair delay even when a larger repair in the same tick nets it out (`DamageSystem.ApplyShieldFirst`). Legacy applies each impact alone, so its hit restarts the delay; the core nets a tick (owner, 2026-11-09). A repair alone does not restart it.
6. B4: stage 12 stores the absolute armor and shield deficits on the slot at retreat completion. Deploy applies them as a spend, as legacy `ShipInfo.Create`, so a shield or armor deficit starts its delay at deployment. Energy starts full.

## Engine

`ShipEngine` ports legacy `ShipEngine` in radians. The legacy formulas are linear in the angle unit, so the conversion changes no result. Stage 4 runs it for active roster ships, then drones in entity ID order.

1. The engine reads `Energy > 0` sampled at the start of stage 2, before the recharge. Without energy it uses the engine without energy; without one, the ship neither thrusts nor turns.
2. Thrust only above throttle 0.01. It steers the velocity toward `MaxVelocity` along the heading: a soft cap that keeps faster forward motion. The velocity changes by `acceleration × dt` before the physics step.
3. With a course, the legacy turn controller brakes inside 120° when the spin would overshoot, and drops to a tenth of the turn rate above the maximum angular velocity. Without a course it brakes the spin.
4. A retreating ship has its engine off and drifts (legacy `ShipRetreatEffect`).
5. Body damping is 0, as the legacy prefab drag.

## Contacts and damage

Stages 8–11 run one pipeline for every contact source. Physics only reports; the core selects the valid hits. Ramming, bullet sweeps, beam rays and point-defense overlaps share this pipeline.

1. **Shape table.** Physics shapes carry no user data. `ShapeTable` maps each live `ShapeId` to its entity, its hit priority (`Shield` before `Hull`) and its order in the definition. A lookup checks the shape generation. A stale `ShapeId` therefore resolves to nothing: the delayed end event of a destroyed body, or a reused index. Deploy adds the hull parts in `ShipDefinition.HullParts` order. That order is the shape order of hit records. Removal releases them.
2. **Candidates.** Each source adds `ContactCandidate`s to `ContactResolver` at stage 8. The sort key is a tuple, not one packed number, so the 64-bit IDs stay intact. The key order is: time (a fraction of the step), kind (`Disarm` before `Hit`), attacker ID, attacker device index (point defense; -1 otherwise), ray distance fraction, target shape priority, target ID, target shape order. Non-ray sources use zero for ray distance. Beam time stays zero; distance selects the nearest valid target within that ray. Without this field, the global sort could select a farther target by ID. Two candidates with the same key are a defect: the battle throws when `ThrowOnDefect` is set, and logs otherwise.
3. **Walk (stage 9).** The resolver walks the sorted candidates. It keeps per-bullet state for the step: stop time, disarmed, consumed own hit, damage taken. A candidate is skipped when:
   - its attacker is disarmed, has already stopped at its own hit, or stopped at an earlier time;
   - its target is a disarmed bullet, or a bullet that stopped at an earlier time.

   A target that stops at the same time stays valid, so two bullets that hit each other both take the hit. A hit on a bullet with `TargetHitPoints` above 0 adds its damage to the target's damage taken. The hit that brings it to the hit points stops the target at that time. Several weak hits in one step therefore stop a missile at the lethal one. A candidate with `OncePerTargetEntity` (damage over time and area bullets, D16) counts once per attacker and target entity per step. Ships never become invalid within a step.
4. **Ramming (Q8).** Stage 4 stores each active ship's linear velocity before the step (`PreStepVelocity`). Physics reports a touch only after the solve, when the velocities have already changed. `RammingTracker` counts the touching shape pairs per enemy ship pair. At stage 8 it handles begin events first, then end events. When a count goes from 0 to 1, it adds one candidate per direction at time 1. The damage is `0.1 × |vA − vB| × min(mass A, mass B) × the attacker's RammingDamageMultiplier`, and it applies only above 1 (legacy `DefaultCollisionBehaviour`). The candidate's target shape is the smallest (priority, order) among the pair's begin events in that step. A removal forgets every pair of the removed ship. Ally pairs are not tracked. A hostile worm tail segment pairs with a ship too; only the ship deals damage (Device statuses, worm tail).
5. **Damage (stage 10).** Stage 9 adds each accepted hit on a ship or a missile to its `PendingDamage` and emits a `Hit` event. A ship keeps one pending amount per damage type. Stage 10 applies the totals in one pass, as legacy `Impact.ApplyShield` and `ShipStats.ApplyDamage` do (see "Damage types"). A missile loses hit points (legacy `MissileDamageHandler`). The resolver and stage 10 add the same values in the same order, so they agree on a missile's death. Stage 10 applies damage only to local targets; the authority rules above decide which contacts supply that damage.
6. **Deaths (stage 11).** A ship with armor 0 or less is marked; stage 12 removes it with reason `Death` and a `Killed` transition. A missile with hit points 0 or less gets removal reason `Death`; it wins over an expiry in the same tick. `QueueDeath` remains as a test hook.

## Damage types

Phase 4 step 1 (D4, owner 2026-10-28): the legacy formula, with every type rule as data. Examples: `TODO/battle_simulator_phase4.md`, "Damage examples (D4)"; tests: `Core/Tests/DamageTypeTests.cs`.

1. **Table.** `BattleSetup.DamageTypes` is a `DamageTypeTable`: one `DamageTypeDefinition` per type (its shield order and its resistance ceiling, Device statuses item 9) and a `RammingDamageType`. A damage type is an index into it. The default is `LegacyDamageModel.Types`: impact, energy, heat, corrosive in database enum order; ramming deals impact.
2. **Sources.** `BulletDefinition`, `BeamDefinition` and `PointDefenseDefinition` carry `DamageType` (point defense defaults to energy, legacy `PointDefenseCollisionAction`). `BattleSetup` rejects a type its table lacks, on roster ships and their drones.
3. **Resistances.** `ShipDefinition.Resistances` holds one `DamageResistance` per type, or none (every type in full). `Armor` is the fraction removed before armor, `Shield` the fraction removed before the shield, `EnergyGain` the energy per point that passes the shield. `Armor` and `Shield` are at most 1, so no damage heals. `LegacyDamageModel.Resistances` builds them from the legacy percentages: corrosive takes half of the smallest armor resistance and the shield corrosive resistance; energy absorption over the armor multiplier is the energy gain.
4. **Stage 10.** The shield meets the types group by group in `ShieldOrder` (legacy: corrosive first). A type's load is its damage times `1 − Shield`. A group within the shield left is absorbed; a larger group keeps the same share of each type, and the shield empties. The shield restarts its delay. Armor then takes each remaining amount times `1 − Armor`. Energy gains each remaining amount times `EnergyGain`, up to its maximum, without restarting its delay.
5. **Bullet targets** take damage without types or resistances (legacy `MissileDamageHandler`).
6. **Network.** A hit record carries its damage type. Prediction and the correction replay use the copy's resistances and the same function.

## Result and timer

This section is the rules of `RosterMode` (`RosterRules`, [Battle modes](battle_modes.md)). The core asks the mode for the result at stage 13; another mode has its own rules.

Stage 13 evaluates elimination first: a side whose slots are all `Destroyed` loses; both sides in one tick give a draw. A `Ready` reserve keeps its side alive.
Then the timer: it advances one tick only while both sides have an active slot (B5). `TimeLimitTicks` 0 means no timer. `RosterRules.TimerMode` decides the resets and the expiry (phase 4 step 8; `TODO/battle_simulator_phase4.md`, "Timer modes and reinforcements (8)"):
- `Draw` (default): any ship deployment or removal on either side resets the timer to zero (owner, 2026-10-20). At the limit the battle ends in a draw with reason `Timeout`.
- The legacy `TimeOutMode` values use `PlayerSide`; the other side is the enemy. An enemy deployment resets. An enemy removal resets, except in `CallNextEnemyOrDraw` with no ready enemy slot. A player removal resets only in `DrainPlayerHp`. A player deployment never resets. A removal is a death, a retreat or a vanish; a reserve destroyed without an entity is not. The rules read the counts after every transition of the tick. With remote owners a removal on either side resets in every mode (phase 7, Q1).
- `CallNextEnemy` never draws: the timer counts on past the limit, and the adapter deploys the next enemy. `CallNextEnemyOrDraw` does the same while the enemy side has a ready slot (alive minus active); without one, 3 × the limit is a draw. `DrainPlayerHp` never draws: from the limit on, `RosterMode` asks a drain (`ModeContext.Drain`) that stage 10 applies to each active player roster ship of a local owner by k % of its maximum armor and shield per second, k being the whole overtime seconds of the previous tick's timer. The drain ignores resistances and restarts both delays. With remote owners it drains the active ships of both sides, each on its owner (phase 7, Q1).

The tick of a reset counts when both sides then have a ship. `RosterRules.MaxActiveShips(side)` is the deployment limit per side (`maxActiveShipsBySide`, else `maxActiveShipsPerSide` for both).
`RosterRules.OnlyOneShip` copies legacy `PlayerShipSelectionMode.OnlyOneShip` (phase 4 step 9; `TODO/battle_simulator_phase4.md`, "Ship selection modes (9)"). The first `Active` → `Destroyed` transition of a `PlayerSide` slot sets a deadline `RosterMode.ShipLostTicks` (150, the legacy 3 s) later. At the deadline the enemy side wins with reason `ShipLost`; the player reserves stay `Ready`. Elimination at or before the deadline comes first, so an enemy that dies inside the window gives a player victory. A retreat does not set the deadline. After the death the mode rejects player deployments. With remote owners the rule applies to both sides (`RosterRules.HasPlayerRules`; phase 7, Q1): the first death on either side sets the deadline and that side loses; first deaths of both sides in one tick give a draw with reason `ShipLost`; a later death of the other side does not change the loser. The duel rules (`OnlineDuel.json`) do not set the flag.
`BattleResult` holds outcome (`Victory` or `Draw`), winning side, reason (`Elimination`, `Timeout`, `ShipLost`, or `Mode` with a `ModeReason` of the mode) and the original commit tick.
`CommittedBattleFlow` (in `CombatSimulation.Modes`) evaluates roster transitions by their original ticks. It stores a confirmed prefix and replays only pending transitions.
A delayed deployment or removal can replace a pending timeout. All transitions at one tick apply before elimination and timer evaluation.
The first eliminated side loses; elimination of both sides at the same tick is a draw. Drones and predicted health never enter this evaluator.
`BattleSnapshot.CandidateResult` exposes the provisional result. `Result` and `IsCompleted` stay unset until the local core executes its tick
and every remote watermark covers it. Missing operation sequences and pending roster revisions block the confirmed prefix.
The core continues stepping and accepting commands while a candidate is pending. After finalization, `Step()` and `Submit()` throw, unless the setup has `ContinueAfterResult` ([Battle continuity](battle_modes.md#battle-continuity)); `IsStopped` tells which.
One final result event uses the publication tick; its `Result.Tick` retains the historical result tick.
Single player uses the same evaluator with an immediately confirmed local frontier, preserving its event log and timer behavior.

## Mod-only AI nodes

M3 stub ports ([plan](../../../../TODO/battle_simulator_phase5_part2.md#stub-ports-m3)). No built-in tree changes its outcome through these nodes, but Frontier trees reach all of them. Since this port, no node type is a stub (`NodeBuilder.Stub` stays for node types the schema adds later).

1. **Conditions.** `MainTargetWithinAttackRange` compares the distance with the lerped range of the selected weapons; a range of 0 or less fails. `TargetDistance` gives `Success` without a target, as legacy does, and uses `LimitedRange` when `MaxDistance` is not positive. `HasMothership` is true while the mothership is in the snapshot.
2. **Movement.** `MaintainAttackRange` circles the target (`Navigation.FlyAround`, spin 0) between the lerped range, capped at half the area width, and that range less the tolerance share. `SlowDown` brakes to a stop within the tolerance.
3. **Starbases.** `TargetAllyStarbase` and `TargetEnemyStarbase` keep a matching target, then the last starbase found, then search at most once a second for the first `SizeClass.Starbase` ship of the asked allegiance in snapshot order.
4. **Thrust.** `EnginePropulsionForce` compares throttle × (propulsion vector · forward) of the last tick with its value. It is computed from the snapshot (question 2 (a)), so it uses the velocity after that thrust. A starbase or an engine without propulsion fails.
5. **Mothership.** `MakeTargetMothership` sets the main target as the mothership through `AiContext.SetMothership`; it fails when the target already is the mothership or has left. A ship keeps it in its AI, and the mothership nodes, `FindEnemyNearMothership` and `FindDamagedAlly` read it. Destroyed and retreated read the target's roster slot; a target without a slot (a drone or a decoy) reads as destroyed when it leaves. A drone keeps it in its AI only (question 1 (a)): its world owner and allegiance stay, unlike legacy `Type.Owner`, and the next capture or berserk replaces it.
6. **Checks.** `TargetNodeTests` covers the examples of the plan; `NodeBuilderTests.EveryNodeTypeHasAPort` fails when a node type has no port.

## Allegiance and ship commands

Phase 4, step 7e (D12, D13). The side stays the team: result, timer, surrender, kill-all and spawn placement read it.

1. **Allegiance.** `Allegiance` copies legacy `UnitSide`: `Undefined`, `Player`, `Ally`, `Enemy`, `Neutral`. `Allegiances.IsHostile` copies `IsEnemy`: equal allegiances and the `Player`-`Ally` pair are friends. A ship and its drones start with `Allegiances.OfSide` (side 0 `Player`, side 1 `Enemy`); a bullet or beam takes its ship's allegiance at spawn. Collision filters, the missile target search, point defense and ramming damage use hostility, not the side. Ship and bullet snapshots carry it.
2. **Ship commands.** `ShipCommand` carries battle ID, command ID, target tick, entity ID and kind: `Vanish` or `ChangeAllegiance(allegiance)`. `Submit` rejects wrong battles, past ticks, null IDs, unknown kinds, invalid allegiances and duplicate pending command IDs. Stage 1 runs them after self-destruct requests and before frames, in command ID order; it rejects unknown and remote entities.
3. **Vanish (D12).** Stage 1 marks the ship; stage 12 removes it with `RemovalReason.Vanish`: no death, no explosion. A death of the same tick wins. A roster ship gets the `Retreated` transition at once and keeps its deficits for a redeployment. A drone is lost to its bay.
4. **Allegiance change (D13).** Stage 1 sets the ship's allegiance, its hull filters, and the allegiance and filters of its live bullets and beams: legacy bullets read their owner's side. A drone that turns hostile gives no kill credit and earns none (the core keeps no kill credit).
5. **Network.** The owner sends an `Allegiance` operation (stage 1). The receiver applies it to the copy and the copy's bullets, predicted copies included. A vanished drone travels as a removal with reason `Vanish`; a vanished roster ship travels as its `Retreated` roster record, so the copy shows a retreat removal.
6. **Runtime.** `PilotControls` carries `Vanish` and an optional `Allegiance`. The runner submits them as ship commands for the pilot's own ship at the frame's target tick, in locked and network mode; they never enter the input stream.

## Snapshots and events

Stage 14 writes a `BattleSnapshot` of the tick it completed, then increments `Battle.Tick`. `Battle.Snapshot` returns the published one. The constructor publishes tick −1: every slot `Ready`, no ships, no events.

1. Contents: battle ID, tick, timer, result, `Ships` (active roster ships in roster order, then drones by entity ID), `Bullets` (live bullets in storage order; the order has no meaning), `Segments` (live worm tail segments in storage order: entity, ship, side, allegiance, owner, definition, number, detached, scale, mass and pose), `Asteroids` (live asteroids in setup order: entity, tag, size, mass, hit points and maximum, pose and velocity), `Units` (every unit in setup order) and `Events` (the tick's presentation batch), plus `OutgoingHits` (resolved contacts for remote ship and drone owners, and contacts on remote bullets for their owners). The lists are `ReadOnlySpan` views over arrays the battle reuses.
2. `ShipState`: entity, roster ID, side, allegiance, owner, definition, pose and velocities from the physics body, armor, shield and energy (value and maximum), the controls in force and the retreat completion tick.
3. `BulletState`: entity, parent entity, side, owner, definition, pose, velocity and expiry tick. Projectiles retain `Definition` (`BulletDefinition`). Beams set `BeamDefinition` and `IsBeam`, with `Definition` null. Their pose is the sampled ray origin and angle; `BeamEndPoint` and `BeamTargetId` describe the accepted hit or full range. Beam velocity is zero and expiry is `long.MaxValue` because the device owns lifetime.
4. `UnitState`: roster ID, side, owner, status, deployment count and order, revision, active entity (null unless active), the retained B4 deficits, `DamageFraction` (B2) and `Performance` ([Battle modes](battle_modes.md#unit-performance)).
5. `DeviceState` (phase 4, step 3b): `GetDevices(ship)` returns one entry per device of a `ShipState` from the same snapshot, in control-bit order. `Ready` is legacy `CanBeActivated` for the next tick with the end-of-tick resources; the bit does not count. `Active` is legacy `SystemBase.Active`: the bit was held and the device's checks passed before it spent energy. `Cooldown` is the HUD fraction (0 ready, 1 blocked). Drone bays add `InHangar` and `ActiveDrones`; beams, manageable cannons and charging weapons add `ActiveBullet` (the attached beam, the bullet in flight, or the last shot's live bullet; phase 5, steps 4b and 4f); charging weapons add `Charge` (step 4f). Inert devices are never ready or active and show cooldown 1. No peer sends it: each derives it from its own simulation. The rules per device are in [the phase 4 plan](../../../../TODO/battle_simulator_phase4.md#device-state-3b).
6. Two buffers: the battle writes one while readers use the other. A reader finishes with a snapshot before the next `Step()` returns; the snapshot of tick `t` stays intact while tick `t + 1` runs. A reader on another thread takes a copy (`CopyTo`, [Session thread](runtime_session.md#session-thread)).
7. Publishing allocates nothing once the arrays fit the battle (unit tests: ships, cannon hits, missile salvos, sustained beam hits and repeated beam activations, and the full fleet of the scripted battle). The copy size is measured in [Scripted battle](#scripted-battle).

`BattleEvent` is one flat struct per event, tagged by `Kind`. Its identity is battle ID, tick and `Sequence` (the position in the batch). Beam spawn/removal events carry `BeamDefinition`, `ParentId`, `IsBeam` and `IsBullet`; `BulletDefinition` is null. A beam emits one spawn and removal per activation and a `Hit` for each accepted tick. Events follow stage order. Roster transitions follow roster order; drone removals follow entity ID order; hits follow contact order. An entity event comes before the roster transition of the same operation:

| Operation | Stage | Events |
| --- | --- | --- |
| Deploy | 1 | `Spawn` (with the `ShipDefinition`), then `UnitTransition` Ready → Active, `Deployed` |
| Shot | 5 | `Spawn` of a bullet: `BulletDefinition`, `ParentId`, side and owner; `IsBullet` is true |
| Hit | 9 | `Hit` per accepted hit on a ship or a missile, in contact order: attacker (a ship or a bullet), target, damage before the shield, point (zero for a ship touch) |
| Point-defense zap | 9 | `Disarm`: attacking ship ID, device index, target bullet ID and query position |
| Death | 12 | `Removal` (`Death`), then `UnitTransition` → Destroyed, `Killed` |
| Retreat completion | 12 | `Removal` (`Retreat`), then `UnitTransition` → Ready, `Retreated`, with the retained deficits |
| Surrender, kill-all | 12 | For an active slot `Removal` (`Death`); for every slot `UnitTransition` → Destroyed, `ForcedDestruction`. A reserve has no entity. |
| Teleporter jump | 2 (a copy: 1) | `Teleport`: ship ID, `ShipDefinition`, device index, start point (`Point`) and landing point (`Destination`) |
| Bullet adoption (network only) | 5 (an overdue record: 1) | `Rename`: a predicted copy of a remote bullet takes its owner's ID; `PreviousId` is the copy's ID (fire confirmations, P2) |
| Missile target or correction (network only) | 4 or 1 | None: the copy's target and motion change without an event (P3) |
| Child bullet | 5 (`Created`) or 12 | `Spawn` of a bullet whose `ParentId` is a bullet; at stage 12 before its parent's `Removal` (bullet triggers) |
| Bullet removal | 12 | `Removal` of a bullet (`Death` at a hit, `Expiry`, or `Disposal` after disarm or beam shutdown), after the ship changes, in entity ID order. `RemovalConditions` names the trigger conditions of the removal for the view (phase 5b, step 6): the owner gives those that fired; another device derives them from the reason and the definition (`BulletTriggers.RemovalConditions`), so it misses a manageable or sticky detonation and the cooldown of an `Expired` `Detonate` trigger. A stopped beam counts as expired; a bullet that leaves with its ship has none. Presentation only: the state hash and the codec do not change |
| Mode action or event | 1 or 13 | The events of the action (`Spawn` and `UnitTransition` for a deployment; `UnitTransition` → Ready, `Respawned`), or `Mode` with the `ModeEvent` ([Battle modes](battle_modes.md)) |
| Result | 13 | `Result` with the `BattleResult` |
| Damage text | 10, published at 14 | `Damage` per ship and kind of loss: the shield loss (`DeviceIndex` −1), then each type's armor damage after the shield and its resistance (`DeviceIndex` the type). Presentation only (phase 5b, step 4); the network replay emits none. They come last in the batch, after the result, so the other events keep their sequence numbers |
| Repair bot or tail segment removal | 12, published at 14 | `AuxiliaryRemoval`: the object's ID, its ship (`ParentId`), its last position (`Point`) and the reason (`Death`, `Vanish`, `Retreat`). Presentation only (phase 5b, step 7b): the views of these objects live by the snapshot, and the event tells a death from a vanish. Published before the `Damage` events, after the result, so the other events keep their sequence numbers |

A repeated destruction emits nothing. `UnitTransitionReason` has the same values as the application's `RosterChangeReason`, plus `Respawned`, which only a mode gives. Every single-player event is committed; mixed-ownership events carry explicit authority as described above.

B2 participation facts: `DamageFraction` is the share of a slot's armor that is lost. It is 1 when the slot is destroyed (also without an entity), the armor deficit over the maximum while active or after a retreat, and 0 when never deployed. The core emits no separate checkpoint event. A `Retreated` or `Killed` transition and the `Result` event are the checkpoints. The consumer reads the fractions from the same snapshot, after the tick's roster updates. It credits the increase of the enemy sum, as legacy `CombatModel` does. A `ForcedDestruction` transition is not a checkpoint. At completion, the consumer credits the slot with the highest `DeploymentOrder` of its side.

## Pilots and scheduling

`BattleRunner` (Runtime) owns a `Battle`, assigns command IDs and runs pilots. Callers submit roster commands through it, so command IDs never clash.

1. `IPilot.Think(snapshot, ship, out controls)` reads the snapshot and returns course, throttle, system bits and optional `SelfDestruct`. The runner submits self-destruction with the same `N + Delay` as the control frame. `false` keeps the last frame in force; the runner submits nothing.
2. `IPilotProvider.CreatePilot` runs once per local ship `Spawn` event (bullets and remote owners are skipped) and returns a pilot and a `PilotSchedule`, or null. A redeployment is a new spawn, so the provider is asked again. A `Removal` event drops the pilot.
3. Locked mode: after each step, a pilot thinks on the snapshot of tick `N` when `N mod Period` equals `EntityId mod Period`. Its frame targets `N + Delay`; the battle's command buffer holds it until then. Pilots run in spawn order.
4. Presets (Q9): `Ship` (delay 2, period 1), `Drone` (2, 2), `PlayerShip` (1, 1).
5. Pilots run synchronously on the calling thread, so a pilot is never late. Free-running mode is deferred to the worker thread (owner, 2026-10-20): without one it equals locked mode with delay 1.
6. No pilot thinks on the result snapshot of a stopped battle; with `ContinueAfterResult` the pilots keep thinking. Frames already queued for a ship that leaves are dropped at stage 1 and counted as rejected.

Core tests cover tick advancement, fixed physics timing, independent battle instances, setup validation, identity derivation and value semantics, the entity registry, counter-based random, roster transitions, roster commands, control frames, spawn placement, the result, the timer, resources, B4 deficits, the engine, snapshots, events, pilot scheduling, contact ordering and selection, ramming, damage, B2 facts, the cannon (Q1) and bullets: hits, expiry, filters, identity, events, the snapshot and allocation, the target grid, and the homing missile (Q2): launch, thrust, turn, search timing, retarget, hits on ships, bolts and missiles, expiry and allocation.
Beam (Q3), point-defense (Q4) and drone-bay (Q5/Q6) tests cover full battle steps and allocation. The scripted battle tests cover all of it together. All 654 engine-free tests pass, including Network and the step 2 authority checks. Core, Runtime and Core.Tests compile separately with Unity 6000.0.75f1's compiler.

## Scripted battle

Phase 2 step 11. `Core/Tests/ScriptedBattle.cs` is test code: the core knows nothing of it.

1. **Fleets.** Each side has three slots with 600 armor, in this order: a gunship (cannon and point defense), a striker (homing missile launcher and beam on separate barrels) and a carrier (a bay with four cannon drones). Two ships per side are active; the carrier is the reserve. Arena 200 × 200, time limit 6000 ticks.
2. **Pilots.** One `ChasePilot` per ship and drone, through `BattleRunner` with the Q9 schedules. It flies at the nearest enemy by the shortest torus vector and only turns inside 6 units. It holds a weapon bit when the enemy is in range and inside the aim cone: 20° for bolts, 60° for missiles, 10° for the beam. Point defense and bays are always held. A drone self-destructs when its bay slot is `Destroyed`; after a retreat it waits for the redeployment (Q6).
3. **Commander.** After each step it deploys `Ready` slots in roster order while a side is below its active limit. It orders one retreat per gunship slot below 60 % armor, so the ship survives the 7 s charge and redeploys.
4. **Result (seed 7).** Side 0 wins by elimination at tick 3506 (70 s). The battle has 4 roster deaths, 3 drone deaths, 2 retreats and 2 redeployments. It spawns 8 ships, 8 drones, 1119 bolts, 34 missiles and 46 beams. It has 2053 hits (1222 beam, 8 ramming) and 54 disarms.
5. **Determinism.** The log holds every event and a per-tick digest of ship and bullet poses, velocities and resources as raw float bits. One run alone and two runs stepped interleaved in one process give the same log.
6. **Allocation.** The same fleet with huge armor and energy, all three slots active, steps 3000 ticks after a 1500-tick warm-up with zero bytes allocated, pilots included. The window has bolts, missiles, beam hits, point-defense zaps and eight live drones. Buffers grow until about tick 510, at the first salvo peak; afterwards nothing allocates.
7. **Snapshot size.** Element sizes on .NET 8 x64, measured as the allocation of a 1024-element array: `ShipState` 128 B, `BulletState` 88 B, `UnitState` 64 B, `BattleEvent` 160 B, `DeviceState` 24 B (2026-10-29; `ShipState` kept its size with the device offset). The scripted battle copies 1.8 KB per tick on average and 3.6 KB at most (peak 12 ships, 16 bullets). At R2 (2,000 bullets, 130 ships) the ship and bullet copy is 192,640 B per tick, about 9.6 MB/s at 50 Hz. That is 3.2 times the design estimate of 60 KB, which assumed 24-byte bullets. Timed on R1 (2026-10-28), `BattleSnapshot.Write` takes 0.24 ms per tick at R2 (0.27 ms with `DeviceState`, 2026-10-31); the byte copy is about a tenth of it ([core design](../../../../TODO/battle_simulator_core.md), risk 3).
