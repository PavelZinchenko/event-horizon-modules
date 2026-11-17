# Core devices

Part of [Combat simulator](index.md).

## Point defense

Phase 2 steps 9 and 10 (Q4). The owner chose payment before each zap and legacy checking while ready. Step 10 adds drone energy damage.

1. **Definition.** `PointDefenseDefinition` supplies radius, cooldown seconds, energy cost and optional nonnegative `droneDamage` (default 0). Radius is positive; cooldown and cost are nonnegative. All values are finite. The device needs no barrel. The legacy fixture uses radius 4 (size 3 plus hull radius 1), cooldown 0.3 s and cost 1.
2. **Activation.** Stage 2 checks the held bit and pays the energy cost once. Release or retreat disables the device before queries. Failed zap payment also disables it. Reactivation needs payment and `TickTime.AtLeast(cooldown)` ticks since shutdown. Zero-cost activation and zaps retain the existing resource rule requiring positive available energy.
3. **Query timing.** The first query occurs at first activation tick plus `TickTime.After(cooldown)`. A successful query processes all affordable targets, then the next query waits the same delay. At 0.3 s the delay is 16 ticks. Empty queries leave the device ready, so it queries again next tick. Disable/re-enable preserves the query deadline; redeployment creates fresh state.
4. **Overlap.** Stage 6 queries a circle at the ship's current position, before physics. It includes enemy missiles and energy bolts spawned at stage 5. Enemy drones are also included. Allies, roster ships, beams and bullets pending removal are excluded. Physics measures distance to the target surface and handles torus wrapping. The reusable buffer starts at 32 entries and grows on overflow until every target fits.
5. **Payment and ordering.** Projectile overlaps become time-zero `Disarm` candidates. Drone overlaps become time-zero `Hit` candidates, one per device and drone, including compound hulls. Disarms sort before hits, so projectiles receive payment first. Drone damage reduces shield, then armor at stage 10; it never drains energy. Drones remain valid targets throughout the contact walk, even after lethal accumulated damage. The ship ID and device index identify the attacker. Stage 9 checks contact validity before payment. A failed payment spends nothing, leaves the target valid and stops this device's remaining zaps. A target already disarmed by another device costs nothing and does not reset the skipped device's query deadline. Accepted zaps restart the energy recharge delay.
6. **Removal and events.** An accepted disarm immediately invalidates the bullet within the contact walk, before any time-zero hit. Projectiles and beams can then select valid targets behind it. Stage 9 emits `BattleEventKind.Disarm` with attacker ID, target ID, `DeviceIndex` and the target position sampled at stage 6. Stage 12 removes the bullet with `Disposal`. The device has no separate entity.
   An area bullet is never a target (phase 5, step 3). A bullet with `CanBeDisarmed` false (legacy `Body.CanBeDisarmed`, phase 5) is zapped the same way: payment, delay, `Disarm` event and its `Hit` triggers. It stays a valid attacker and target and flies on, on both devices. Its owner counts each zap once (attacker, bullet, tick), whether its own copy or a remote claim reported it.
7. **Authority.** Defense energy and cooldown are simulated on both copies. The bullet owner commits destruction; the other copy removes the bullet by prediction.
8. **Validation.** All 534 engine-free tests pass, including 29 point-defense cases. Coverage includes payment, cooldowns, retreat, redeployment, filtering, wrapping, same-tick spawns, overlapping devices, allocation order, real beam/projectile continuation and 70-target salvos. Repeated interception and activation allocate zero bytes after warm-up. The large-salvo test also fixed a resolver bug: array growth must finish before the disarm array reference is fetched.

Core and Core.Tests compile separately with Unity 6000.0.75f1's compiler and assembly references. Unity Editor imported the new sources and compiled both assemblies.

## Drone bays and self-destruction

Phase 2 step 10 (Q5/Q6). A drone is a full `ShipEntity` without a roster slot. Its bay belongs to `(RosterId, device index)`.

