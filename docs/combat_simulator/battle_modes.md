# Battle modes

Part of [Combat simulator](index.md).

The core runs the world: physics, ships, devices, bullets, contacts, damage and the units. It has no game rules. A **battle mode** is the game rule code: who deploys, where, when the battle ends, and why. A mode is engine-free and deterministic, and it runs inside `Battle.Step`. A new mode needs no core change. A new mechanic (docking, loot, perks) still needs a core primitive, one time.

| Mode | Assembly | Game |
| --- | --- | --- |
| `RosterMode` | `CombatSimulation.Modes` | Event Horizon's roster battle and online duel. Frontier's PvP duel can use it. |
| Frontier's modes | Frontier's engine-free `CombatModes` | The common level, the arena and the other Frontier modes. |

## Rules and mode

`IBattleRules` is immutable data that `BattleSetup.Rules` holds:

1. The `BattleSetup` constructor calls `Validate(setup)` last. `RosterRules` throws when a side has no unit.
2. Each `Battle` calls `CreateMode(setup)` once, in its constructor. The mode is the mutable object.
3. `SetupHash` walks the rules by reflection, so the duel peers compare the rules too.
4. A setup without rules runs an open battle: every deployment is allowed, and the battle has no result.

`IBattleMode` has these hooks. The core calls them between stages, never inside one.

