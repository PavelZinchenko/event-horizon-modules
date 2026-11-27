# Combat simulator

`Assets/ModulesShared/CombatSimulation/` is the new battle simulator. It will replace `Assets/Modules/BattleSimulator/` ([battle simulator](../../../../docs/battle_simulator.md)). Event Horizon and Frontier share one copy through the `ModulesShared` submodule ([Gui: Shared code constraint](../gui.md#shared-code-constraint)).

## Status

Status on 2026-11-19:

| Game | Status |
| --- | --- |
| Event Horizon | Quick combat, campaign battles and planet exploration run on the core when `CombatSettings.UseNewCombatSimulator` is true. The flag also shows the online duel, which runs only on the core. The legacy `BattleSimulator` stays the default. |
| Frontier | The core is present and compiles, and `EngineFree.Tests` passes. The application does not use it: every battle runs on `BattleSimulator`, and Frontier's `CombatSettings` has no `UseNewCombatSimulator` field. The migration plan is in Frontier's `TODO/new_combat_simulator.md`. |

[Unity integration](unity_integration.md) describes Event Horizon's code. Frontier has no Unity integration for the core yet.

History in Event Horizon: physics and the phase 2 core (steps 1–11) exist; phase 2 is complete: a scripted battle runs on the core rules without Unity. Migration phase 3c is complete: quick combat runs on the core in a debug scene ([Unity integration](unity_integration.md)). Migration phase 4 is complete (2026-11-01): original ships, ammunition, hulls and behaviour trees run on the core, and campaign battles reach the new scene when `CombatSettings.UseNewCombatSimulator` is true. Core provides the fixed tick loop, spawn identity, the core-owned roster, roster commands, the battle result, the timer, ships with an engine, resources and control frames, the published snapshot and event batch, the contact pipeline with damage and ramming, the bullet store with the cannon, homing missile and beam, point defense, drone bays with persistent inventory, and the per-tick target grid; Runtime runs pilots in locked mode, including drone self-destruct requests. Stage P1 is closed (2026-10-17, with the known limits in [Physics library](physics.md)). It has the stage P1 features: torus broad phase, circles, convex polygons, a soft-step contact solver with speculative contacts, and a free-position query. Stage P2 is closed (2026-10-18, with its known limits): capsules, convex decomposition, compound bodies, whole-body depenetration, inner-edge contacts and the time-of-impact check of speculative contacts. Stage P3 is closed (2026-10-19, with its known limits): collision filters, bullets with swept hits and pass-through, ray casts and circle overlaps. Stage P4 is closed (2026-10-19, with its known limits): hinges, ropes, bullets attached to bodies, group teleport, and contact and joint impulses. The plan and its progress are in `TODO/battle_simulator_implementation.md`; stage results in `TODO/battle_simulator_physics.md`. The findings and decisions are in `TODO/battle_simulator_investigation.md`. The `TODO/` folder is local and is not part of this repository.

## Parts

| Part | Contents |
| --- | --- |
| [Network library](network.md) | Peer and transport contracts, channels, encoding, tick clock. |
| [Runtime network session](runtime_session.md) | Session API, handshake, session thread, duel scene and lobby, controls, wire format. |
| [Core foundation](core.md) | Battle setup, tick stages, identity, roster, engine, contacts, damage, result and timer, allegiance, snapshots, pilots, scripted battle. |
| [Battle modes](battle_modes.md) | Rules and modes, mode actions and commands, pickups, respawn, unit performance, `RosterMode`, world size. |
| [Core network authority](core_network.md) | Authority, outgoing hits, inbound owner records, fire confirmations, corrections, homing missile sync. |
| [Core weapons and bullets](core_weapons.md) | Bullets, missiles, triggers, impacts, damage effects, harpoons, satellites, weapon classes, beams. |
| [Core devices](core_devices.md) | Point defense, drone bays, repair bots, decoys, shields, teleporter, gravity generator, asteroids, device statuses. |
| [Unity integration](unity_integration.md) | Scene selection, adapter, ship and pilot mapping, views, known limits. |
| [Physics library](physics.md) | Physics world, contacts, shapes, depenetration, queries, joints. |
| [Gate harness](gate_harness.md) | Physics and network gate harness, relay, gate matrix and results. |

## Assemblies

| Assembly | Folder | References |
| --- | --- | --- |
| `CombatSimulation.Physics` | `Physics/` | `Utilites` |
| `CombatSimulation.Physics.Tests` (Editor) | `Physics/Tests/` | `CombatSimulation.Physics`, `Utilites` |
| `CombatSimulation.Core` | `Core/` | `CombatSimulation.Physics`, `Utilites`, `Diagnostics` |
| `CombatSimulation.Modes` | `Modes/` | `CombatSimulation.Core`, `CombatSimulation.Physics`, `Utilites` |
| `CombatSimulation.Core.Tests` (Editor) | `Core/Tests/` | `CombatSimulation.Core`, `CombatSimulation.Modes`, `CombatSimulation.Physics`, `CombatSimulation.Runtime`, `Utilites` |
| `CombatSimulation.Runtime` | `Runtime/` | `CombatSimulation.Core`, `CombatSimulation.Physics`, `CombatSimulation.Network` |
| `CombatSimulation.Runtime.Tests` (Editor) | `Runtime/Tests/` | `CombatSimulation.Core`, `CombatSimulation.Modes`, `CombatSimulation.Physics`, `CombatSimulation.Network`, `CombatSimulation.Runtime` |
| `CombatSimulation.Network` | `Network/` | `Utilites` |
| `CombatSimulation.Network.Tests` (Editor) | `Network/Tests/` | `CombatSimulation.Network`, `Utilites` |
| `CombatSimulation.Relay` | `Relay/` | `CombatSimulation.Network`, Unity Transport, Collections, Services (Core, Authentication, Multiplayer) |
| `CombatSimulation.Relay.Tests` (Editor) | `Relay/Tests/` | `CombatSimulation.Relay`, `CombatSimulation.Network`, Unity Transport, Collections |

Rules:

1. Physics, Core, Modes, Runtime and Network are engine-free (`noEngineReferences: true`). Follow the [engine-free module rules](../../../../docs/architecture.md#engine-free-modules).
2. The new core and the old `BattleSimulator` do not reference each other, in either direction.
3. Phase 2 does not reference `Database` or `ShipConstructor`. Phase 4 adds the database path using contracts shared with Frontier.
4. Unit tests go into `Physics/Tests/`, `Core/Tests/`, `Network/Tests/` or `Runtime/Tests/` with an Editor-only asmdef. `Tests~/EngineFree.Tests.csproj` compiles all four. `Relay` is not engine-free: its tests run only in the Unity Test Runner. Existing pilot tests remain in `Core/Tests/`.
5. Core grants internal access to Runtime and the Core, Runtime and AI test assemblies. Core does not reference Network or the application.
6. Physics, Core, Modes and Runtime are `autoReferenced`, so the application adapter in Assembly-CSharp can use them. An asmdef cannot reference Assembly-CSharp, where the application contracts live.