1. **Definition and inventory.** `DroneBayDefinition` supplies positive capacity, nonnegative finite launch energy cost and a drone `ShipDefinition`. A launched ship launches none: a drone or clone with a bay or a clonning center is rejected, as legacy excludes them on drones (`DeviceDefinition.ChildShip`). A clonning center (phase 5, step 7c; [Device statuses](#device-statuses)) has the same inventory without a capacity, so its clone is a drone of the slot. Each roster slot owns persistent bay inventory. Launches consume hangar stock; only a drone replicator restores it (item 10). Surviving drones and unlaunched stock remain through retreat and redeployment.
2. **Launch timing.** Held controls launch while stock, energy and the shared mothership bay timer permit. The timer is separate from weapon barrels. It uses `TickTime.After(0.4f)`: first launch at deployment tick + 20, subsequent launches at least 21 ticks apart. Device index resolves competition between bays. Failed payment spends nothing. Release preserves readiness; retreat blocks launch; redeployment resets the timer.
3. **Intentional timing correction (owner, 2026-10-21).** Legacy `ShipFactory.CreateDroneBays` registers the same platform twice. `ShipSystems` therefore advances its 0.4-second timer twice per tick. The new core advances it once and uses the declared duration.
4. **Spawn.** Stage 2 pays and queues; stage 5 creates at the mothership position and heading, with zero velocity and empty controls. The child ID uses the current mothership ID, tick, device index, lifetime launch slot and sequence. The free-position query searches up to 40 units. On failure it still launches at the returned least-overlapping position and retains the payment (owner, 2026-10-21).
5. **Lifecycle.** Mothership retreat or destruction does not remove drones. Redeployment exposes the new mothership ID through the same bay without resetting drone resources, controls or pilots. Surrender and kill-all destroy roster slots; surviving drones remain in the frozen result snapshot. Drones do not consume roster deployment limits, prevent elimination, prevent timer pauses or reset the timer (owner, 2026-10-21).
6. **Combat.** Drones use the ship engine, resources, devices, damage and ramming pipeline. They touch all drones and enemy roster ships, but pass through allied roster ships. Enemy projectiles and beams hit them. Newly launched drones participate in same-tick point-defense queries and physics. Their first systems/motion update and target-grid entry occur next tick.
7. **Public state.** Ship snapshots and spawn/removal events include nullable `BayKey` and `IsDrone`; a drone's `RosterId` is default. The snapshot supplies the current `MothershipId`; events use `ParentId`. An absent mothership has a null entity ID. No drone removal creates a roster transition. Both snapshot buffers grow to include drones.
8. **Pilot command.** `SelfDestructCommand` carries battle ID, command ID, target tick and entity ID. `Submit` rejects wrong battles, past ticks, null IDs and duplicate pending command IDs. Stage 1 processes it after roster commands and before frames, rejecting targets that are not live drones. Stage 11 marks death; stage 12 removes the drone normally. Runtime exposes `SelfDestruct` and forwards `PilotControls.SelfDestruct` at the locked delay. Providers select `PilotSchedule.Drone` (delay 2, period 2). Pilots decide whether an absent mothership warrants self-destruction.
9. **Allocation.** Pooled ships cache device instances per definition and reset their transient state on reuse. The ship free list reserves capacity as the pool grows. Repeated launches, drone deaths and weapon fire allocate zero bytes after warm-up. Sustained drone zaps also allocate zero bytes.
10. **Drone replicator (phase 5, step 10a).** Legacy `DroneReplicator` is `DroneReplicatorDefinition` (build time 1 / `DroneBuildSpeed`), a passive device without a control bit; the mapper puts it before the bays. Its timer starts at deployment, as if one tick earlier, and ends after `TickTime.AtLeast(build time)` ticks. At the end, the first drone bay in device order with room spends one of its `BuildExtraCycles`, or gets one drone back and refills its cycles; then the timer stops. The bays refresh every `TickTime.After(0.5f)` = 26 ticks from deployment; a refresh with room in any bay restarts a stopped timer in that tick (legacy, question 5a). Room counts only live drones; legacy also counts the drones lost since the last refresh (owner, 2026-11-08: a behaviour change). A restored drone can launch in the same tick. The device runs during a retreat (legacy blocks activation only). Clonning centers are not restored. Only the owner restores: a remote copy takes a launch record beyond its hangar as a restore when the slot has a replicator; no record is new. Bay HUD cooldown (legacy `DroneBay.Cooldown`): the launch timer while the hangar has a drone; 1 while the drones fill the bay or without a replicator; else the replicator's remaining share plus build time × cycles left, at most 1.

## Repair bots

Phase 5, step 10b. `RepairBotDefinition` and `RepairBotDevice` port legacy `RepairSystem`; `RepairBots` owns the auxiliary bodies.

1. **Activation.** A held bit pays `EnergyConsumption` per second, including the first tick. There is no separate start cost. Release, retreat, blocked controls or failed payment ends the use and starts the device cooldown. Repair bots keep stealth. A destroyed bot blocks the device until the removal tick + 1 + `TickTime.AtLeast(Lifetime)`; a held device then launches a replacement. The HUD shows cooldown 1 during this lock.
2. **Body and movement.** Each bot has its own entity, circle of radius `Size / 2`, mass `Size`, and hit points `ArmorMultiplier * Size`. It uses the drone collision filter but never enters the ship store, target searches, roster results or kill counts. `Satellites.Steer` supplies the legacy movement law with factors 0.75 and 5, angular drag 0.05, and the 50-unit jump. Its slot orbits at radius `ship scale / 2 + Range`, at 120 / radius degrees per second. On release it returns to the ship centre and vanishes inside its scale. Ship removal removes its bots.
3. **Repair cycle.** Motion starts after the launch tick. Ages 101?150, 251?300, and each following third second add `Power / 100 * maximum armor * 0.02` repair per tick. The cycle follows the bot's creation, including a returning bot that is reactivated. Integer ticks give a full second of repair without accumulated float drift. Repair uses the normal pending-impact stage and armor cap.
4. **Contacts.** Projectiles, beams, areas, point defense and hostile ramming damage bots without armor resistance. Bots deal no ramming damage. Exactly zero hit points survives; strictly negative hit points destroys. Teleport has no effect on a bot. Sticky mines and harpoons can attach to its body. Ghost and ship invulnerability do not remove bot contacts.
5. **Network.** Owners send device starts and ends, a launch per bot, and removals. Launch identity uses the ship, tick, device index, slot zero and sequence zero. The existing bullet-spawn record carries the launch pose; contact records use `TargetIsBullet`, as tail segments do. Contact deduplication prevents a remote hit and a locally observed copy from counting twice. Bot pushes travel with hits. Motion corrections use the existing projectile correction format and bandwidth budget, always including velocity; health remains owner-only. A late launch or correction replays movement against the current parent pose, without replaying repair. Ship resource corrections reconcile delayed repair. No codec version changes.
6. **Presentation and checks.** `BattleSnapshot.RepairBots` supplies the sprite pose and repair state. The renderer uses `Satellites/repair_bot`, the device laser color and view-only ?30? aim jitter. No sound is added. Nineteen tests cover lifecycle, healing, collisions, late records, deduplication, corrections and two serialized owner streams. All 1,410 engine-free tests pass; baseline hashes are unchanged. Unity compilation and *Check Ship Builds* pass: 377 mapped, 0 failed; coverage 416 supported, 46 inert, 2 deferred. Components 79 and 80 cover 63 builds. Owner playtesting follows step 10d.

## Decoys

Phase 5, step 10c. `DecoyDefinition` and `DecoyDevice` port legacy `DecoyDevice`; `Decoys` runs the decoy units (legacy `Decoy`).

1. **Activation.** A held bit with the cooldown passed and `EnergyConsumption` in store spends it once and launches round(`Power`) decoys. The cooldown runs from the use. A use ends stealth.
2. **Entity.** A decoy is a `ShipEntity` of its own kind (`IsDecoy`, owner question 2a) in the drone list, after the roster ships in entity ID order. Its definition (`DecoyDefinition.Ship`) has a circle of radius 0.25, scale 0.5, mass 0.1, no engine, no devices, no resistances, ramming multiplier 0, target priority `High`, and is teleport immune. Armor holds the hit points, `ArmorMultiplier × 10` of the ship. It takes the side, owner and allegiance of its ship and the drone collision filter. It has no slot, bay or pilot, does not keep its side alive, gives no kill, and stays when its ship leaves.
3. **Launch.** Stage 5 creates decoy i at the ship centre plus the unit vector at (90 + 180 i) / count degrees (integer division) + heading + 90, at rotation 0, with the ship velocity plus that vector × `Range`. Its identity is the ship, tick, device index, slot i and sequence 0. Counter-based draws (purposes `DecoySpin`, `DecoyLifetime`; behaviour changes row "Decoy and repair bot random") give a spin of −90..89 whole degrees per second and a lifetime of `Lifetime` × (0.75 + 0.5 r), rounded up to ticks.
4. **Motion and life.** From the tick after the launch, stage 4 lowers the velocity by itself × 0.02 and the spin by 10% of itself × 0.02 per tick. Stage 11 marks the death at spawn tick + lifetime ticks. `ShipState.Life` falls from 1 at the spawn to 0 at the expiry.
5. **Damage.** Stage 10 sums the pending damage of every type, without resistances or shield. A total above the hit points kills the decoy; exactly zero hit points survives (legacy strict check). Repair, recharge and drains do nothing; a siphon heals nothing from a decoy. Slow-down and roll apply as on ships. Point defense ignores decoys (it reads drones only); sticky mines and harpoons attach to them.
6. **Searches.** The missile grid, the barrel and satellite searches take a decoy as any ship, so its priority `High` wins. The AI ship search (`EnemyOptions.ForShip`) skips decoys. The drone search draws its 30% take chance in the legacy order; when it hits, the first decoy replaces any enemy that is not a decoy, whatever its score. Additional targets (`TargetLists.UpdateEnemies`), `FindDamagedAlly` and `HoldHarpoon` skip decoys. `AiContext.ProfileOf` gives every decoy `AiShipProfile.Decoy` (scale 0.5, no weapons). The threat list reads bullets and asteroids, never a decoy.
7. **Network (H8).** The owner sends one bullet-spawn record per decoy with its launch position and launch angle; the copy takes the velocity from its spawn sample of the ship at that tick and replays the drift of a late record. The record starts the copy's cooldown. Removals travel by bullet name, without a bay. A contact that involves a remote owner travels as an auxiliary contact (`TargetIsBullet`), with damage and push only; its slow-down and roll are left to corrections. Decoy poses use the missile correction format with velocity, never the body format. Missile, sticky and harpoon targets name a decoy by entity ID (`TargetIsSegment`). Hit points belong to the owner; a copy dies only by the owner's removal. No codec change.
8. **Presentation.** The adapter draws `Resources/Textures/star` (the legacy prefab sprite) at size 1 in the device colour; the opacity is 1 − (1 − Life)² (legacy `SpriteView`). A death fades the view. No sounds and no spawn or explosion effects (H11).

## Shields

Phase 5, step 10d. `ShieldDefinition` and `ShieldDevice` (`Devices/Shield.cs`) port legacy `EnergyShieldDevice` and `FrontalShieldDevice` with their `EnergyShield` unit and `EnergyShieldDamageHandler`; `ShieldRules` holds the shape and filter rules. Tests: `Core/Tests/ShieldTests.cs`.

1. **Activation.** Energy shield: a held bit with the cooldown passed and `EnergyConsumption` in store spends it once; the use lasts while the bit is held. Frontal shield: a held bit with `EnergyConsumption` in store pays it per second, the start tick included; a failed payment ends the use. Both end on release, retreat or blocked controls, and their cooldown runs from the end. An energy shield keeps stealth; a frontal shield ends it. Only the owner starts and ends a use.
2. **Shape.** Each shield is one shape on the ship body, registered with shield priority and the shape order hull part count + device index. It is massless (`ShapeDef.Massless`), so the ship's mass centre and inertia stay as they are. Off, it keeps its ship category and ignores every category. Energy shield: a circle of radius `0.5 / ImageScale` of its `CircularSpriteObject` prefab record (0.5 for the default prefab) × `Size` × model scale. Frontal shield: the first collider outline (3 or more points) of its `ColliderSpriteObject` prefab record, else the 7-point `FrontalShield` prefab polygon (phase 5b, step 7c; built-in record 4 holds that polygon), × `Size` × model scale, moved by `Offset` × model scale. A running energy shield sets `NoContacts` on the hull; the shield shape itself never reads `NoContacts`, so InfinityStone does not turn it off.
3. **Contacts.** Hostile bullets, beams, areas and ships touch the shape; its ship's own bullets do not. A contact on the shape names the ship. The shield takes no push, slow-down, roll, capture, siphon or lifetime restore (legacy shield units are not ships). Teleport moves its ship. Energy drains and recharges through a shield lose 75%. A shield contact is not a stealth contact and not a ship touch. D16 still counts one over-time or all-targets hit per attacker and ship per step, so an area that covers a frontal shield and its hull hits only the shield (behaviour changes row "Area hit on a frontal shield and its hull").
4. **Ramming.** A ship that touches with its shield deals no ramming damage. A rammer's damage on a shield pays the shield's energy (owner question 6).
5. **Shield rule (stage 10).** Before the ship's shield points, each shield sums its contacts of the tick: the damage of each type less its shield resistance, plus the shield drain less the shield recharge. That total / (`Power` × `ShieldMultiplier`) is the energy cost. Paid, the damage goes; the shield drain still reaches the ship's shield points. Not paid, all energy goes: the absorbed amount removes the shield drain first, then the damage group by group in shield order (corrosive first), and the shield turns off. The rest joins the ship's pending impact. An `IgnoreShield` contact is lost and turns the shield off. A turned-off energy shield gives the hull its contacts from the next tick.
6. **Network (H8).** The owner sends device-use start and end records. A remote attacker sends its shield contacts as ship hits on the shield shape order, without a push; over-time and ramp records keep that shape. The receiver applies the shield rule. A hit on a shield that is off by now reaches the hull (hits in flight count). An `IgnoreShield` contact travels as `DamageIgnoringShield` on the shield shape. A copy never starts a shield from its own controls and never pays the start cost; corrections reconcile its energy. No codec change.
7. **AI.** `DefendWithFronalShield` turns to the sum of the directions to the live threats and holds the first frontal shield while within 90°; without a live threat it fails. `MothershipLowEnergy` reads the mothership's energy share; `MothershipLowShield` reads it when the mothership has an energy shield, else the shield share. A missing resource gives 0 (legacy `EmptyResources`).
8. **Presentation.** `BattleSnapshot.Shields` gives each shield's state and the ticks since its last contact. The renderer draws the prefab record's image, else `Textures/Effects/shield` (energy) or `Textures/Effects/frontal_shield` (frontal), as a child of the hull view, `Size` × model scale wide at the offset. The colour goes from transparent to the idle colour (device colour, alpha × 0.3 for an energy shield, × 1 for a frontal one) by 4 × the elapsed time of the distance. For 1 s after a contact it fades from the device colour. No sounds (H11).

## Death spawns

Phase 5, step 11a. `DeathSpawnDefinition` and `DeathSpawnDevice` (`Devices/DeathSpawn.cs`) port legacy `ToxicWaste` and `RetributionDevice` as one device (owner decision 1). Tests: `Core/Tests/DeathSpawnTests.cs`.

1. **Trigger.** The device has no control bit and never activates. When its ship leaves with reason `Death`, `Battle.RemoveShip` calls `ShipDevice.OnDeath` on the owner, before it releases the ship. Kills, self-destruction, surrender and kill-all count. A retreat, a vanish and a drone's return do not. An InfinityStone that saves the ship prevents the death.
2. **Spawn.** `SpawnSystem.SpawnAtDeath` creates one bullet at once, at stage 12. It starts at the ship centre and heading after the physics of the tick (`SpawnRequest.ShipHeading`), with the ship velocity × the bullet's `ParentVelocityEffect`. Its spawn tick is the death tick, and its identity is the ship, that tick, the device index, slot 0 and sequence 0. The bullet first moves in the next tick, so it lives one tick less than its lifetime. Its `Created` triggers run at once.
3. **Owner.** The bullet takes the dying ship's ID, side, allegiance and owner, also for a drone (legacy: the drone's mothership; behaviour changes row "Drone toxic cloud owner").
4. **Mapping.** `CoreShipMapper.MapDeathSpawn` maps the device's `AmmunitionId` with `CoreWeaponMapper.MapDeviceBullet`: legacy `BulletFactory` at power level 1, with `DamageMultiplier` = `Power` × the ship damage multiplier. `ToxicWaste` also sets `AoeRadiusMultiplier` = model scale × `Size` + 1 (decision 2), so ammunition 186 (`ToxicCloud`, radius 1) gives the legacy radius. `Retribution` keeps multiplier 1 (decision 6). A `ToxicWaste` record without `AmmunitionId` (mods, until E5) gets ammunition 186. A beam, a harpoon or a bullet attached to its ship is inert.
5. **Network (H8).** The owner sends one bullet-spawn record with the launch pose, before the ship's removal record. The record's tick is the death tick, so a receiver applies it while its copy of the ship still exists, also in a late batch. The copy takes the velocity from its own copy of the ship. A record later than the bullet's life adds no bullet but keeps the parent for its children (the wraith missiles).
6. **Presentation.** The cloud draws through its ammunition's own `Created` trigger effects (phase 5b, step 6). A carrier without a prefab (the wraith packs, also `AntiMatterBomb` ammunition 3 and 4) has no look; its trigger sounds and effects play (phase 5b, step 6), as legacy draws only its trigger effects. No smoke, flash or sounds (H11).

## Teleporter

Phase 5, step 11b. `TeleporterDefinition` and `TeleporterDevice` (`Devices/Teleporter.cs`) port legacy `TeleporterDevice`. The device is obsolete: an early game version had it, and `WarpDrive` replaced it. Players cannot get it; bosses (8 builds) and mods still use it. Tests: `Core/Tests/TeleporterTests.cs`.

1. **Activation.** A held bit with the cooldown passed and `EnergyConsumption` in store spends it once. The cooldown runs from the use, so a held bit jumps again at each cooldown. A use ends stealth. Ghost and the Detonator lock block it.
2. **Jump.** At stage 2 the ship moves `Range` along its heading with `PhysicsWorld.TeleportGroup`: torus wrap, an exact landing without a free-space search, hinged tail segments and attached bullets move with it. The linear velocity and the spin go to 0. `TeleportImmune` does not apply; it is for impacts only. Satellites and a repair bot follow by their own laws.
3. **Event.** Each jump adds a `Teleport` event: the ship, the device index, the start point (`Point`) and the landing point (`Destination`).
4. **Network (H8).** The owner sends a device-use record at the use tick. The copy does not jump (question 2 (a), as for teleport impacts): it starts its cooldown at the owner's tick, adds a `Teleport` event from its own pose, and follows the jump by position corrections. A copy never jumps from its own controls. No codec change.
5. **Mapping.** `EnergyConsumption` as the cost, `Cooldown`, `Range`. A device with `VisualEffect` or `EffectPrefab` gets a flash look: the device colour at `Size` × model scale.
6. **Presentation.** The adapter draws one flash at each end of the jump for 0.5 s (legacy `StaticEffect` on `OnActivate` and `OnDeactivate`), with the hit-flash sprite. No sounds (H11).

## Gravity generator

Phase 5, step 11c. `GravityGeneratorDefinition` and `GravityGeneratorDevice` (`Devices/GravityGenerator.cs`) and `GravityWaveQueries` (`Devices/GravityWaveQueries.cs`) port legacy `GravityGenerator` as a device effect, not a bullet. Only `Boss02` (build 3, component 68) has it. Differences from legacy are rows in the behaviour changes table of `TODO/battle_simulator_implementation.md`. Tests: `Core/Tests/GravityGeneratorTests.cs`.

1. **Activation.** A held bit with the cooldown passed and `EnergyConsumption` in store spends it once. The cooldown runs from the use, so a held bit fires again at each cooldown. A use ends stealth. Each use adds a `GravityWave` event: the ship, the device index and the wave centre (`Point`).
2. **Wave.** From the use tick, for 25 ticks (legacy 0.5 s, fixed), stage 6 queries a circle of `Range` at the use point (`CollisionFilters.GravityWave`: a query category no shape has, so only a shape that ignores everything ignores it). Each unit counts once per use.
3. **Targets.** Every unit in the circle except the own ship (its hull, shields and satellites), its tail segments, its repair bot and the bullets attached to it (`AttachedToParent`). The own drones, decoys and free bullets count. Beams and beam bolts do not. Shield shapes drop the push (legacy shield units, 10d finding 3): an energy shield takes the hull's contacts, so its ship is not pushed; a frontal shield leaves them, so its ship is. A ghost or another unit without contacts is skipped.
4. **Effect.** Stage 9 removes a bullet with reason `Death`: its `Destroyed` and `Detonated` triggers run, no `Hit` trigger. Any other unit takes an impulse of `Power` × √(its body mass) at its origin, away from the centre, from the poses at the start of the step; there is no damage. A tail segment takes a tenth, as for every push. A unit at the exact centre gets no push.
5. **Network (H8).** Only the owner runs the wave. The owner sends a device-use record; the copy starts its cooldown and adds a `GravityWave` event from its own pose. A remote ship gets a zero-damage hit record with the impulse; its owner pushes it when the record arrives. A remote repair bot or decoy gets a contact record with the impulse. A remote tail segment's record has no push, so the segment follows corrections. A remote bullet: the owner predicts the removal and sends a stop record with the generator's device index; the bullet's owner removes it with reason `Death` and, because of the device index, runs no `Hit` trigger. No codec change.
6. **Mapping.** `EnergyConsumption` as the cost, `Cooldown`, `Range` and `Power`, none scaled by the ship. A device with `VisualEffect` or `EffectPrefab` gets a flash look: the device colour at 2 × `Range` for `Lifetime`.
7. **Presentation.** The adapter draws one flash at the wave centre (legacy `FlashEffect` on `OnActivate`, which follows the ship). No sounds (H11).

## Asteroids

Phase 5, step 14 (H10; legacy `Asteroid`, `CombatManager`). `AsteroidSetup`, `Asteroid` and `Asteroids` (`Asteroids/Asteroids.cs`); adapter and view `CoreAsteroids`. Rules and examples: `TODO/battle_simulator_phase5_part2.md`, "Asteroids (14, M4)". Tests: `Core/Tests/AsteroidTests.cs`.

1. **Setup.** `BattleSetup.Asteroids` lists them: position, velocity, size (the circle diameter), mass, hit points and ramming multiplier, all finite; size, mass and hit points positive. The adapter draws them from the battle seed; the core holds no asteroid rule (owner question 3). A battle with a remote owner takes none (owner question 2): nobody owns an asteroid, so the setup throws.
2. **Creation.** The `Battle` constructor creates them in setup order, before tick 0, so the tick âˆ’1 snapshot lists them. Each is a body with one circle (`EntityKind.Asteroid`), angular damping 0.02 (legacy angular drag) and no linear damping. Its owner is the first roster owner, a local one.
3. **Collision.** `CollisionFilters.Asteroid` is a category of its own outside the allegiance groups (legacy layer `Default` pairs with every layer). Ships, drones, decoys, bullets, beams, tail segments, satellites, repair bots and other asteroids meet it; a unit without contacts does not. Point defense skips it. Its allegiance is `Undefined` (legacy side): hostile to every other allegiance, a friend of another asteroid and of a crazy drone.
4. **Hits.** A projectile, a ray, a beam bolt, a stuck bullet's damage over time and a gravity wave reach it through the contact pipeline. Stage 9 adds the candidate's total damage of every type, without resistance, and applies a projectile's push or a gravity wave's impulse at its origin. No other effect acts on it (legacy `Asteroid.OnCollision` reads damage, impulse and `Destroy`; `Devour` and `BlackHole` are deferred, H4). A sticky bullet and a harpoon hold it as any body.
5. **Ramming.** `RammingTracker` treats it as a ship: it rams hostile ships, drones, decoys, tail segments and repair bots with its own multiplier, and they ram it with theirs (legacy `DefaultCollisionBehaviour`). Two asteroids never deal damage.
6. **Death.** Stage 10 applies the damage; below 0 hit points the asteroid dies (legacy strict test). Stage 12 removes it after the repair bots and adds an `AsteroidRemoval` event: entity, last position, side âˆ’1. No credit, no loot.
7. **Placement.** `TryFindFreePosition` counts asteroid shapes, so a deployment or a launch moves clear of an asteroid. Legacy `FindFreePlace` checks ships only; the core keeps its rule (owner question 6, 2026-11-09).
8. **AI threats** (step 14b). `TargetLists.UpdateThreats` reads asteroids after bullets, with the legacy rule: within 20 units, a path that passes the ship within 5 s, the asteroid size as its scale. `AiContext.TryGetThreat` gives `AvoidThreats` and `DefendWithFrontalShield` the position and velocity of either.
9. **Obstacles** (step 14c). At stage 8 `BodyContactMemory` sets each ship's last body contact and whether it still touches it (legacy `Collider.LastCollision` and `ActiveCollision`); `ShipState` carries both. Only hull contacts with a ship hull, a tail segment, a repair bot or an asteroid count. A touch that began in the step wins, smallest entity ID first; else the last contact stays while it touches; else the touching body with the smallest ID replaces it. `TargetLists.IsObstacle` ports legacy `ThreatAnalyzer.IsObstacle`, and `BypassObstaclesNode` ports the legacy node (no longer a stub).
10. **Adapter** (step 14d). `CoreAsteroids.Layout` (`Assets/Scripts/Combat/Integration/Core/CoreAsteroids.cs`) draws 10 asteroids unless `DisableAsteroids`, from a `System.Random` seeded from the battle seed, in the legacy order per asteroid: size (uniform 2-5), position (whole numbers, uniform over the torus), velocity (a uniform point of the unit disc × 10 / size). Mass is size² × 5, hit points size² × 100 × p, and the ramming multiplier p, where p = `Experience.LevelToPowerMultiplier(GalaxySettings.EnemyLevel(StarLevel))`. The adapter has only local owners. *Check Ship Battles* adds the same layout with p = 1, from the build seed.
11. **View** (step 14d, H11). `CoreAsteroids` instantiates the `CombatView/Objects/Asteroid` prefab per asteroid at scale = size (phase 5b, step 8: the legacy prefab without its rigidbody, collider and physics components); its `AsteroidView` builds the rock mesh and spins it, presentation only. The root takes the interpolated core pose. On `AsteroidRemoval` the adapter removes the view, and the renderer plays legacy `AsteroidExplosionAction`: grey `Smoke` at 2 × size and `SmokeAdditive` at 1.5 × size for 2 s, and `explosion_08`. No disturbance.

## Device statuses

Phase 5, steps 7a to 7d (data-model gate, H2) and step 9 (statuses). Examples: `TODO/battle_simulator_phase5.md`,
"Data-model gate (7)" and "Statuses (9)"; tests: `Core/Tests/GateDeviceTests.cs`, `Core/Tests/WormTailTests.cs`,
`Core/Tests/StealthTests.cs`, `Core/Tests/EngineDeviceTests.cs` and `Core/Tests/GhostFortificationTests.cs`. Each device stays a small `ShipDevice` class that composes these parts:

1. **Activation policy.** `ActivationPolicy` is data in the device definition: energy cost, cooldown, when the cooldown starts
   (`AtStart` or `AtEnd`), duration, and an optional release time after which a released bit ends the use. `DeviceActivation`
   runs it: the cooldown timer (ready at once, as legacy `TimeFromLastUse` 3600 s), the start tick and the running flag. A
   free use needs no energy (legacy `CanBeActivated` has no energy check without a cost).
2. **Ship status.** Stage 3 asks every device of every ship for its status (`ShipStatusFlags`, a mask of blocked devices).
   `NoContacts` keeps the hull categories and ignores every other category, so no shape pairs with the ship: no hits, no
   ramming, no solid contact. The point-defense query has no category; it skips a drone without contacts itself. Homing and
   the AI still see the ship. `EngineOff` gives the engine throttle 0 and no course (the spin brake stays); the engine input
   with it is kept for the correction re-run. `Drift` gives no engine at all: no thrust, no turn and no spin brake, so the
   ship keeps its velocity and spin. `Faded` is for the view (10% opacity). `Stealth` gives target priority `None` and
   fades the view (step 9b; device 6). `FullThrottle` gives throttle 1; `EngineOff` wins over it (step 9c; device 7). Two
   engine values join the flags (step 9c): `EngineScale` multiplies propulsion and `MaxVelocity`, and `Deceleration` is the
   brake sum (device 8). The engine input keeps both for the correction re-run. The ship snapshot carries the flags, and
   `ShipState.Tint` (0 to 1, the largest of the devices) for the view's colour change. A detonator gives its elapsed share
   of the lock; a ghost and a fortification step `DeviceTint` each tick (legacy `OnUpdateView`: 5 × the elapsed time of the
   distance, toward 1 while on and back to 0 after). The view lerps from white to the mapping's `TintColor`, alpha
   included, and multiplies the warp's 10% into that alpha.
3. **Blocked devices.** The mask of stage 3 holds through stage 2 of the next tick: a blocked device sees its bit released, the
   same for every device of the ship (approved change: legacy blocks the devices after the blocking one at once).
4. **Lethal damage.** At the end of stage 10, after the timer drain, a local ship without armor asks its devices in index
   order; the first that saves it ends the round. Every hit of the tick counts first (H3). Stage 11 then finds no death.
5. **Retreat.** `UpdateRetreating` replaces `Stop` at stage 2 of a retreating ship. Devices stop as before; a running use goes
   on as with a released bit (legacy `ShipRetreatEffect` blocks activation only).
6. **Network (H8).** Only the owner starts or ends a use. It sends a `DeviceUse` operation (ship, device, started) that names
   the use's first status tick; the copy applies it at stage 1 of that tick, or at once when late. A copy never starts a use
   from its own control bits or damage. Hits a remote attacker commits before the record arrives still count (attacker
   authority; one network delay).
7. **Ship-centre spawn.** A device may request a bullet at the ship centre (`SpawnRequest.ShipCentre`): rotation 0 and no
   spread (legacy `CreateArea`). Its identity has the device index as source and
   spawn slot 0. The owner sends a `BulletSpawn` record with the launch pose; the receiver adds the bullet from it and never
   derives it (H8), so it appears one network delay late, and not at all once it has expired there.
8. **Child ship.** A device with a `ChildShip` launches through the drone path (`SpawnRequest.Launch`): the slot's inventory,
   the child identity with the lifetime launch slot, a `DroneSpawn` record and drone collision. `OnShipLaunched` tells the
   device its child, on the owner at stage 5 and on a copy from the launch record. `OnShipLost` tells the device of the
   slot's active ship that a child of its index left; the device compares the ID, so a redeployed ship ignores the child of a
   former deployment. `DeviceActivation.Reset(policy, tick, share)` starts a timer partly elapsed.
9. **Resistance share.** A status may carry `ResistanceShare` from 0 to 1. Stage 10 moves each armor resistance r of the ship
   to r + share × (ceiling − r), where `DamageTypeDefinition.ResistanceCeiling` is 1, and 0.5 for legacy corrosive damage
   (half of the smallest resistance). Two shares p and q give 1 − (1 − p)(1 − q), as legacy applies modifications in turn.
   The correction replay uses the copy's share. `ShipState.ResistanceShare` carries it to the view: the combat HUD shows
   heat, energy and kinetic resistance with the share, as legacy `Stats.Resistance` does.

Devices:

1. **InfinityStone** (`TimeMachine`). Cooldown starts at the use; `NoContacts` for `Lifetime`. A use restores energy, armor
   and shield to their maximum without a recharge delay. It starts while the bit is held and the device is ready, then
   `NoContacts` runs from that tick. Lethal damage starts it when ready; then `NoContacts` runs from the next tick, as legacy
   turns the collider off at the next frame, and the cooldown counts from the damage tick. Lethal damage during `NoContacts`
   restores again without a new cooldown.
2. **WarpDrive** (`TeleporterV2`). Energy cost at the start; cooldown at the end. The start sets the velocity to heading ×
   `Power` and the spin to 0. While it runs: `NoContacts`, `EngineOff`, `Faded`, and cannons blocked (legacy: weapons other
   than `Continuous` and `RequiredCharging`; energy shields join when they run). It ends after `Range / Power`, or at a released
   bit once more than 0.1 s has passed; the velocity drops to 0. A copy ends at the full duration when the end record is late.
   The `Chase` node holds the first warp drive at full throttle (legacy `ChaseNode.UseWarpDrive`). Gap: the centre distance
   minus the selected weapons' minimum range. A warp starts within 5° of the course and a gap of 5 up to the warp range; a
   running warp is held while the gap is above 0 and within the range.
3. **Detonator** (`Detonator`, step 7b). Energy cost at the start; cooldown at the end. The lock runs for `Lifetime`, with or
   without the key and during a retreat: `Drift`, every other device blocked, and `Tint` from 0 to 1 (the view moves the
   ship to the device colour). In the first tick after the lock the owner spawns the explosion at the ship centre and queues
   the ship's death for stage 11 (owner rule, phase 5b step 7c): nothing saves it. Legacy took all armor
   (`CollisionEffect.Destroy`), so a ready InfinityStone or a repair in the same tick saved the ship. A ship that dies
   during the lock does not explode. The explosion (`DetonatorDefinition.CreateExplosion`) is an `AllTargets` area for
   0.3 s, radius `Range` × √model scale; heat `Power` × cells × damage multiplier and a push radius × √mass, both with
   falloff; allies are not hit. The lock restarts at each use (approved change: legacy never resets its timer). The copy
   runs the lock from the start record and ends it at the full time; the explosion comes from the owner's spawn record
   (item 7), the death from the owner's roster record. The `DetonateShip` node holds the first detonator; with `InRange`
   only when the ship and its target, each moved on by its velocity for the lock time, touch (centre distance up to the sum
   of half their model scales).
4. **Clonning center** (`ClonningCenter`, step 7c). Energy cost at the start; the use lasts while its clone lives, so the
   cooldown starts at the clone's loss. Deployment starts the timer 80% elapsed (first clone after 6, 9, 12 s for cooldowns
   of 30, 45, 60 s). While no clone lives and the timer has passed, a held bit launches one: at the mothership position plus
   its model scale in a counter-based direction of whole degrees (`RandomPurpose.CloneSpawn`), facing that direction, with
   the mothership velocity. The clone is a drone (legacy `UnitClass.Drone`): it stays when the mothership dies or retreats
   and does not keep its side alive. While it lives the center is not ready, its cooldown is 1, and it counts one active
   drone; without it, one drone in the hangar (the `SpawnDrones` and `HasDrones` AI rules). The mapper builds the clone from
   legacy `ShipSpecDecorator(0.9, 0.9, 0.9)`: armor, shield and shield recharge × 0.9, and no bays and no clonning
   centers. It also scales the weapon damage multiplier and the ramming damage multiplier by 0.9, which legacy combat never
   reads from the decorator (approved change). Devices keep the ship's damage multiplier.