| Hook | When | Use |
| --- | --- | --- |
| `BeginTick(context, inputs)` | Stage 1, before the queued commands | The mode commands of the tick, in command ID order. The actions run only here. |
| `AllowDeploy(context, unit, out reason)` | Stage 1, for each queued `Deploy` command, after the core's checks (unknown ID, remote owner, status) | False rejects the command with the reason. |
| `OnTransition(change)` | Each committed unit transition, by its original tick | A local one at once (also during the mode's own action), a received one when it applies. |
| `EndTick(context, confirmedThrough, out candidate)` | Stage 13 | The result. `confirmedThrough` is the result frontier: the tick itself without remote owners. |
| `TimerElapsedTicks` | After `EndTick` | Published in `BattleSnapshot.TimerElapsedTicks`. 0 for a mode without a timer. |

## Mode context

`ModeContext` reads the battle and acts on it. Read access: `Tick`, `Setup`, `Snapshot` (the last completed tick), `TickEvents` (the batch so far), and the units now (`UnitCount`, `GetUnit`, `TryGetUnit`, `CountUnits(side, status)`).

The actions run at once, in call order, before the queued commands. They have no command IDs. A bool action returns false when it does not apply.

| Action | Effect |
| --- | --- |
| `AddUnit(UnitSetup)` | Adds a `Ready` unit after the existing units. The ID must be new and the owner local. The damage types are checked as at setup. |
| `Deploy(UnitId, SpawnPlacement)` | Deploys a `Ready` unit, as the `Deploy` command does. `Auto` is the command's placement. `At` sets the pose and both velocities. `Near` sets the pose, then moves the ship at most `SearchRadius` to clear an overlap. |
| `Respawn(UnitId)` | See "Respawn". |
| `Retreat`, `Vanish`, `ChangeAllegiance` | As the unit and ship commands. |
| `DestroyUnit(UnitId)` | Destroys the unit at stage 12, after surrender and kill-all, with reason `ForcedDestruction`. A `Ready` unit gets no entity. |
| `RemoveUnit(UnitId)` | Removes the unit from the table at stage 12, after the mode's destructions (Frontier `TODO/common_level_mode.md`, decision 1). Its ship vanishes (`Removal`, `Vanish`), then the drone pass removes its drones and clones, and then the unit leaves with a `Removed` transition (status `Destroyed`). A ship that dies in the same tick dies first. A bullet in flight keeps the removed unit's credit, which no snapshot shows. A later tick may add the ID again. |
| `SpawnAsteroid(AsteroidSetup)` | Adds an asteroid at once and returns its entity ID (decision 2). The ID is new in the battle: the setup's asteroids take the numbers 0 to n − 1, and each spawn takes the next. A free storage entry is reused with a new generation. |
| `VanishAsteroid(EntityId)` | The asteroid leaves at stage 12 with an `AsteroidRemoval` event whose `RemovalReason` is `Vanish`: the view plays no explosion, and a mode gives no loot. |
| `VanishBullet(EntityId)` | The bullet or beam leaves at stage 12 with a `Removal` event whose `RemovalReason` is `Disposal`, and runs no triggers: no detonation and no children. Frontier's preparation clears the enemy's bullets with it, as legacy does. |
| `SpawnPickup(PickupSetup)` | Adds a pickup at once and returns its entity ID. See "Pickups". |
| `VanishPickup(EntityId)` | The pickup leaves at stage 12 with a `PickupRemoval` event whose `RemovalReason` is `Vanish`. |
| `SetCollector(UnitId, PickupCollector)` | What the unit's ship does with pickups: `Collect`, `Attract`, both or `None`. The flags stay through respawns and `ChangeRules` until the mode clears them. |
| `RestoreDrone(DroneBayKey)` | One drone back in the bay's hangar, when the bay has room. No build cycles: a drone that docks, not a rebuild. False for an unknown unit, a device that is not a drone bay, or a full bay. Frontier's salvage drones dock with it. |
| `SetVelocity(EntityId, Vec2)` | Sets the ship's linear velocity at once. `BeginTick` runs before the engine and the physics, so the snapshot's velocity is the ship's velocity until this call. A body with a fixed position keeps its zero velocity. |
| `Drain(UnitId, armorFraction, shieldFraction)` | At stage 10, after the damage: the unit's active ship of a local owner loses the fractions of its maximum armor and shield. No resistance; both delays restart. |
| `SetNoDamage(UnitId, bool)` | Legacy `NoDamageSystem` (Frontier's preparation ships, the god mode cheat). The unit's ship gets its full armor back at stage 10 after the damage of each tick, so no hit kills it. The hit still counts in the performance and gives damage events; shield and energy change as without it. Drains, `DestroyUnit` and the unit's drones are not covered. The flag stays through respawns and `ChangeRules` until the mode clears it; `UnitState.NoDamage` shows it. |
| `Publish(ModeEvent)` | Adds a `BattleEventKind.Mode` event with the mode's code and two values. Also allowed in `EndTick`, where it comes before the result. |
| `Random(purpose)` | A counter-based random state of the match seed, the tick and the mode's purpose (`RandomPurpose.Mode`). |

Rules:

1. An action outside `BeginTick` throws. `Publish` is also allowed in `EndTick`.
2. With remote owners every action except `Drain`, `Publish` and `Random` throws (D10). The online duel runs only `RosterMode`, which uses only `Drain`. A networked mode can come later: a mode changes the battle only through actions, so authority can be added to them.
3. A mode reads no Unity state, no wall time and no `Dictionary` enumeration order.

## Pickups

Frontier's loot (Frontier `TODO/loot.md`, F06). A pickup is a small object that moves, lives for a time, follows a collecting ship and is collected once. The core knows only an opaque `long` reward ID. The mode decides what drops and where; the application turns the reward ID into its item and grants it on the `Collected` removal.

1. **Setup.** `PickupSetup` gives the position, velocity, angle, spin (radians per second), scale, reward ID and lifetime (`DefaultLifetimeTicks` 9000, legacy 180 s). The collection circle has radius scale / 2. The ID takes the next pickup number (`SpawnIdentity.CreatePickup`), so it never repeats in the battle.
2. **Kinematic.** A pickup has no physics body and no contacts. Nothing pushes it, and it pushes nothing (legacy loot is a body of weight 0.1; owner, 2026-11-23).
3. **Motion (stage 4).** Constant velocity with the torus wrap. The legacy magnet pulls it toward the nearest `Attract` ship within 4 + ship scale / 2 + pickup scale / 2: acceleration `(direction × 10 + ship velocity − pickup velocity) × 10` per second. The lower entity ID wins a tie.
4. **Collection (after stage 9).** A `Collect` ship takes the pickup when the torus distance is below the pickup radius plus the ship's hull radius. It is an overlap test, not a contact, so `NoContacts` does not stop it. When two ships reach it in one tick, the lower entity ID wins. A long hull collects a little early at its sides; legacy uses the exact collider. The ceasefire does not stop it, as legacy.
5. **Collectors.** A roster ship collects by its unit's `SetCollector` flags. A drone collects (never attracts) when its definition has `ShipDefinition.CollectsPickups`. Only local ships collect.
6. **Removal (stage 12).** In storage order: the collected, the vanished, then the expired. Each gives a `PickupRemoval` event ([Snapshots and events](core.md#snapshots-and-events)). The pickup lives `LifetimeTicks` ticks, its spawn tick included.
7. **Snapshot.** `BattleSnapshot.Pickups` lists the live pickups: entity, reward ID, scale, position, angle, velocity, expiry tick and `Life` (the remaining share of the lifetime). Views read the snapshot, so a pickup has no spawn event.
8. **Drops.** The ship `Removal` event carries the ship's last position (`Point`) and velocity (`Velocity`), so a mode can place a drop at the next `BeginTick`.

Local battles only: the four actions throw with remote owners (D10). Main uses none of them, so its scripted digest did not change. `PickupTests` covers the motion and wrap, the magnet, the collection by the lower ID, a `NoContacts` collecting drone, the expiry, `RestoreDrone` and the remote-owner rejection.

## Mode commands

`ModeCommand` is an application input that the simulation cannot decide: the player buys a life, a cheat button, an exit. It carries battle ID, command ID, target tick, a kind and two `long` arguments. The core does not interpret it. It goes through `Battle.Submit` and the command queue, so it shares the command ID namespace and the "past tick" check, and a replay gives it again. `BattleRunner.SubmitModeCommand` assigns the command ID.

## Units added during the battle

The unit table and `BattleSnapshot.Units` grow when a mode adds a unit, and shrink when it removes one (`RemoveUnit`). A long level adds one unit per enemy; a mode that runs many levels in one battle removes the old units at each switch (Frontier's common level, decision 1). The later units move up one place in table order. The snapshot array grows as the other arrays do; the growth allocates. `UnitSetup.Allegiance` (default `Allegiances.OfSide(side)`) is the allegiance of the unit's ships and drones at deployment.

## Respawn

`Respawn(UnitId)` turns a `Destroyed` unit to `Ready` (`UnitTransitionReason.Respawned`). The death before it is a normal death: `Removal` (`Death`), the death devices, and `Killed`.

1. The armor and shield deficits become zero.
2. Each drone bay and cloning centre refills: the hangar has the capacity less the unit's live drones (legacy `DroneBay.LookForDrones`).
3. The next deployment of the unit gives its live drones to the new ship (`MothershipOverride`, unless a capture or berserk set one). A retreat and a redeployment without a respawn do not: the drone keeps its retreated mothership, as Main's AI expects.
4. While the unit is `Destroyed`, `AiContext.IsMothershipDestroyed` is true for its drones.
5. Only a mode respawns. `RosterMode` never does, so `Destroyed` stays final in Main's battles and in the network code.

## Result

`BattleResult` has outcome, winning side, reason and tick. A mode's own result uses `BattleEndReason.Mode` and a `ushort ModeReason` that the mode defines. `ToString` prints "Mode n" for it; the other reasons print as before.

## Battle continuity

D12: one local `Battle` lives for a whole level visit (preparation, combat, result, preparation again), as legacy Frontier keeps its `Game` scene. A new battle would rebuild every view and lose the world state.

1. **After the result.** `BattleSetup.ContinueAfterResult` (default false) lets the battle step and take commands after its result. The first completed result latches: later results of the mode are ignored, and the `Result` event comes once. The mode's `BeginTick` and `EndTick` keep running. `IsCompleted` means "has a result"; `IsStopped` means "has a result and steps no more". `BattleRunner` keeps the pilots thinking. A setup with remote owners rejects the flag: Main's online duel stops at its result.
2. **Battle commands.** `BattleCommand` carries battle ID, command ID, target tick and a kind: `Ceasefire(enabled)` or `ChangeRules(rules)`. It shares the command ID namespace and the "past tick" check. Stage 1 applies the commands of its tick first, in command ID order, before the mode's `BeginTick`, so a replay gives them again. A battle with remote owners throws at `Submit`. `BattleRunner.Ceasefire` and `BattleRunner.ChangeRules` assign the command ID.
3. **Ceasefire.** As legacy `CeasefireSignal`: while it is on, every ship's `Systems` is 0 and the frames' turret requests are dropped; `RequestedSystems` keeps the frame's bits, and the engine controls stay. A change applies at stage 1 after `PreviousSystems`, so the devices see the release, and a held button presses again when the ceasefire ends. It covers the player, the AI and drones. A running device finishes its duration, and bullets in flight still hit. `BattleSnapshot.Ceasefire` publishes it.
4. **`ChangeRules`.** `Submit` validates the rules against the setup (`IBattleRules.Validate` throws as at setup). At stage 1 the battle creates the new mode (null rules: an open battle), clears the latched result and the ceasefire, and sets every unit's performance to zero. `Battle.Rules` gives the running rules; `Setup.Rules` keeps the first ones. The units, the ships, the entity IDs, the world size, the asteroids and the zones stay. The new mode starts from the current units: it reads them through its context. The application reads the performance for the reward before it submits the command.

With the flag off and no battle commands, the battle steps as before: Main's scripted battle digest does not change.

## Unit performance

Each unit counts what its ships, drones and clones did, as legacy Frontier's `ShipPerformance` does. `UnitState.Performance` publishes the counts. They only read the results of stage 10, so they never change the battle. The application selects the units it shows and derives the combat contribution and the experience.

| Count | Rule |
| --- | --- |
| Armor and shield damage dealt | Loss of hostile ships and drones after resistances. An energy shield device's absorption is shield damage. The armor loss is at most the armor the target had (legacy `Armor.Get`). |
| Damage to allies | Armor and shield loss of friendly targets. |
| Damage repaired | The repair amount of friendly hits. |
| Armor and shield damage received | All loss of the unit's ship, from any source. Legacy adds the armor damage to the received shield damage; the core counts the shield damage (owner, 2026-11-17). |
| Kills | A hostile unit ship that dies in a tick where this unit dealt armor damage to it last, in contact order. Drones do not count. |
| Deaths | `Killed` transitions of the unit. |

How the credit works:

1. A bullet keeps the unit of its firing ship (`ShipEntity.CreditUnit`: the ship's unit, or its bay's unit for a drone or a clone). The credit outlives the ship. A child bullet takes its parent's. A ramming ship credits its own unit.
2. Stage 9 records each accepted damage hit on a ship: target, attacking unit, amount, hostile or not.
3. Stage 10 splits each target's shield loss and armor loss among its attackers by their share of all the raw damage the target took in the tick. With one attacker in a tick, the usual case, the split is exact.
4. Damage without an attacking unit (an asteroid, a tail segment, a remote hit, a drain) counts only as received.
5. Only local targets count: a remote copy's damage is a prediction. The counts are exact in single player.

Known limits: a captured missile keeps its firing unit's credit. The split uses the share of all damage types, not of each type.

## Event Horizon's roster battle (`RosterMode`)

`RosterRules` has the rule fields that `BattleSetup` had before: `TimeLimitTicks`, `MaxActiveShipsPerSide` and `MaxActiveShipsBySide`, `TimerMode`, `PlayerSide`, `OnlyOneShip`, and `HasPlayerRules(setup, side)`. [Result and timer](core.md#result-and-timer) describes the rules.

1. `AllowDeploy`: the side limit, then `OnlyOneShip`. The log texts stay "side limit" and "only one ship".
2. `OnTransition` feeds `CommittedBattleFlow`, unchanged, with the original ticks.
3. `BeginTick`: in `DrainPlayerHp` overtime, a `Drain` for each unit with the player rules, from the timer of the previous tick. Stage 10 applies it where the core drained before, in unit order, so the ships that deploy in the tick are drained too.
4. `EndTick`: `CommittedBattleFlow.Evaluate` with the result frontier.

The change kept the scripted battle log and its SHA-256, and every engine-free test (2026-11-17).

## Docking

`Docking` (`CombatSimulation.Modes`) is a mode helper for F10. It reads only the snapshot, so it needs no core change.

1. The mode adds circle areas, each with a `long` tag, a center and a radius. `Move` follows a moving object; `Remove` undocks a docked ship at once.
2. The mode calls `Update(context, ship)` once a tick, in `BeginTick` or `EndTick`. It checks the ship of the last completed tick.
3. The ship docks when its hull circle touches an area (legacy `PlayerDockingAction`: the collider touches the trigger). It undocks when the gap is larger than the release margin, so a ship at the edge does not flicker.
4. Each change is a `ModeEvent` with the mode's docked or undocked code, the tag (`Value0`) and the ship's entity ID (`Value1`).
5. A null or absent ship undocks from every area. A new ship first undocks the previous one. Areas dock on their own, in the order they were added.

## Lives

`Lives` (`CombatSimulation.Modes`) is a mode helper for lives with respawn: legacy `PlayerLivesHolder` and the scene managers' `CheckPlayerStatus`. It uses only `Respawn`, `Deploy`, `Publish`, `TickEvents` and the snapshot, so it needs no core change.

1. The mode adds and deploys a unit, then calls `Track(unit, LivesSetup)`. `LivesSetup` gives the lives (respawns), the delay in ticks, the placement, `AtLastPose` and `AskForMore`.
2. The mode calls `BeginTick` after it handles its `ModeCommand`s, and `EndTick` before it decides the result.
3. `EndTick` reads the `Killed` transitions in `TickEvents`. A death spends a life at once, as legacy `--_lives`, and the unit is `Waiting` until the death tick + the delay. A `ForcedDestruction` stops the tracking; a retreat or a vanish is not a death.
4. `BeginTick` handles the due units in `Track` order. With a spent life, the unit respawns: `Respawn`, then `Deploy`. With no life and `AskForMore`, it is `Deciding` and the decision event is published. Otherwise it is `Out`.
5. `AtLastPose` deploys with `Near` (radius 40) at the dead ship's pose in the last snapshot, with zero velocity. A ship that died in its spawn tick is not in the snapshot, so the placement is used.
6. Only one unit is `Deciding` at a time. The mode answers with `AddLife` (the unit respawns in the next `BeginTick`) or `Decline` (`Out`). A unit with a life respawns while another one decides.
7. The helper never ends the battle. The mode reads `Status` and sets its result.
8. Events: the changed code with the unit ID (`Value0`) and the lives left (`Value1`), when they changed in the call; the decision code with the unit ID and the deaths.

The game computes `AskForMore` (in Frontier from `IAP_DISABLED`, the level loot and the tutorial), the prices and the windows. `LivesTests` covers it.

## Exploration (`ExplorationMode`)

The shared exploration level of both games (`CombatSimulation.Modes`). It uses only the mode API, `Docking` and `Lives`, so it needs no core change. The game decides what is on the level; the shared code decides where.

1. **Layout.** `ExplorationLayout.Generate(ExplorationLayoutInput)` is a pure function that runs before the battle. The input has the seed (the planet's, never `MatchSeed`), the world size, `UnitIdBase`, the game's objectives (kind, radius, content key, guardian group), the guardian groups, a content key for each random ship, the zones and the asteroids.
2. **Placement.** The player starts at the world centre, which is grid point (5, 5) of a 10 × 10 grid. Each objective takes its own grid point (not the player's), with a jitter of ±25 % of a cell. Guardians ring their objective, starting at its angle. Random ships, zones and asteroids are uniform, outside `StartClearance` (75) of the start.
3. **Random streams.** Each part draws from its own stream: `RandomState.FromSeed` of a SplitMix64 mix of the seed, the part and the index. A change in one part does not move the others, and a new objective at the end keeps the earlier ones. `ExplorationLayout.Version` increases with any change to the output.
4. **Spawners.** One for each hostile objective, guardian, dockable ship and random ship, in this order. `UnitId = UnitIdBase + index`; `TryFindSpawner(UnitId)` finds the layout item. The setup's own units must be outside this range.
5. **Content.** `IExplorationContent.CreateShip(role, contentKey, ordinal)` gives the `ShipDefinition`. `ordinal` is a guardian's number in its group (0 to n − 1) and 0 for the other roles, so each guardian of a group can get its own ship. It is pure, and the mode calls it in `BeginTick` when a spawner first activates, and for the dockable ships at tick 0. A respawn keeps the unit and does not call it again.
6. **Rules.** `ExplorationRules` holds the layout, the content, the player's unit (side 0, in the setup), the player's `LivesSetup`, the objectives complete before the visit (Main), and the distances: activation 75, vanish 100, docking release margin 0.6. The world must have the layout's size, and the battle must be local.
7. **Start (tick 0).** The player deploys at the start. Each dockable ship is added (side 0, `Ally`) and deployed at its objective. Each dockable object and dockable ship gets a docking area; a hostile objective gets none.
8. **Spawners at run time.** `Dormant` → `AddUnit` (side 1, `Enemy`) and deploy when the player is within 75 of the spawner position → `Active`. `Active` → `Vanish` when the ship is farther than 100 from the player → `Away` (the unit is `Ready` with its armor and shield damage). `Away` → deploy again within 75. A deploy is at the spawner position, facing the player, at rest. A kill → `Dead`. A guardian with a respawn delay respawns to `Away` while its objective's ship lives. Nothing activates or vanishes while the player has no ship. A dockable ship never vanishes. The mode checks every spawner each tick; the ship positions come from one pass over the snapshot.
9. **Objectives.** A dockable object completes on the `CompleteObjective` command (scan window), a dockable ship on the command (store) or its death, a hostile objective on its death. A forced destruction (kill-all) counts as a death. Each completion removes the docking area and publishes the completed event with `ExplorationCompletion`. Objectives complete before the visit get no area and no ship, and their guardians never come.
10. **Commands:** `CompleteObjective` (ID; an unknown or complete ID is ignored), `Exit`, `BuyLife`, `Decline`, `KillAll` (the cheat: `DestroyUnit` for each active hostile, guardian and random ship), `DockingInProgress` (ID, 1 or 0; see 11). The mode rejects every `Deploy` command.
11. **Docking brake** (owner, 2026-11-19). The game sends `DockingInProgress(id, 1)` while it shows the docking (a scan window, an animation) and `(id, 0)` when it stops. A start while the player is not docked at that objective is ignored; an undock or the completion ends it. While a docking is in progress and the player's throttle in the last snapshot is below `ExplorationRules.DockingBrakeThrottle`, the mode calls `SetVelocity` with the velocity × (1 − `DockingBrakeRate` × tick duration), once a tick. Legacy rates: Frontier `DockingSystem` 1 below 0.001 (the defaults); Main `DockingStationCollisionBehaviour` 3 below 0.1. Legacy braked without the window condition; the condition is new (owner).
11. **Events:** docked and undocked (objective ID, player entity), objective completed (ID, how), lives changed and the decision (from `Lives`).
12. **Results:** `AllComplete` (victory of side 0), then `PlayerOut` (`Lives` says `Out`; victory of side 1), then `PlayerExit` (victory of side 1; Main ignores the outcome). The battle continues after the result (`ContinueAfterResult`), and the mode keeps running. The application times the result window.

`ExplorationModeTests` covers the layout, the spawners, the guardians, the objectives, the results, determinism, and the cost of 620 random spawners.

## World size

`BattleSetup` rejects a width or height above `MaxWorldSize` (2048). Above about 4096, one float step is a tenth of the contact slop and contacts lose precision. `WorldSizeTests` runs the scripted fleets near (300, 300) and near (1900, 1900) of a 2048 world: the logs differ by rounding, but the hits stay within 5 %, and the deaths and survivors match.
