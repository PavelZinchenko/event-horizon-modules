# Core weapons and bullets

Part of [Combat simulator](index.md).

## Bullets and cannon

Phase 2 steps 6–7. Devices are explicit code per device (`ShipDevice`); the data form waits for the data-model gate (A4).

1. **Definitions.** `ShipDefinition.Devices` lists at most 64 `DeviceDefinition`s; the list index is the device index and its control bit. `ShipDefinition.Barrels` lists `BarrelDefinition`s: the muzzle point and angle in the ship frame (the legacy platform offset included). `CannonDefinition` holds barrel index, cooldown (`1 / FireRate`), energy cost, spread (full angle, radians) and a `BulletDefinition` (radius, speed, range, damage and damage type, extra damage of other types, weight, `ParentVelocityEffect`, hit points, homing, `StartingVelocityMultiplier`, recoil, push). Deployment creates or resets cached devices for the definition. A missile launcher is a cannon with homing ammunition: legacy launchers are ordinary weapons, and the ammunition decides the behaviour.
2. **Whole ticks.** `TickTime` converts legacy seconds once, at setup, with exact arithmetic. A legacy `>=` check takes ceil(seconds × 50) ticks; a legacy `>` check takes floor(seconds × 50) + 1. A value within 0.001 tick of a boundary counts as on it. Legacy accumulates float time per step, so at an exact multiple it can differ by one tick.
3. **Cannon (Q1, legacy `CommonCannon` on a `FixedPlatform`).** At stage 2, after the resources recharge, each device of a ship that is not retreating runs in index order (legacy `ShipRetreatEffect` blocks every system). A cannon fires when its bit is held, its cooldown is over (`CooldownTicks`, a `>=` check; it starts ready), its barrel is ready, and `Energy.TryConsume(cost)` succeeds. A failed check spends nothing.
4. **Barrels.** Weapons on one barrel share a 0.04 s cooldown (legacy weapon platform, a `>` check): 3 ticks. A deployment counts as a barrel shot in the tick before, so the first shot comes 2 ticks after the deploy tick, as in legacy.
5. **Spawn (stage 5).** A device writes a `SpawnRequest` at stage 2; stage 5 creates the bullet. The ID is `CreateChild(ship, tick, device index, barrel index, sequence)`. The angle is the ship angle + the barrel angle + (r − 0.5) × spread, with r from `CounterRandom` (`WeaponSpread`, the bullet ID). The velocity is speed × `StartingVelocityMultiplier` along the angle + the ship velocity × `ParentVelocityEffect`; the ship velocity is the one after stage 4.
6. **Recoil.** An impulse of −`Recoil` × the bullet velocity relative to the ship (legacy `BulletFactory.ApplyRecoil`), at the muzzle point; an off-centre barrel also spins the ship. `Recoil` comes from `Body.Recoil` only, never from the weight (phase 4 E1, owner 2026-10-31); the mapper sets it to 0 for a bullet attached to its parent. `PreStepVelocity` keeps the stage 4 value.
7. **Store.** `BulletStore` is a struct array with a LIFO free list and generations. The free list grows with the array, so a tick that frees many bullets late in a battle allocates nothing; `EntityRegistry` maps bullet IDs too. A physics bullet maps back to its entry by physics index and `BulletId`. Physics bullets are pass-through, so physics reports every target; the core keeps the first valid hit (contact ordering). The bullet's `Owner` body is its ship, so it never hits that ship.
8. **Filters.** `CollisionFilters` gives one category bit per allegiance group and class, following the legacy layer matrix (phase 4, D13: four groups, Player and Ally share one). Ship hulls are `Ship(allegiance)` and ignore nothing. An energy bolt is `Energy(allegiance)` and ignores friendly ships and drones and all energy bolts. It hits hostile ships, drones and missiles. A missile is `Missile(allegiance)` and ignores friendly ships, drones, bolts and missiles. It meets hostile ships, drones, missiles and bolts. A test checks every pair against the legacy matrix.
9. **Hits.** At stage 8 each physics hit on a live ship becomes a candidate at its sweep fraction (`BulletSweep`). Q1: a cannon bullet stops at its first valid hit, so stage 9 marks it for removal with reason `Death`. The damage then follows the ship pipeline: shield first, then armor. A ship takes each damage effect (`Damage` and `ExtraDamage`) with its own type; a missile and the hit event take the total. A remote ship gets one hit record per damage effect. A hit on a bullet becomes a candidate only when the target is a missile, with the missile's hit points as `TargetHitPoints` (see Missiles and target grid).
10. **Lifetime.** Range / speed, with a `>` check: `LifetimeTicks`. A bullet spawned at tick s flies in the physics of ticks s to s + `LifetimeTicks` − 1. Stage 11 of the last tick marks it `Expiry`.
11. **Removal (stage 12).** Bullets leave after the ship changes, in entity ID order, so the events do not depend on storage. A bullet outlives the ship that fired it; physics ignores its stale owner body.
12. **Push (phase 4 E4).** A hit on a ship or drone adds `Push` × the bullet velocity as an impulse at the target's origin (legacy `PushAction`: no spin; negative pulls). Missiles are never pushed: legacy `MissileDamageHandler` drops impulses. On a remote target the attacker's copy moves at once as a prediction; the first hit record carries the impulse, and the target owner applies it when the record arrives.

## Missiles and target grid

Phase 2 step 7 (Q2a, Q2b). The rules port legacy `HomingController` on new ammunition, with `W = 1 / (0.2 + 2 × weight)`.