5. **Worm tail** (`WormTailV2`, step 7d). A passive device; `Battle.Tails` runs its chain of `TailSegment` entities
   (`EntityKind.Segment`). Segment 1, the head of the tail, hangs on the ship; each next one on the segment before it.
   - *Growth* (stage 2, owner only). The head grows at the deployment tick, then each segment grows the next one after
     0.05 s (3 ticks), up to `Size` (legacy `RoundToInt`). A segment that replaces a lost one, and each segment after it,
     waits 0.5 s instead (legacy factory counter). Scale × 0.95 per segment from the ship's model scale; mass ship / `Size`
     for the head, then × 0.95; a circle of the prefab radius. The segment appears where its hinge holds it, straight
     behind the parent at the parent's angle, without velocity. A growth waits while the place overlaps a body the segment
     would touch (P4 rule): an `OverlapCircle` with the segment filter, bullets excluded.
   - *Hinges.* Anchor (−offset × parent scale, 0) on the parent, with the device `Offset.x` plus 0.15 at the ship, and
     (`Offset.y` × segment scale, 0) on the segment; reference angle 0; limits ±75° at the ship, ±40° between segments.
   - *Collision.* The missile class of its allegiance (legacy `UnitClass.Limb` on the missile layer): hostile ships,
     drones, missiles and bolts touch it; its own ship, its own segments and allies never do. Beams and point defense
     follow from the filter; point defense ignores segments (legacy targets bolts, missiles and drones only).
   - *Motion* (stage 4, both sides). An attached segment loses 5 × its sideways speed per second. A segment at a squared
     distance of 5 × its scale or more from its parent, or turned 90° or more from it, goes back where the hinge holds it,
     with the parent's velocity and no spin, and skips the damping that tick.
   - *Damage* (stage 9 queues, stage 10 applies, owner only, before the ship pass). A segment takes the total of every
     damage type, without resistances, and a tenth of the push. Segments 1 and 2 never break while attached: a hit goes to
     the tip of the tail first, and the damage the whole tail cannot take passes to the ship as impact damage. From segment
     3 on, recent damage above the hit points tears the segment off (recent damage falls by 5 × the hit points per second);
     otherwise the hit goes to the tip first, and a segment that cannot take the rest dies and passes it on. A segment that
     absorbs a hit restarts its 5 s regrowth wait. Hit points: `Power` × the ship's armor.
   - *Loss.* The segments behind a lost one detach at once: they keep their hinges to each other, take hits on their own
     hit points and destroy themselves after 5 s. The parent regrows the lost segment `Cooldown` later.
   - *Status.* Stage 3 gives the ship the resistance share of the attached segments over `Size` (item 9).
   - *Ship leaves.* A ship that dies takes segment 1 with it; the segments behind it detach and destroy themselves after
     5 s, as after a tear (legacy: the head finds its ship destroyed first). A ship that retreats or vanishes takes every
     attached segment (legacy `Vanish` detaches nothing). Detached segments run out their own timers; on a copy they leave
     by the owner's removal records.
   - *Ramming.* A hostile ship rams a segment with the ship rule (Contacts and damage, item 4); a segment rams nothing.
   - *Turn cap* (P4). A ship with a tail turns at most 942°/s with 2,447°/s² (the fastest database worm, where the hinge gate
     passed); `ShipDefinition` caps both engines.
   - *Network (H8).* Identity `CreateChild(ship, tick, device, segment number, 0)`. The owner sends a `BulletSpawn` record
     per grown segment (barrel = segment number) and a removal record per segment that leaves, also with its ship; the copy
     builds each segment behind its own chain and never grows, damages or times out a segment itself. A contact on a segment
     travels as a contact on a bullet (`TargetIsBullet`, network plan 9d): it counts once, whether the attacker's or the
     owner's device saw it; a projectile once per segment, a ray, damage over time and a ram (device −1) once per tick. A
     remote projectile that stops at a local segment stops on its owner too. No codec change.
