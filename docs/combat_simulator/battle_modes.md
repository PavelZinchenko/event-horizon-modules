# Battle modes

Part of [Combat simulator](index.md).

The core runs the world: physics, ships, devices, bullets, contacts, damage and the units. It has no game rules. A **battle mode** is the game rule code: who deploys, where, when the battle ends, and why. A mode is engine-free and deterministic, and it runs inside `Battle.Step`. A new mode needs no core change. A new mechanic (docking, loot, perks) still needs a core primitive, one time.

| Mode | Assembly | Game |
| --- | --- | --- |
| `RosterMode` | `CombatSimulation.Modes` | Event Horizon's roster battle and online duel. Frontier's PvP duel can use it. |
| Frontier's modes | A Frontier engine-free assembly (planned) | The common level, the arena and the other Frontier modes. |

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
| `Drain(UnitId, armorFraction, shieldFraction)` | At stage 10, after the damage: the unit's active ship of a local owner loses the fractions of its maximum armor and shield. No resistance; both delays restart. |
| `Publish(ModeEvent)` | Adds a `BattleEventKind.Mode` event with the mode's code and two values. Also allowed in `EndTick`, where it comes before the result. |
| `Random(purpose)` | A counter-based random state of the match seed, the tick and the mode's purpose (`RandomPurpose.Mode`). |

Rules:

1. An action outside `BeginTick` throws. `Publish` is also allowed in `EndTick`.
2. With remote owners every action except `Drain`, `Publish` and `Random` throws (D10). The online duel runs only `RosterMode`, which uses only `Drain`. A networked mode can come later: a mode changes the battle only through actions, so authority can be added to them.
3. A mode reads no Unity state, no wall time and no `Dictionary` enumeration order.

## Mode commands

`ModeCommand` is an application input that the simulation cannot decide: the player buys a life, a cheat button, an exit. It carries battle ID, command ID, target tick, a kind and two `long` arguments. The core does not interpret it. It goes through `Battle.Submit` and the command queue, so it shares the command ID namespace and the "past tick" check, and a replay gives it again. `BattleRunner.SubmitModeCommand` assigns the command ID.

## Units added during the battle

The unit table and `BattleSnapshot.Units` grow when a mode adds a unit. Units are never removed, so a long level adds one unit per enemy. The snapshot array grows as the other arrays do; the growth allocates. `UnitSetup.Allegiance` (default `Allegiances.OfSide(side)`) is the allegiance of the unit's ships and drones at deployment.

## Respawn

`Respawn(UnitId)` turns a `Destroyed` unit to `Ready` (`UnitTransitionReason.Respawned`). The death before it is a normal death: `Removal` (`Death`), the death devices, and `Killed`.

1. The armor and shield deficits become zero.
2. Each drone bay and cloning centre refills: the hangar has the capacity less the unit's live drones (legacy `DroneBay.LookForDrones`).
3. The next deployment of the unit gives its live drones to the new ship (`MothershipOverride`, unless a capture or berserk set one). A retreat and a redeployment without a respawn do not: the drone keeps its retreated mothership, as Main's AI expects.
4. While the unit is `Destroyed`, `AiContext.IsMothershipDestroyed` is true for its drones.
5. Only a mode respawns. `RosterMode` never does, so `Destroyed` stays final in Main's battles and in the network code.

## Result

`BattleResult` has outcome, winning side, reason and tick. A mode's own result uses `BattleEndReason.Mode` and a `ushort ModeReason` that the mode defines. `ToString` prints "Mode n" for it; the other reasons print as before.

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

## World size

`BattleSetup` rejects a width or height above `MaxWorldSize` (2048). Above about 4096, one float step is a tenth of the contact slop and contacts lose precision. `WorldSizeTests` runs the scripted fleets near (300, 300) and near (1900, 1900) of a 2048 world: the logs differ by rounding, but the hits stay within 5 %, and the deaths and survivors match.