1. **Missile.** A bullet with hit points above 0 is a missile (legacy `UnitClass.Missile`). It stops at its first valid hit on a ship or a missile (legacy `DetonateAtTargetAction`): impact damage only. Area explosions come with triggers in phase 5 (owner, 2026-10-20).
2. **Bullet contacts.** A bolt that hits a missile deals its damage and stops. Two missiles that meet both detonate, whatever their hit points: the candidate of each one stops it. A missile that meets a bolt deals nothing and does not stop (legacy `BulletDamageHandler`); only the hit of the bolt counts.
3. **Lifetime.** Range / speed, as for every bullet (Q2a; the legacy 1.2 factor is not kept).
4. **Target grid (R9).** At the start of stage 4, `TargetGrid` takes every active ship: entity ID, allegiance, position, hull radius, target priority and missile camouflage chance, at the poses at the start of the step. It is a uniform torus grid with 32 u cells, rebuilt once per tick. It allocates nothing once its arrays fit the battle. Drones join it on the tick after launch: stage 4 precedes the stage 5 spawn.
5. **Target rule.** Range is the centre distance at the nearest torus image minus both radii. The search covers 1.3 × the range of the missile. An enemy with priority `None` (stealth) is never a target. Inside it, the enemy within 30° of the heading with the highest priority wins, then the nearest one. Without one, the enemy with the smallest deviation wins, whatever its priority (legacy). Equal keys go to the lower `EntityId`, so the result does not depend on grid order. Beyond the radius there is no target (owner, 2026-10-20). Legacy chases an enemy at any distance, and its choice depends on list order. Missile camouflage (`ShipDefinition.ChanceToAvoidMissile`, phase 5, step 9a): each search skips the candidate with that chance. The roll is counter-based (match seed, bullet, tick, candidate; purpose `MissileDodge`), so both cores agree; legacy rolls an unseeded `System.Random` (D20). A drone has priority `Low`, so a missile prefers a ship in its cone; a jammer gives `High`, and so does a decoy ([Decoys](core_devices.md#decoys)), which therefore wins inside the cone.
6. **Search timing.** Legacy runs no search at creation, so the first search is in the tick after the launch. The next one comes 101 ticks later (`TickTime.After(2 s)`). A removed target starts a search at once, at stage 4 of the next tick (redesign, owner, 2026-10-20). Every search restarts the period.
7. **Steering (stage 4, `HomingSteering`).** Below full speed along the heading, the velocity changes by `normalize(speed × heading − v) × 0.5 × speed × W × dt`. This also removes sideways velocity. The turn rate is `120 × W` deg/s toward the target, and 0 inside ±5° or without a target. The rate is set, not accelerated. Physics never turns a bullet, so the core sets the heading after the turn before the physics step. The legacy body has the same heading after its step. Smart aim is deferred (phase 5, H4).
8. **Magnetic homing (phase 5, step 4c).** `IgnoreRotation` maps to `BulletDefinition.IsMagnetic` (legacy `MagneticController`). It is a homing bullet, so the search, the target records and the corrections of the homing rules apply. With a target, each tick the velocity changes by `normalize(speed × normalize(L) − v) × speed × W × dt`. `L` is the target delta plus the target velocity × |delta| / speed. The thrust has full size every tick, so the speed jitters around `speed`. Without a target there is no thrust. A bullet with a direction (`HasDirection`, legacy `BulletShape.HasDirection`) searches in the 30° cone and turns its heading by `10 × angle to its velocity × dt`, with or without a target. A `Spark` or `Mine` searches all around (the nearest enemy wins) and never turns. Legacy measures the delta without the torus wrap; the core uses the nearest image. A correction re-runs the magnetic steering (`HomingSteering.Advance`).

## Bullet triggers

Phase 5, step 2. `BulletDefinition.Triggers` lists the gameplay triggers in legacy order (`BulletTriggerDefinition`): `Spawn` creates child bullets, `Detonate` makes the bullet detonate. Sound and visual triggers stay in the view (owner, 2026-10-29; [Views](unity_integration.md#views), item 9). A trigger has one or more `BulletTriggerConditions`.

1. **Who runs them (H8).** Only the owner of a bullet runs its triggers. A receiver never derives a child: it adds each child from the owner's spawn record. A remote copy that this device removed by prediction (N2) therefore gets no children of its own.
2. **Conditions (legacy `Bullet`).** `Created` runs when the bullet is made. Every other condition runs at stage 12, before the bullet's removal:
   - `Hit` runs once per contact of the tick: the bullet's own stopping hit, a point-defense zap, or a hit that damages it (a missile). A contact that a remote device claimed counts once.
   - A `Death` removal (a stopping hit, or a missile shot down) runs `Destroyed`, then `Detonated` if `DetonateWhenDestroyed` (legacy default true).
   - An `Expiry` runs `Expired`, then `Destroyed`. A `Detonate` trigger among the `Expired` ones adds `Detonated`.
   - A `Disposal` (point defense) runs `Disarmed`.
   - A `Detonate` trigger on `Hit` stops a bullet that survives the contact (`Death`). On other conditions legacy ignores its result.
3. **Children.** Child i of a `Spawn` trigger turns from the parent's heading by `Rotation + RotationSpan × r` and starts at its pattern offset in that frame (`BulletSpawnPattern`). Its velocity is speed × `StartingVelocityMultiplier` along its heading + the parent's velocity × the child's `ParentVelocityEffect`. The trigger's `RandomFactor` varies speed and lifetime by (r − 0.5) × factor each. All draws come from `CounterRandom` (`ChildSpawn`, the child's ID) in a fixed order: rotation, offset X, offset Y, speed, lifetime.
4. **Identity and timing.** The ID is `CreateChild(parent bullet, spawn tick, trigger index, 0, sequence)`. A `Created` child of a stage 5 shot spawns at that tick and moves in it. A stage 12 child has the next tick as its spawn tick: it is created at stage 12, appears in that snapshot, and moves from the next tick, as a shot does. Homing steering starts after the first move. A child's own `Created` triggers run at once, so nesting stays in one tick; the mapper bounds the depth.
5. **Inherited fields.** A child takes owner, side, allegiance, the ship that fired its first ancestor (`ShipId`, so allegiance changes reach it) and that ship's device index (hits name it). `ParentId` is the parent bullet.
6. **Records.** The owner captures each child's spawn record before the parent's removal record, so the stream still names the parent. The record carries the launch pose. The receiver takes the parent's definition and velocity from its live copy, or from a store of remote bullets that can spawn children. An entry lives from its spawn record until the tick after its owner removal record applies. A late batch applies the removal (tick T) before the children (tick T + 1). The receiver moves a late child on a line from its spawn tick, as a late shot. The inherited velocity comes from the receiver's copy of the parent, as the shot velocity comes from its copy of the ship (P1).
7. **Mapper.** `CoreWeaponMapper` maps legacy `BulletTriggerBuilder`: triggers in order, then the `OutOfAmmo` spawn triggers with the conditions of the spawn triggers that reached `MaxNestingLevel` (default 100). A child takes the legacy `CreateFactory` modifiers (power and size multipliers, trigger colour); its speed and range also scale by the area multiplier. Each child definition is built once per weapon. The rotation and offset expressions are sampled per child index: a `RANDOM` call becomes a span. Inert reasons: a child the core cannot run gives its own reason; `SpawnBullet on Cooldown`, `SpawnBullet on Hit with cooldown` (a missile), `spawn expression with RANDOM` (several calls, `RANDOM_INT`, not linear), `too many child bullets` (over 1000 per weapon), `beam with trigger …`. A bullet with a gameplay trigger needs no damage effect (a carrier).
8. **Behaviour change (owner, 2026-11-02).** Legacy builds an `OutOfAmmo` `Detonate` with the `OutOfAmmo` condition itself, which runs once at creation, so it does nothing. The core gives it the same conditions as the `OutOfAmmo` spawn triggers. `ClusterMissile` (record 112) now detonates when it expires at its last nesting level. The old simulator keeps the legacy result.

9. **Shot beam contacts (phase 5, step 5f).** A Common shot beam can carry Hit spawn triggers.
   Stage 12 spawns children at the ray endpoint in the beam's direction. Each trigger has a timer per beam;
   GreenLightning's one-second timer permits one cloud during its 0.1-second shot. The existing child record names
   the beam as parent. Receivers retain expired beam parents until their owner removal records permit pruning.
   `BeamDefinition.Impact = AllTargets` applies damage and effects once per target identity per shot.
   The ray still stops at the first body, including one already hit. A changed first body takes its own hit;
   HeavyLightning's half-second trigger timer prevents another set of sparks during that shot.

## Area and over-time impact

Phase 5, step 3. `BulletDefinition.Impact` copies legacy `BulletImpactType`. A bullet that is neither a missile nor a `FirstTarget` bolt is a legacy area of effect (`IsArea`): it has the energy filter, point defense ignores it (legacy `PointDefenseCollisionAction`), and nothing damages it.

1. **`AllTargets`** (legacy `HitAllTargets`, `OnTriggerEnter`): a target unit counts when it enters the bullet (physics `Begin`), so a target that stays inside is hit once and a target that leaves and comes back is hit again. The bullet never stops at a hit. A missile with `AllTargets` flies through its targets (legacy builds no `DetonateAtTargetAction` for it).
2. **`OverTime`** (legacy `DamageOverTime`, `OnTriggerStay`): every step the bullet touches a target, the target takes `Damage × 0.02` once per bullet and target entity (D16). The definition allows one damage type and no push or falloff, because a remote ship takes it as one interval at a fixed rate (network plan, decision 2); the record names shape 0, so the interval stays one contact while the bullet crosses the hull parts. A remote bullet target gets one record per tick, as a ray.
3. **Falloff (E6).** A `DamageAmount` with `Falloff` (legacy `DamageWithFalloff`) and `RadialPushWithFalloff` scale by `clamp01((R + target scale − d) / R)`: R is half the bullet `Scale`, d the torus distance between the centres after the physics step, and the target scale is `ShipDefinition.Scale` (legacy `ModelScale`) or the target bullet's `Scale`. Stage 8 stores the factor in the candidate; the candidate damage is the total after the falloff.
4. **Radial push** (legacy `RadialPushAction`: `PushFromCenter`, `PullToCenter`, `PushFromCenterWithFalloff`): an impulse along the line from the bullet centre to the target's origin, negative toward the centre, added to `Push`. Legacy applies it at the bullet centre; that point lies on the same line, so applying it at the origin gives the same spin. Missiles are never pushed. A remote target gets it in the first hit record (phase 4 E4).
5. **Lifetime.** A positive `lifetime` (legacy `Body.Lifetime`) replaces range / speed; speed and range may then be 0. A child's random factor varies it as before.
6. **Attached bullets** (`AttachedToParent`): the bullet rides on its ship at the barrel pose (physics `AttachBullet`), without spread, recoil or velocity of its own. Each device attaches its copy to its own ship copy; a fire confirmation does not re-aim it. When the ship leaves (death, retreat, vanish), stage 12 removes the bullet with `Disposal` and runs none of its triggers (owner, 2026-11-02; behaviour changes). A child cannot be attached: physics attaches bullets to bodies only.
7. **Mapper.** `CoreWeaponMapper` maps `HitAllTargets` and `DamageOverTime`, `DamageWithFalloff`, the three radial push effects, `Body.Lifetime` and `AttachedToParent`. A bullet without a prefab is the legacy area bullet: a circle of radius 0.5 × size. An area with effects scales its size by `AoeRadiusMultiplier` (legacy `BulletStats.BodySize`); `Scale` is that size. Inert reasons: `push over time`, `falloff over time`, `several damage types over time`, `bullet attached to a bullet`, `homing bullet attached to the parent`, `beam with falloff`. No built-in ammunition uses them.
8. **Views.** A bullet without a prefab has no look of its own in legacy (its trigger effects draw it), so `CoreBulletVisuals` draws the legacy flash glow at the bullet's `Scale`, in its colour at half alpha, until H11 decides the area views.

## Drains and siphon

Phase 5, step 5a. Examples: `TODO/battle_simulator_phase5.md`, "Drains and siphon (5a)"; tests: `Core/Tests/DrainTests.cs`.

1. **Model.** `BulletDefinition.Effects` and `BeamDefinition.Effects` hold one `ImpactEffects`: `EnergyDrain`, `ShieldDrain` and the
   `Siphon` factor. A hit applies each drain once at full power, without falloff (legacy actions); an `OverTime` bullet and a
   beam apply it per second, × 0.02 per tick. A charge scales the drains, not the siphon factor. Only ship and drone targets take
   them; a missile or a tail segment takes the damage only.
2. **Stage 9 and 10.** Stage 9 adds the drains to the target's `PendingEffects` beside its pending damage. Stage 10 (legacy
   `Impact.ApplyShield`, `ShipStats.ApplyDamage`) takes the shield drain from the shield first; the shield left absorbs damage,
   so a drain at least as large as the shield lets all damage reach the armor. The shield loses drain plus absorbed. The armor
   takes what passed less the repair. Energy gains its share first, then loses the drain, clamped at 0; the drain restarts its
   recharge delay. Several hits in one tick sum, so the order holds for the sum.
3. **Siphon** (legacy `SiphonHitPointsAction`). The siphon heals from the first damage effect: the attacker's ship (`ShipId`, also
   for a child bullet) regains that damage × (1 − the target's armor resistance of its type, with its resistance share) ×
   `Siphon`, as a repair at stage 10, capped at its maximum. The target's shield does not reduce it (finding 7a, kept). Only the
   attacker's owner heals its own ship; a copy follows corrections.
4. **Network.** A drain on a remote target is its own hit record beside the damage records of the contact (`HitEffect`), with
   the drain as amount; over time, its own interval with the rate as amount, because the interval key includes the effect. The
   target owner applies it like damage; the attacker's copy takes it at once as a prediction, and the correction replay
   includes it. A drain makes no `Hit` event on the receiver.
5. **Mapper.** `DrainEnergy` (× `EffectPowerMultiplier`), `DrainShield` (× `DamageMultiplier`) and `SiphonHitPoints` (a damage
   effect × `DamageMultiplier`, with its `Factor`). A bullet or beam with only drains maps with damage 0. Inert reason: `siphon
   with another damage effect` (the core heals from one damage effect; no built-in ammunition has it).

## Slow-down and roll

Phase 5, step 5b. Examples: `TODO/battle_simulator_phase5.md`, "Slow-down and roll (5b)".
Tests extend `Core/Tests/ImpactEffectTests.cs`, `DrainTests.cs`, and `Runtime/Tests/SessionCodecTests.cs`.

1. **Mapping and weight.** `ImpactEffects.SlowDown` and `Roll` use `EffectPowerMultiplier` and scale with charge.
   Neither uses damage falloff. Effects without damage map with damage 0. `ShipDefinition.Mass` comes from the same
   `stats.Weight` as legacy bodies, for ships and drones. Its 0.01 minimum is below the built-in weight floor of 0.05.
2. **Slow-down.** Stage 9 scales velocity and angular velocity by `1 - min(1, power / sqrt(mass))`, before push.
   Over-time bullets and beams use `power * 0.02` each tick. Separate contacts multiply separately.
   A web of power 4 on mass 16 applies 0.98 each tick: about 36.4% remains after 50 ticks without engine input.
3. **Roll.** On contact entry, add `power / sqrt(mass) * 180` degrees/s in the current spin direction.
   Zero spin starts counter-clockwise. Physics receives radians through `SetAngularVelocity`.
   Two reused sets of attacker/target pairs prevent repeated kicks during sustained contact. Leaving and returning
   starts a new contact. Both motion effects ignore missiles and tail segments (the approved minimal Roll port).
4. **Network.** Version 11's `HitEffect` field holds `SlowDown` and `Roll`.
   A slow-down record precedes the damage record carrying push. Its amount is power, or power per second for an interval.
   Roll is a discrete record carrying a positive degrees/s change; the receiver chooses its own spin direction.
   The target owner applies both on arrival. Missed slow-down ticks compound the per-tick multiplier against current
   velocity; they do not replay intervening engine inputs. Motion effects make no remote damage event and stay outside
   resource correction replay. Existing velocity corrections handle motion; they do not reconstruct contact history.
5. **Unsupported combinations.** The mapper reports `several slow-down effects` or `slow-down after roll` instead of
   merging non-equivalent action sequences into one power. No built-in ammunition uses either combination.

## Progressive damage

Phase 5, step 5c. Examples: `TODO/battle_simulator_phase5.md`, "Progressive damage (5c)".
Tests: `Core/Tests/ProgressiveDamageTests.cs` and `Runtime/Tests/SessionCodecTests.cs`.

1. **Rule.** Legacy `ProgressiveDamageAction` on a beam: `ImpactEffects.Ramp` (power × `DamageMultiplier`, × charge)
   and `RampTime` (the effect's `Factor`, unchanged by charge). Contact tick k deals
   `Ramp × min(1, k × 0.02 / RampTime) × 0.02` more damage of the beam's type; tick 0 deals none (legacy `IsNew`).
2. **Contact.** The contact is the first body on the ray. `BulletEntity.RampTargetId` and `RampTick` hold it. A tick
   without an accepted contact, another first body or a new beam starts again at k = 0. Another hull part of the same
   ship keeps the contact.
3. **Any target.** Stage 6 adds the ramp to the ray candidate's damage. A ship, a drone, a missile and a tail segment
   take it, as legacy adds it to the target's impact. The resolver walk counts it, so the ramp can stop a missile.
4. **Network.** A remote ship target gets a `ProgressiveDamage` interval beside the damage interval: the full ramp per
   second as amount, the beam's damage type, shape 0 and `ResolvedHit.RampTime`. The target owner counts k from the
   interval's first local tick, so a late start record catches up the ramp, not a flat rate. The correction replay uses
   the same closed form (`ImpactEffects.RampDamage`). `SessionCodec` version 11 writes the ramp time (32-bit float) after
   the effect of this record only. A bullet or segment target gets the ramp inside its contact's damage, as before.
5. **Mapper.** A non-over-time impact ignores the effect, as legacy. Inert: `progressive damage of another type` (a
   beam whose ramp type differs from its damage type), `several progressive damage effects`, `progressive damage on a
   bullet` (legacy shares one ramp among every target of an over-time bullet), and a siphon beside a ramp (`siphon with
   another damage effect`). No built-in ammunition has any of them. A beam with the ramp only deals no fixed damage.

## Restore lifetime

Phase 5, step 5d. Examples: `TODO/battle_simulator_phase5.md`, "Restore lifetime (5d)".
Tests: `Core/Tests/RestoreLifetimeTests.cs`.

1. **Spawn.** Legacy `Lifetime.Take(Max × Factor)`: `ImpactEffects.LifetimeLoss` (the effect's `Factor`) is spent at
   creation. `BulletDefinition.SpawnLifetimeTicks` flies `After(lifetime × (1 − LifetimeLoss))` ticks, after the random
   factor. `BulletEntity.LifetimeCapTicks` keeps `After(lifetime)`, the full lifetime.
2. **Restore.** Legacy `RestoreLifeAction`: an accepted contact with a ship or a drone moves `ExpiryTick` back by
   `ImpactEffects.Restore` ticks per tick over time, or `Restore` seconds once per hit. The expiry stops at
   `tick + LifetimeCapTicks − 1`, a full lifetime from the contact tick. A missile or a tail segment restores nothing.
3. **Fractions.** A restore that is not a whole number of ticks keeps its fraction in `BulletEntity.RestoreCarry` for
   the next contact. The cap clears it. No built-in power has a fraction.
4. **Owner only.** Stage 9 restores on the attacker's owner, before stage 11 checks the expiry. A copy has
   `BulletDefinition.HasOwnerLifetime`, as an aura: it never expires by itself and leaves with the owner's removal
   record. A late spawn record of such a bullet, or of such a child, is never skipped as expired. No record is new.
5. **Charge.** Legacy reads the raw power, so a charge scales neither `Restore` nor `LifetimeLoss`.
6. **Mapper.** Inert, mods only: `several restore lifetime effects`, `restore lifetime without power` (legacy
   `Lifetime.Restore` gives back the full lifetime at power 0), `beam with restore lifetime`, `aura with restore
   lifetime` (the aura's size follows its renewed lifetime). The core definitions reject the last two.

## Friendly fire and repair

Phase 5, step 5e. Examples: `TODO/battle_simulator_phase5.md`, "Friendly fire and repair (5e)".

1. **Contacts.** `BulletDefinition.FriendlyFire` and `BeamDefinition.FriendlyFire` select a neutral collision filter.
   `CoreWeaponMapper` sets it from the ammunition's `Body.FriendlyFire` or the weapon's
   `WeaponStatModifier.ForceFriendlyFire` (Frontier's Berserker perk); a child bullet inherits the modifier.
   The bullet keeps its firing ship's allegiance for repair and recharge. Rays and projectile contacts exclude
   the firing ship, its drones, its tail and its bullets. Another ship of the same network owner remains a target.
   Friendly rays grow their reusable hit buffer on overflow, so excluded units cannot hide the next valid target.
2. **Repair.** An allied ship or drone receives `ImpactEffects.Repair`, without resistance. Stage 10 nets it against
   armor damage before the death check and caps armor at its maximum. A hostile target takes `RepairDamage` of
   `RepairDamageType`, shield first. An allied missile blocks the ray but receives no repair. A hostile missile
   or tail segment takes the damage branch; neither receives repair or recharge.
3. **Recharge.** Allied shield and energy gains net against their respective drains. Shield recharge reduces the
   final shield loss, but does not add shield for this tick's damage absorption, as in legacy `Impact.ApplyShield`.
   Enemy recharge effects become drains. Repair and shield powers use the damage multiplier; energy powers use
   the effect-power multiplier and energy-cost multiplier on both branches. A nonpositive `Factor` reads as 1.
   Charge scales every power. A mod with repairs of several damage types is inert; no built-in ammunition has it.
4. **Network.** The core accepts several owners on one side, and allegiance can change during battle. A remote ally
   therefore needs records: `Repair`, `EnergyRecharge` and `ShieldRecharge`, each with its own over-time interval.
   The target owner applies them; the firing owner's copy predicts them and replays unacknowledged gains after
   corrections. The interval key includes damage type, so ordinary damage and repair's hostile damage remain separate.
   Step 5e widened the effect field to four bits in version 11, before any version 11 batch had shipped at that check.
5. **AI.** `FindDamagedAlly` selects the nearest allied roster ship at or below 99% armor, excluding itself and drones.
   This follows the approved example; legacy searched below 75% and retained targets up to 99%. The range is the
   attack range plus `max(5, LimitedRange)`, centered on the live mothership or the searching ship. Defensive drones
   use one quarter of `LimitedRange` before the minimum. The search skips the mothership, as legacy does; another
   node can select it. The existing search/change cooldowns remain. `MainTargetIsAlly` requires a live allied target;
   `MainTargetLowHp` compares its armor fraction strictly below `MinValue`.

## Shield bypass and CircularSaw

Phase 5, step 6a. `ImpactEffects.IgnoreShield` skips shield absorption for that impact's damage only.

1. **Damage.** Each ship accumulates bypass damage separately by damage type. Stage 10 applies ordinary damage to the shield first.
   Bypass damage then joins the remainder before armor resistance, energy absorption, repair and energy drain.
   An ordinary bolt and a saw in one tick therefore keep separate shield behavior. Missile and tail damage stay unchanged.
2. **Shape.** `BulletDefinition.CircleCenter` gives the collision circle's local center at full size.
   The mapper gives CircularSaw radius `0.5 × size` and center `(0.4 × size, 0)`.
   An aura scales both through `PhysicsWorld.SetBulletCircle`, including collision bounds and queries.
   A charged copy scales both too. The sprite uses the same center and aura growth.
3. **Network.** `DamageIgnoringShield` preserves each damage type and any projectile push.
   `ProgressiveDamageIgnoringShield` preserves bypass when a beam also has a progressive ramp.
   Both receiver damage and correction replay keep bypass separate from ordinary damage.
   `SessionCodec` version 12 retains four effect bits; version 11 batches are rejected.
4. **Checks.** `ShieldBypassTests` covers growth, release, mixed hits, resistance, energy absorption, charge, missile damage and network replay.
   Physics tests cover rotation, wrapping and query bounds. Tail, progressive-ramp and codec tests also cover bypass.

## Growing waves

Phase 5, step 6b. `WaveDefinition` describes the legacy arc from `BulletPrefab.Margins` and `Deformation`.

1. **Geometry.** Eight points sample `(cos a − 1, sin a)` for angles between `−A` and `A`.
   `A = π/2 × (1 − Margins)`. The wings trail behind the bullet origin.
   The polygon has a short leading chord; the approved approximation differs by less than 0.14 units for built-in waves.
   `PhysicsWorld.SetBulletPolygonScale` scales a private copy about the origin and refreshes its collision and query bounds.
   It preserves shared geometry and normals, and allocates nothing after the first scale call.
2. **Timing.** Stage 5 scales the arc by `BodySize × (1 + Deformation × clamp01(elapsed / lifetime))`.
   The spawn tick has zero elapsed time. `FlightLifetime` stores exact seconds, including charge and random variation.
   Confirmation and identity adoption update this lifetime. Late records use their original spawn tick.
3. **Contacts.** The existing `AllTargets` path hits each target once per contact entry.
   Growth can reach additional targets. Separation permits another hit on re-entry.
4. **View and network.** Snapshot `WaveScale` drives a local arc line at the projectile pose.
   Ship shots and children derive the same growth on each owner; the wire format stays unchanged.
   `EnergyWave` and `EnergyWaveL` map. Satellite `IronBall` maps since step 12c.
5. **Checks.** `WaveTests` covers dimensions, two targets, re-entry, charge, and late ship and child records.
   Physics tests cover scaled query bounds, rotation, torus wrapping, shared templates and reused bullet slots.

## Teleport impacts

Phase 5, step 6c. `ImpactEffects.Teleport` carries the raw distance; weapon modifiers and charge do not scale it.
`QuantumRiftTorpedo` maps its carrier and the `QuantumRift` child: 50 impact damage and a 100-unit teleport.

1. **Targets.** Ships and drones move along their current heading, with torus wrapping. Their linear velocity clears; spin stays.
   `ShipDefinition.TeleportImmune` excludes the database's `Starbase` and `Special` types. They still take damage, as do missiles.
2. **Contact.** Teleport runs once on entry, including over-time contacts; separation allows another teleport.
   The landing is exact, without a free-space search. `PhysicsWorld.TeleportGroup` moves all attached hinge bodies with their offsets intact.
   Tail segments retain their velocities. Bullets attached to a moved body follow it through the physics attachment path.
3. **Tail hits.** A direct hit destroys a destroyable segment and detaches those behind it, which expire after five seconds.
   Fixed segments 1 and 2 take the hit as ordinary damage, as legacy `FixedWormSegment.Affect` ignores the destruction flag.
   The legacy head-rebuild rule stays (wait `1.5 × Cooldown`, then a segment every 0.5 seconds); no built-in path reaches it.
4. **Network.** `HitEffect.Teleport` carries distance in a discrete hit record. Only the target owner moves the ship; copies follow corrections.
   Predicting the jump would let an older correction return the target to the rift and cause another entry hit.
   Teleport never enters resource correction replay. Segment contacts carry the same effect with `TargetIsBullet`; the owner destroys the segment.
   Either device can report a segment contact; the existing contact ledger prevents duplicate application.
   `SessionCodec` version 13 retains four effect bits and rejects earlier versions. No new correction fields are needed.
5. **Checks.** `TeleportTests` covers the six target cases, heading, wrapping, spin, contact re-entry, charge, head rebuild, and remote delivery.
   `SessionCodecTests` covers ship and segment records and rejects teleport intervals or typed damage records.
   Visual flashes remain outside this phase's scope.

## Capture and crazy drones

Phase 5, step 6d. The mapper supports `CaptureDrones` and `DriveDronesCrazy`, including capture-only ammunition.
Charge and weapon modifiers leave these flags unchanged. `DroneControl_M` and `EmpBomb` now map.

1. **Targets.** Both effects act on drones and missiles, once per new contact. Ships and tail segments ignore them.
   A `HitFirstTarget` projectile still stops on a ship. EMP drains remain independent of its ownership effect.
2. **Drone ownership.** Capture applies the source's allegiance and makes the source ship the mothership.
   Crazy sets `Undefined` and removes the mothership. `ShipEntity.MothershipOverride` carries this change into `ShipState`.
   A null override retains the original bay association; an explicit null entity means no mothership.
   The original bay keeps counting the drone. Its roster side, network owner and identity do not change.
3. **AI and source protection.** The drone context reads the override and updates its mothership and roster deployment.
   EMP can remove an owner, and capture can restore one. Mothership nodes then use the new ship.
   Friendly-fire source protection uses that same link, so the firer's current drones remain safe.
4. **Missiles.** Ownership changes update allegiance, collision filters and the physics owner group, then restart homing search.
   `BulletEntity.AllegianceOwner` overrides the original firing ship for allegiance and source protection; child bullets inherit it.
   Spawn identity and source weapon stay intact. Crazy missiles have no source immunity.
5. **Network and credit.** `HasRemoteOwners` disables both ownership effects, preserving all other ammunition effects (H9 interim).
   No network record changes. The core keeps no kill credit, including for captured drones (D13).
6. **Checks.** `DroneControlTests` covers capture, EMP, source protection, missile search, contact entry, charge and remote-owner exclusion.
   `TargetNodeTests` covers EMP, recapture and the new mothership's death through the live snapshot.

## Harpoons

Phase 5, step 6e. `EnergySiphonHarpoon` and `DotHarpoon` map to continuous cannons with `BulletDefinition.IsHarpoon`.
The hook uses a circle with radius 0.5 × body size. `Harpoons` owns attachment and break decisions.

1. **Free flight.** `BulletDef.MaxOwnerDistance` caps the sweep endpoint at range from the firing ship, before contact queries.
   The hook stops at the rope length, including across the arena seam. Capture changes collision ownership, but preserves the rope anchor.
2. **Attachment.** The first ship, drone or tail segment contact becomes the target at the next stage 4.
   The hook follows that body's local attachment point. A rope joins the firing ship to that point, with connected-body contacts enabled.
   The solver pulls both bodies according to their masses. The hook has no separate solver mass, as agreed in P4.
3. **Damage and lifetime.** Attached hooks use the existing over-time contact path for damage and energy drain.
   Holding the key renews the hook's lifetime. After release, it expires 0.5 seconds after its last renewal.
   Zero-cost continuous weapons launch and renew at zero energy or zero capacity: `ResourcePool` passes every free cost.
   Point defense or lethal projectile damage removes the hook and rope. Removing the firing ship disposes of both without triggers.
4. **Break and target loss.** Above range × √1.5, the owner removes the rope and detonates the hook.
   A missing target, or one with contacts disabled, frees the hook. It can attach again while its weapon keeps it alive.
5. **Network.** Existing `MissileTarget` records carry sticks, segment targets and null-target detachments; the codec does not change.
   Both copies run the rope solver. Ship corrections move its anchors. A hook copy waits for the owner's removal record, including after nominal expiry.
   As with sticky mines, a delayed stick record uses the copy's current hook pose; a live duel remains untested.
6. **AI and view.** `BulletState.AttachedTargetId` exposes the attachment to `HoldHarpoon`.
   The node holds an enemy ship and retains the key for 0.5 seconds after losing it, using the legacy shared timer.
   It ignores drones, tail segments and allies. Normal attack selection skips a harpoon with an active hook.
   `BattleRenderer` draws the hook sprite and an interpolated rope to its firing ship, using the nearest arena image.
7. **Checks.** `HarpoonTests` covers flight, seams, attachment, damage, drain, mass response, break, expiry, destruction, cleanup and remote records.
   `BulletTests` checks that the capped sweep cannot hit beyond range. `ActionNodeTests` checks holding and delayed release.

## Hull auto-aiming barrels

Phase 5, steps 12a–12b. `BarrelDefinition` holds the rest muzzle, mount angle, half-arc, turn speed and rotating muzzle offset.
`BarrelAiming` owns target search, lead, turn limits and idle catch-up. Angles and speeds use radians.

1. Each ship has separate state per barrel. All weapons on one barrel share its target and angle.
2. Turrets search each tick. Other auto-aiming barrels search at creation, before shots and when the pilot requests a search.
3. Search excludes allies and `TargetPriority.None`. It scores priority, distance and size, preferring reachable targets over fallbacks.
   Fallbacks can be outside the arc or within twice the weapon's hit range. Retries wait 0.1 seconds; live targets stay for one second.
4. Rotation uses target lead, the weapon's relative velocity effect and the barrel's speed and arc.
   A non-turret mount can spend its accumulated idle time turning as it acquires a target. A lost target returns the barrel to its mount angle.
5. Projectiles, beams and attached ammunition use the rotating muzzle. `BattleSnapshot.GetBarrels` exposes copied angles and target IDs.
   `BattleRenderer` interpolates turret sprites relative to the interpolated hull.
6. `PilotControls` carries an ally target with selected device bits, plus independent search-request bits.
   `ControlFrame` consumes these requests once. Failed AI tree evaluations still submit them.
   AI fire checks apply the barrel arc only for a matching or null platform target.
7. `ShipDefinition.TargetPriority` supplies initial priority. The entity and `ShipState` carry current priority; AI reads the snapshot.
8. Only the owner searches and consumes pilot target requests. It sends `OwnerOperation.TurretTarget` when a barrel's target changes, including clears.
   Copies retain the received target and calculate rotation locally. They never search, including before predicted shots.
   Incoming records apply at stage 1 when due; late records apply without replaying past ticks.
9. The record names the ship's control index, barrel index, and an optional 64-bit target ID.
   An ID supports allies and targets whose deployment has not arrived. The codec version remains 13, as agreed before release.
   Validation checks sender ownership and the live ship's auto-aiming mount. Application checks them again after pending deployments.
10. `NetworkSession` preserves local ally-target and search requests through input quantization, including requests with unchanged motion and fire controls.
    These requests use the owner's input delay. Remote inputs carry motion and firing; reliable records carry the resulting barrel targets.

`TurretTargetGateTests` is the engine-free two-owner gate scene. It checks held delivery, unchanged-input ally requests,
target clears, duplicate records, and final angle convergence with zero- and five-tick input delays.
The local `Experimental/CombatSimulationHarness` also has a `Turrets` scene with three mounts and six drones per owner.
On 2026-11-07, seven link profiles ran twice at seed 1 and 150 ms corrections: no target mismatches or unexpected failures.
Both state and packet hashes repeated. The asymmetric profile retained its known three-tick pacing offset.

Two hash baselines in `StructureBaselineTests` cover the mounts: a single-player battle and a two-owner duel on `ScriptedBattle.TurretSetup`.
Each side has a hidden 20° barrel, an 80° side turret, a full-circle beam turret, a low-priority target and a redeployment.
Their digest adds barrel angles and targets; the step 8b baselines omit them, so their hashes stay unchanged. A new hash needs owner approval.

## Satellites

Phase 5, step 12c. Legacy `Satellite` with `SatelliteController` or `AutoAimingSatelliteController`. `SatelliteDefinition`
holds the slot, rest heading, mass, arc limits, velocity and angular factors (legacy 0.75 and 3) and an optional shape.
A `BarrelDefinition` names its carrier: `Hull` or a satellite index. Its position and angle are then in the satellite frame.

1. **Mapping.** `CoreShipMapper` reads each satellite's mount point with the Frontier `SatelliteFactory` formula
   (satellite plan, phase 7). The slot is the mount point position × (ship scale + satellite scale) / 2. The rest heading
   is the mount point direction plus the platform rotation. The arc is the direction from the ship centre to the mount
   point ± `RotationLimit`; a limit below 5° makes a satellite that does not aim. Main's default mount points
   ((−0.75, ±1.5), direction 0, limit 127) give −10.4°..243.6° on the left: the legacy forward edge, a wider rear edge.
   The mass is cells × `DefaultWeightPerCell` / 1000. The core never derives a left or right slot. The mapper reads
   `IShipInfo.CoreSpecification`, which has every mount point; the legacy simulator keeps mount points 0 and 1.
   A satellite barrel uses the satellite's model scale. Its weapons map as hull weapons; the `satellite weapon` reason is gone.
2. **Kinds.** Legacy `SatelliteFactory` rules. An arc of at least 5° below 360° without an image makes an aiming satellite:
   its one barrel is fixed (arc 0) and the whole satellite turns. Any other satellite keeps the ship heading plus its rest
   heading; an auto-aiming barrel on it turns as a hull barrel, relative to the satellite.
3. **Body.** Each satellite is a physics body without a shape, created at the ship centre and heading when the ship gets
   its pose. Stage 4, after the engines, sets its velocities (legacy `UnitExtensions.MoveTowards`): the ship velocity plus
   the slot offset × 0.75 × w, and the heading error × 3 × w, with w = 1 / (√(1 + mass) − 1) and Unity's angular drag 0.05.
   More than 50 units from its slot, it jumps there. The ship's removal destroys the body.
4. **Shots.** Projectiles, beams and attached ammunition leave from the satellite's present pose and inherit its velocity.
   The satellite takes the recoil. A satellite keeps no spawn history, so a remote copy fires from its present pose.
5. **Aiming satellite.** It searches each tick, as a turret, from its slot. The search arc is centred on its limits
   (owner decision 8): centre `rest + (min + max) / 2`, half-width `(max − min) / 2`. Legacy D9 skips this arc test.
   It turns toward the lead point from the slot when that direction lies inside `rest + min .. rest + max`, else to its rest
   heading. Its target uses the barrel state and `OwnerOperation.TurretTarget`. A weapon's search request or shot only
   updates the weapon values, as legacy `IAimingSystem.Aim`.
6. **AI.** `WeaponMount` reads the muzzle from `BattleSnapshot.GetSatellites`. `AttackTurretTargets` fires an aiming
   satellite's weapon at its target. When the satellite holds the enemy, the fire check compares the aim direction with
   the satellite's muzzle heading, not the ship heading (redesign, owner, 2026-11-07). Other satellites keep the legacy
   check.
7. **View.** `BattleRenderer` draws each satellite at its interpolated pose in the ship's colour, and a turret on it relative
   to the satellite. A ship's death fades its satellites; a retreat or vanish removes them.
8. **Destructible satellites** (satellite plan, phase 6; Frontier `DestructibleSatellite`). `SatelliteDefinition` adds
   `HitPoints` and `RespawnTime`; `VelocityFactor` takes Frontier `Steadiness`. A satellite with a shape and at least 1 hit
   point is destructible; any other satellite has no shapes and takes no contacts. The rules:
   1. **Entity.** A destructible satellite is a contact target of its own (`EntityKind.Satellite`, `SatelliteStore`). Its ID
      derives from the ship ID and the satellite index, so both devices know it without a spawn record. It never enters the
      ship store, target searches, roster results or kill counts.
   2. **Contacts.** Projectiles, beams, beam bolts, areas and gravity waves hit it, and a hostile ship rams it. It takes the
      total damage without resistance and the push of a projectile or a gravity wave. It deals no ramming damage. Nothing
      sticks to it. Point defense ignores it. A touch with it ends contact stealth as a touch with a repair bot does.
   3. **Death.** Stage 10 applies the damage; strictly below 0 hit points the satellite dies at stage 12. Its shapes ignore
      everything, its barrels are not ready (`ActiveTick`), and its body keeps following the slot. Hit points become 0.
   4. **Respawn.** At stage 2 of death tick + 1 + `TickTime.AtLeast(RespawnTime)`, before the barrels, the satellite gets
      full hit points and moves to the ship centre and heading (Frontier `FakeRespawn`). Without a respawn time it stays
      destroyed. It leaves with its ship.
   5. **Network.** As a repair bot: the owner keeps the hit points; a remote attacker's contact travels as a hit record; the
      owner's removal record (reason `Death`) destroys the copy at the owner's tick, so both respawn in the same tick. No
      wire format changes. A copy shows full hit points until the record arrives.
   6. **Presentation.** `SatelliteState` adds `IsActive` and `HitPoints`. The `SatelliteChange` event (ship ID, satellite
      index, position) marks a death (`RemovalReason.Death`) or a respawn (`None`). `BattleRenderer` hides a destroyed
      satellite and its turret and plays the drone explosion at its death.
   7. **AI.** A weapon on a destroyed satellite is not ready to fire. `TargetNodes` is unchanged: the AI never targets
      satellites, and the obstacle check skips them as before.

   Mapping (phase 7): an aiming satellite keeps the legacy velocity factor 0.75, as Frontier's aiming controller does;
   any other satellite takes `Steadiness`. The hit points are `Satellite.HitPoints(ArmorPoints / ArmorMultiplier) ×
   ArmorMultiplier`; `RespawnTime` maps as is. A destructible satellite gets one outline from its sprite's physics shape,
   reduced with tolerance 0.2 (the path with the largest area), or a circle around the sprite bounds. Frontier's
   built-in satellite prefabs have circle colliders instead; Main's prefabs have none, so Main uses the sprite rule for
   every image. Main's built-in satellites have no hit points, so none of them is destructible. `DestructibleSatelliteTests` covers contacts,
   death, respawn, disabled barrels, the snapshot copy, determinism and both network directions.

`SatelliteTests` covers the plan examples, the arc fix and the remote record. `SatelliteGateTests` is the two-owner gate:
held delivery, ally targets, clears, duplicate records, and converged satellite poses with zero- and five-tick delays.

## Weapon classes and rockets

Phase 5, steps 4a, 4b, 4f and 4g. `CannonDefinition.Mode` copies the legacy weapon classes that fire projectiles from a held key; `Magazine` is the bullet count (multi-shot) or the burst length (machine gun). Examples: phase 5 plan, "Weapon classes and controllers (4)".

1. **Multi-shot** (legacy `MultishotCannon`). One shot needs the cooldown, the barrel and `Magazine × cost` of energy, spent at once. It requests `Magazine` bullets from one spawn slot in one tick (sequences 0 to n − 1), each with its own spread draw and recoil. One barrel shot.
2. **Machine gun** (legacy `MachineGun`). While the key is held, the magazine is not empty, the cooldown is over and the energy is above the cost (a strict check), the gun fires one bullet whenever the barrel is ready (every 3 ticks). After the last shot, the next update without a shot refills the magazine and starts the cooldown. A released key keeps the count. A confirmed shot re-syncs only the barrel, because the weapon cooldown follows bursts; the copy window of a shot is the barrel cooldown.
3. **Random factor.** Legacy `WeaponFactory` sets `RandomFactor` 0.2 (machine gun) and 0.3 (multi-shot): each bullet's speed and lifetime vary by (r − 0.5) × factor. `BattleWorld.DrawShot` draws them from `CounterRandom` (`ShotVariation`, the bullet ID): speed, then lifetime. A copy that adopts the owner's ID takes the owner's draws. Homing thrust and the speed cap scale with the speed; the turn rate does not (legacy).
4. **Rocket** (legacy `RocketController`, `BulletDefinition.IsRocket`). From the tick after its spawn tick, stage 4 changes the velocity by `normalize(max(forward speed, speed) × heading − v) × speed / (0.1 + weight) × dt`. It also removes the sideways speed. The thrust keeps full size until the velocity matches: the forward speed ends up to one tick of thrust above the speed, and a sideways remainder flips sign each tick (legacy). It never turns. A rocket on an attached bullet does nothing, so the mapper maps it as a plain bullet.
5. **Late spawns.** A late spawn record applies at stage 1, before stage 4. `RocketSteering.Advance` moves a late projectile from its spawn tick to the start of the present tick, with the thrust of the ticks between; a plain projectile moves on a line. A shot copy, a confirmed launch (P1) and a late child use it, so a remote rocket matches its owner without corrections.
6. **Mapper.** `CoreWeaponMapper` maps `MultiShot` and `MashineGun` weapons to cannons with the mode and `Magazine`; a magazine below 1 is inert (`weapon class … without magazine`). `Rocket` controllers take `StartingVelocityModifier`. Step 4b: `Manageable` weapons map to the manageable mode; `StickyMine` controllers set `StickyLifetime` (inert: `sticky bullet attached to the parent`, `sticky bullet without lifetime`).
7. **Manageable** (step 4b, legacy `ManageableCannon`). A shot needs the key, the cooldown, the barrel and the energy, as a cannon; it records no barrel shot. Stage 5 hands the bullet to the device (`CannonDevice.Hold`), and the device state names it (`DeviceState.ActiveBullet`). While the bullet flies, the device is active and ready while the key is held, whatever the barrel and the energy; a released key makes the owner mark the bullet's detonation at stage 2 (`BulletStore.MarkDetonation`). Legacy also needs a ready barrel, so another weapon's shot on that barrel detonated the bullet; the core fixes this defect (owner, 2026-11-03). A retreat counts as a released key. The update that finds the bullet gone records the barrel shot and starts the weapon cooldown; it fires nothing. A dead ship's bullet flies on, and nobody detonates it.
8. **Detonation** (legacy `Bullet.Detonate`). A bullet marked before the physics step takes no contacts in that tick and is no target. Stage 12 removes it with `Death` and runs `Destroyed`, then `Detonated` whatever `DetonateWhenDestroyed` (`BulletEntity.Detonates`). The children start at the bullet's pose after that tick's move: one tick of flight later than legacy.
9. **Sticky mine** (step 4b, legacy `StickyController`, `StickyMine`). Stage 8 records the first ship, drone or tail segment a sticky bullet touches. At the next stage 4 the bullet rides on that body (physics `AttachBullet`) at its pose in the body frame, with the offset × 0.9. Its expiry moves by `StickyTicks`, and the owner detonates it at stage 4 of the tick `StickyTicks − 1` after the stick tick. When the target leaves, the bullet leaves at the next stage 12 with `Disposal` and no triggers. Physics never hits an attached bullet's parent, but legacy keeps the stuck bullet inside its target's collider: each step, `BulletStore.CollectStuckHits` adds one time-0 overlap contact of a stuck `OverTime` bullet on its target's first shape, so its damage over time goes on.
10. **Network** (H8). Only the owner detonates and sticks. A copy takes the weapon cooldown from the owner's removal record: the tick after the record's tick, however late it arrives (`CannonDevice.OnRemoteRemoval`); a confirmed shot of a manageable cannon re-syncs neither cooldown nor barrel. The owner sends a stick as a `MissileTarget` record; the copy sticks to that target at its own pose when the record applies. A stick to a tail segment sets the record's segment flag (step 5a, `SessionCodec` version 11): the segment travels as its entity ID.
11. **Charging** (step 4f, H7, legacy `ChargeableCannon`; `CannonDefinition.Charged`, mode `Charging`, and `BeamDefinition.Charged`). `WeaponCharge` holds the rule for both devices. A held key starts the charge when the barrel is ready and the energy is at least half the full cost; the start spends nothing and counts charge tick 1. Each later tick with the key held spends `EnergyCost × 0.02 / ChargeTime` and adds a tick; from `TickTime.After(ChargeTime)` ticks on, the key holds the charge for free. The charge c is the ticks over `ChargeTime × 50`, 1 from `TickTime.AtLeast(ChargeTime)` ticks. A released key, a blocked bit, a retreat or energy that runs out fires the charge at that tick when it has at least 6 ticks (more than 0.1 s), else drops it with its energy. The shot records a barrel shot; there is no weapon cooldown. `DeviceState.Charge` shows c, `Ready` is legacy `CanBeActivated` (a running charge, or the barrel and half the cost), `Cooldown` is the barrel only, and `ActiveBullet` is the last shot's bullet while it lives. A charging weapon is not a shot weapon: a warp lets it charge and fire.
12. **Charged definitions.** The shot uses `BulletDefinition.WithCharge(c)` or `BeamDefinition.WithCharge(c)`, cached per device and charge tick count (`CannonDevice.BulletFor`, `BeamDevice.BeamFor`); a full charge is the device's own definition. Legacy `BulletStats` with `PowerLevel` c: damage, push and radial push × c; range × c, or 0 at c ≤ 0.1 (a beam then hits nothing; a bullet without a body lifetime expires after its first step); body size, collider radius, weight, recoil and beam-bolt length × (0.5 + 0.5c). A body lifetime stays, except a charged beam's (G9, owner 2026-11-03: × (0.5 + 0.5c)). Each child bullet takes its parent's charge, recursively (behaviour change "Charged child bullets"; legacy keeps the charge of the weapon's first shot). `Uncharged` and `Charge` name the source; the scene looks up the view by `Uncharged` and scales its size.
13. **Charge network.** The owner's spawn record carries the shot's charge ticks (`OwnerOperation.ChargeTicks`). The receiver builds the bullet or beam of that charge; a copy that its own control frames fired takes the owner's definition and expiry (`TakeCharge`), but its body keeps the radius of its predicted charge. A confirmed shot ends the copy's own charge that started at or before the shot (`WeaponCharge.EndAt`), so a missed release frame fires no second shot. The copy window of a charging weapon is the barrel cooldown plus 6 ticks, minus 1. Corrections carry no charge.
14. **Charging mapper and AI.** `RequiredCharging` with a beam controller maps to a charged beam (the shot-beam limits of [Beams](#beams), item 12), any other controller to a charging cannon; the fire-rate cooldown is the charge time. A fire rate of 0 or below is inert (`charging weapon without fire rate`). AI: `IsReadyToFire` needs c = 1; `Activate` releases the key; `SustainAim` tracks a charging weapon while its `ActiveBullet` lives; `ChargeWeaponsNode` (legacy `ChargeWeaponsNode`) holds the key of each charging weapon whose remaining cost `EnergyCost × (1 − c)` the free energy covers, or while the energy is at least 99%, and reserves that cost; Running while one charges, Success when all held ones are charged. `ChargeTests` covers the plan's rows 1-9 and 11, the definitions and three network cases.
15. **Continuous cannon** (step 4g, legacy `ContinuousCannon` with a bullet that is not a beam; mode `Continuous`). The device holds one bullet, as a manageable cannon does. A shot needs the key, the weapon cooldown and a ready barrel, and costs `EnergyCost` (legacy `ActivationCost`: `AuraEmitter` is no continuous controller, so no × lifetime). While the bullet lives, each tick with the key and the cooldown over pays `EnergyCost × 0.02` and renews its lifetime from that tick (`Auras.Renew`, legacy `Lifetime.Restore`); any other tick, also one without the energy, restarts the cooldown (legacy `TimeFromLastUse = 0`). The update that finds the bullet gone may fire the next one. `Ready` needs the cooldown, and the barrel only without a bullet; no energy check. A retreat counts as a released key; a warp does not block it.
16. **Aura** (step 4g, legacy `AuraController`, `BulletDefinition.IsAura`). `Radius` is the full radius. At the end of stage 5 the aura grows one tick, to at most `AuraGrowthTicks` (the body lifetime in ticks), and never above the ticks of lifetime left before this tick's step: g = min(g + 1, G, G − (tick − renewal tick)). Physics takes the radius R × g / G (`PhysicsWorld.SetBulletCircle`; the local center scales with the radius). At g = 0, the last tick of its lifetime, it takes no contacts. So a held aura grows in G ticks and stays; a released one shrinks to 0 over its lifetime; one released during growth grows until it meets the falling share. The snapshot shows g / G (`BulletState.AuraPower`); the scene scales the area sprite by it. The mapper maps `AuraEmitter` on any weapon (inert: `aura without lifetime`) at legacy full size 2 × `BodySize`, and a `Continuous` weapon with it to a continuous cannon.
17. **Aura network.** Only the owner expires an aura: its weapon's renewals follow the owner's keys. A copy grows and shrinks its aura by the control frames it has and leaves it with the owner's removal record (`BulletStore.MarkDeaths`); a late record adds the aura whatever its spawn tick. A copy adopts its device's held aura at any spawn tick, as a continuous beam; a record of another aura removes the held one, a prediction. No codec change. `AuraTests` covers the plan's rows 1-4, a renewal while the aura shrinks, damage while it grows and shrinks, and the network rule.

## Beams

Phase 2 step 8 (Q3). Immediate shutdown replaces the legacy lifetime tail (owner decision, 2026-10-20).

1. **Definition.** `BeamDefinition` is a device with barrel index, cooldown seconds, activation cost, energy per second, range and damage per second. Values must be finite; range is positive and costs, cooldown and damage are nonnegative. Phase 2 uses a fixed muzzle direction without spread or recoil; phase 5 adds the spread and tracking (items 10 and 11).
2. **Activation.** Stage 2 checks the held bit, weapon cooldown, shared barrel cooldown and activation payment. The first active tick pays only activation energy. Later ticks pay `EnergyPerSecond × 0.02` after resource recharge. Each successful tick deals `DamagePerSecond × 0.02`. Failed payment spends nothing and gives no partial damage. The existing resource rule requires positive available energy even for a zero cost.
3. **Shutdown.** Release, failed payment or retreat start stops the ray before stage 6 and starts cooldown on that tick. A held bit can restart after cooldown and energy recovery, but never on the shutdown tick. A beam records one barrel shot on activation; maintaining it does not occupy the barrel.
4. **Lifetime and identity.** Stage 5 creates one entity in `BulletStore` using the normal child ID. It has no physics body or collider, cannot be hit, and has no projectile expiry. The same ID lasts through the activation. Stage 12 removes stopped beams with `Disposal`. Parent removal marks attached beams before releasing parent state; projectiles still outlive their parent.
5. **Queries.** Stage 6 samples the current muzzle pose and performs one pass-through ray cast with eight reusable hit slots. It uses the energy collision filter and ignores the parent body. Enemy ships and missiles block the beam; friendly ships, friendly missiles and energy bolts do not. The ray and its no-hit endpoint use the physics limit of the smaller arena dimension.
6. **Selection.** Stage 8 collects the retained ray candidates; stage 9 selects the nearest valid target after disarms. A hit consumes this tick's ray, not the beam entity. Shield priority breaks equal-distance ties before entity ID and shape order. Damage-over-time deduplication limits each attacker/target pair to one hit per tick.
7. **Overflow.** If all eight returned targets are invalid and physics reports overflow, that tick has no hit. `BeamQueries.ExhaustedRayCount` increments and a warning identifies the battle, tick and beam. Overflow with an accepted target does not increment it.
8. **Validation.** `BeamTests` covers legacy LaserBeam_S_1 values, energy boundaries, cooldowns, retreat, identity, query ordering, shields, missiles, wrapping, snapshots and exhausted buffers. `PointDefenseTests` also checks real disarms through complete battle steps, including exhausted ray buffers. Sustained hits and repeated activations allocate zero bytes after warm-up.
9. **Shot beam** (phase 5, step 4d, legacy `CommonCannon` with a beam controller). `BeamDefinition.Shot` has a lifetime (`IsShot`); `ActivationCost` is the energy per shot and `EnergyPerSecond` is 0. The device fires as a single-mode cannon: the held bit, the weapon cooldown from the last shot, the barrel and the energy, then one barrel shot. It holds no beam (`DeviceState.ActiveBullet` is empty) and is a shot weapon, so a warp blocks it. The beam rides on its barrel and expires after `LifetimeTicks` (`TickTime.After`) at stage 11, as a projectile does; stage 12 removes it with `Expiry`. Ship removal removes a live shot beam with `Disposal`, without triggers (legacy `BulletCompositeDisposable`).
10. **Spread** (legacy `BeamController`). Each tick stage 6 turns the ray from the barrel by (r − 0.5) × `Spread`, for a continuous or a shot beam. `r` comes from `CounterRandom` (`BeamJitter`, the beam ID and tick), so a remote copy with the owner's ID draws the same angle. A beam without spread draws nothing.
11. **Tracking** (legacy `LookAtTargetController`, `TrackingAngle` > 0). Stage 6 points the ray at the position of the target the beam touched in the previous tick (a ship, a tail segment or a missile; the nearest torus image), at most `TrackingAngle` from the barrel. Without a target it turns by (2r − 1) × `Spread`: the full spread either way (legacy).
12. **Mapper.** A `Common` weapon with a beam controller maps to a shot beam with `Body.Lifetime` (inert: `shot beam without lifetime`); `RequiredCharging` maps to a charged beam (step 4f, [Weapon classes and rockets](#weapon-classes-and-rockets), item 11); other classes that fire projectiles keep the reason `cannon with controller Beam`. Both forms take the weapon `Spread` and the controller `TrackingAngle`, and the same limits: one damage type, no push or falloff, a positive range, and `AttachedToParent` (inert: `beam not attached to the parent`; legacy leaves such a beam at its spawn pose). A charged beam also needs damage over time and no gameplay trigger. A Common shot beam also allows `Hit` spawn triggers and `HitAllTargets` (step 5f).
13. **Beam bolt** (phase 5, step 4e, legacy `MovingBeamController` and `RayCastCollider`). A cannon bullet with `BeamLength` > 0 is a ray of up to that length from its pose along its heading (`BeamBolts`). Its physics body pairs with nothing, and point defense skips it (the legacy prefab has no collider). It launches with `ParentVelocityEffect` × the ship velocity only. Its ray is 0 in the spawn tick, then speed × age × 0.02, at most the length and the range. In the tick the ray reaches the length (`GrowthTicks`, `TickTime.AtLeast(length / speed)`), stage 4 adds speed × heading to its velocity. Its lifetime starts after the growth, so `LifetimeTicks` and the random-factor draws add the growth ticks. In flight the ray is the remaining lifetime × speed when that is shorter than the length; otherwise the length, capped by the range (the legacy collider keeps its growth cap). A late spawn record replays the launch in `RocketSteering.Advance`.
14. **Bolt contacts.** Stage 6 casts the ray on the poses at the start of the step, with the energy filter, ignoring the bolt's ship. Each hit becomes a projectile contact (`BulletSweep`, time 0, the ray fraction orders the targets), so the projectile rules apply: a `FirstTarget` bolt stops at its first valid hit and runs its `Hit` triggers; an `AllTargets` bolt hits a unit when the unit enters its ray (it was not on the ray in the tick before); an `OverTime` bolt hits every tick. Only missiles among bullets take its hits. A ray without pass-through (`Piercing` false: every beam shape but `PiercingLaser`) gives an `AllTargets` or `OverTime` bolt its first unit only. Stage 9 cuts such a ray at its first accepted hit for the view: the snapshot draws it from the bolt's pose after the step (`BulletState.HasRay`, `BeamEndPoint`), 0 once the bolt has passed the hit. The network needs no new record: a bolt is a plain projectile (spawn record with its launch pose).
15. **Bolt mapper.** A beam shape on a cannon with the `Projectile` controller maps to a beam bolt; its length is `Body.Length`, or the body size when 0 (a child scales it by `AoeRadiusMultiplier`). Inert: `beam bolt with controller …`, `beam bolt with hit points`, `beam bolt attached to the parent`, `beam bolt without speed`. `BeamBoltTests` covers growth, launch, lifetime, shrink, the range cap, first-hit and piercing rays, damage over time, point defense and a late remote copy.