6. **Stealth** (`Stealth`, `SuperStealth`, step 9b). A held bit starts it when the cooldown has passed and the energy cost
   is available; it spends the cost once and stays on after the release. While on: the `Stealth` status, so
   `ShipEntity.TargetPriority` is `None`, also over a jammer's `High`. The AI enemy search, the barrel search and the
   missile search skip the ship; the AI still sees it within 5 units plus its scale. It survives a retreat.
   - *End.* Another device of the ship becomes active at stage 2 (`ShipDevice.IsActive`), except point defense and drone
     bays (`KeepsStealth`; later Ghost, Fortification, energy shields and repair bots). For `Stealth` only, the ship takes
     a contact: every hit candidate of a local attacker at stage 9, with or without damage; a remote attacker's record at
     stage 10; a body touch with a ship or a tail segment of any allegiance, in each tick it lasts. A contact ends stealth
     at stage 2 of the next tick, so the status of the contact tick still has it (legacy `OnHit` after the systems).
   - *Cooldown.* Each end rule restarts the cooldown, also while stealth is off (legacy `Deactivate`; owner question 5).
     Legacy device order: an active device before the stealth device restarts it before stealth updates, so a press in
     that tick starts nothing; one after it counts from the next tick, after stealth may have started and paid.
   - *Network (H8).* The owner sends the start and the end (`DeviceUse`). A copy never starts or ends stealth itself; its
     own device activity restarts only its cooldown. On the owner, a remote attacker's contact counts only by its record.
   - *View.* 40% opacity for the viewer's side (`BattleRenderer.ViewerSide`), 5% for the others; the opacity moves by 1 per
     second, as legacy, and multiplies the warp's 10%.
7. **Accelerator** (`Accelerator`, step 9c). It runs while the bit is held and the cooldown has passed, and each tick pays
   `EnergyPerSecond` × 0.02. The mapper computes it as max(1, `EnergyConsumption` × the ship's `EngineEnergyConsumption`).
   There is no cost at the start. While on: `FullThrottle`, and `EngineScale` × `Power`; two accelerators multiply. The
   engine caps the scaled `MaxVelocity` at `EngineDefinition.VelocityLimit` (legacy `ShipSettings.MaxVelocity`, 40);
   propulsion has no cap. A released bit, a failed payment or a retreat ends it; the cooldown counts from the end.
   `Active` is the held bit with the cooldown passed, also when the payment fails (legacy). An active accelerator ends
   stealth.
   - *Network (H8).* The owner sends the start and the end (`DeviceUse`). The copy follows them. It pays the energy of its
     held bit as the owner does, so its energy stays close until the correction. A failed payment on the copy ends nothing.
8. **Brake** (`Brake`, step 9c). A passive device: it ignores its bit and is never active. Its status adds `Power` /
   √(ship weight) to `Deceleration`. The engine applies the sum against the velocity while the effective throttle is 0.001
   or less, after the thrust. It stops the ship at zero speed and never reverses it (owner question 6). `EngineOff` (warp)
   and `FullThrottle` stop the brake. Without an engine (retreat, `Drift`, no energy without an energy-free engine) there is
   no brake. A copy reads the throttle of its control frames, so it needs no record.
9. **Ghost** (`Ghost`, step 9d). A held bit starts it when the cooldown has passed and `EnergyCost` is available; the start
   spends the cost. Each later tick pays `EnergyCost` × 0.02, for at most `Lifetime` after the start tick (legacy pays while
   the time left is above 0: 3,000 ticks after the start tick for 60 s). While on: `NoContacts`, and every other device
   except the stealth devices is blocked (legacy `CanActivateSystem`). A released bit, the end of the lifetime, a failed
   payment or a retreat ends it; the cooldown counts from the end. `Active` is the held bit while on. A ghost keeps
   stealth on, and stealth can start during a ghost.
   - *Network (H8).* The owner sends the start and the end (`DeviceUse`). The copy follows them and pays the energy per
     second of its held bit, but not the start cost and nothing in the start tick. A failed payment on the copy ends nothing.
   - *View.* The colour moves to the device colour (`#40FFFFFF` for the built-in record: 25% opacity, multiplied by the
     stealth opacity).
10. **Fortification** (`Fortification`, step 9d). It runs while the bit is held and the cooldown has passed, and each tick
    pays `EnergyPerSecond` × 0.02, the start tick too. A start also needs `EnergyPerSecond` in store (legacy
    `CanBeActivated`). While on: `ResistanceShare` `Share` (item 9 of the parts), so energy, heat and kinetic resistance r
    becomes `Share` + r (1 − `Share`), as legacy. Corrosive moves toward 0.5, as legacy derives it from the fortified
    values. The mapper gives `Share` = `Power` / 100, 0.5 when `Power` is 0, clamped to [0, 1]. A released bit, a failed
    payment or a retreat ends it; the cooldown counts from the end. `Active` is the held bit with `CanBeActivated`, also when
    the payment fails. A fortification keeps stealth on.
    - *Network (H8).* As the accelerator (device 7).
    - *AI.* The `Ram` node with `UseSystems` (legacy `RamNode`) holds the first fortification from a second before the hit,
      and the first accelerator at full throttle while the course is within 15°, the target is 10 u or more away, and the
      accelerator is active or the energy is at least 50%.
