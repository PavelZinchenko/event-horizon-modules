using System;
using CombatSimulation.Physics;
using Utilites;

namespace CombatSimulation.Core
{
    // What a battle mode reads and does. The battle gives the same context to every call.
    //
    // Actions run only in IBattleMode.BeginTick, at once and in call order, before the queued commands of the tick. They
    // have no command IDs. A bool action returns false when it does not apply (unknown ID, wrong status). With remote
    // owners every action that changes units or ships throws (D10): the online duel runs only RosterMode.
    public sealed class ModeContext
    {
        internal ModeContext(Battle battle) => _battle = battle;

        // The tick being processed.
        public long Tick => _battle.Tick;
        public BattleSetup Setup => _battle.Setup;

        // The state and events of the last completed tick.
        public BattleSnapshot Snapshot => _battle.Snapshot;

        // The events of the tick so far. At stage 13 the batch has every event of the tick except the result and the
        // damage events.
        public ReadOnlySpan<BattleEvent> TickEvents => _battle.TickEvents;

        // The units now, in table order: the setup's units, then the added ones.
        public int UnitCount => _battle.Units.All.Count;
        public UnitState GetUnit(int index) => new UnitState(_battle.Units.All[index]);

        public bool TryGetUnit(UnitId id, out UnitState unit)
        {
            var slot = _battle.Units.Find(id);
            unit = slot != null ? new UnitState(slot) : default;
            return slot != null;
        }

        public int CountUnits(int side, UnitStatus status) => _battle.Units.Count(side, status);

        // Adds a Ready unit after the existing units. The ID must be new and the owner local; the damage types are
        // checked as at setup.
        public void AddUnit(UnitSetup unit) => _battle.ModeAddUnit(unit);

        // Deploys a Ready unit, as the Deploy command does, at the placement.
        public bool Deploy(UnitId id, in SpawnPlacement placement) => _battle.ModeDeploy(id, placement);

        // A Destroyed unit becomes Ready (UnitTransitionReason.Respawned). Its deficits become zero, and its drone bays
        // and cloning centres refill. Its next deployment takes its live drones.
        public bool Respawn(UnitId id) => _battle.ModeRespawn(id);

        // As the Retreat command.
        public bool Retreat(EntityId id) => _battle.ModeRetreat(id);

        // As the Vanish ship command (D12).
        public bool Vanish(EntityId id) => _battle.ModeVanish(id);

        // A bullet or beam leaves at stage 12 with a Disposal removal and runs no triggers (Frontier's preparation clears
        // the enemy's parasites, which restore their lifetime with each hit). A battle with remote owners throws (D10).
        public bool VanishBullet(EntityId id) => _battle.ModeVanishBullet(id);

        // As the ChangeAllegiance ship command (D13).
        public bool ChangeAllegiance(EntityId id, Allegiance allegiance) => _battle.ModeChangeAllegiance(id, allegiance);

        // The ship's linear velocity, at once. BeginTick comes before the engine and the physics, so the velocity in the
        // snapshot is the ship's velocity until this call (exploration docking brake). A battle with remote owners
        // throws (D10).
        public bool SetVelocity(EntityId id, Vec2 velocity) => _battle.ModeSetVelocity(id, velocity);

        // The ship jumps at once to the position (torus wrap) and the angle, with its satellites (TeleportGroup); its
        // velocity and spin become 0 (Frontier's cheater: TODO/captain_perks.md, C11). A Teleport event with device index
        // -1 tells the view to snap the ship without a device effect. A battle with remote owners throws (D10).
        public bool Teleport(EntityId id, Vec2 position, float angle) => _battle.ModeTeleport(id, position, angle);

        // Destroys a unit at stage 12, as surrender does for each unit of a side. A Ready unit has no entity.
        public bool DestroyUnit(UnitId id) => _battle.ModeDestroyUnit(id);

        // Removes the unit from the table at stage 12 of this tick, after the mode's destructions (common level mode,
        // decision 1): its ship vanishes, then its drones and clones, and then the unit leaves with a Removed
        // transition. A ship that dies in this tick dies first. A bullet in flight keeps the unit's credit, which no
        // snapshot shows. A later tick may add the ID again. A battle with remote owners throws (D10).
        public bool RemoveUnit(UnitId id) => _battle.ModeRemoveUnit(id);

        // Adds an asteroid at once (common level mode, decision 2). Its ID is new in the battle: the setup's asteroids
        // take the numbers 0 to n - 1, and each spawn takes the next number. The snapshot of this tick lists it.
        public EntityId SpawnAsteroid(AsteroidSetup asteroid) => _battle.ModeSpawnAsteroid(asteroid);

        // The asteroid leaves at stage 12 with an AsteroidRemoval event whose RemovalReason is Vanish.
        public bool VanishAsteroid(EntityId id) => _battle.ModeVanishAsteroid(id);

        // Adds a static obstacle at once (Frontier's island, TODO/other_modes.md, O8). Its ID is new in the battle, and the
        // snapshot of this tick lists it. It stays through ChangeRules until a mode removes it. A battle with remote owners
        // throws (D10).
        public EntityId AddObstacle(ObstacleSetup obstacle) => _battle.ModeAddObstacle(obstacle);

        // The obstacle leaves at stage 12, without an event.
        public bool RemoveObstacle(EntityId id) => _battle.ModeRemoveObstacle(id);

        // Adds a pickup at once (loot plan, L1, L2). Its ID is new in the battle. It moves from this tick on, and the
        // snapshot of this tick lists it. A battle with remote owners throws (D10).
        public EntityId SpawnPickup(in PickupSetup pickup) => _battle.ModeSpawnPickup(pickup);

        // The pickup leaves at stage 12 with a PickupRemoval event whose RemovalReason is Vanish.
        public bool VanishPickup(EntityId id) => _battle.ModeVanishPickup(id);

        // What the unit's ship does with pickups (loot plan, L5). The flags stay through respawns and ChangeRules until the
        // mode clears them. A battle with remote owners throws (D10).
        public bool SetCollector(UnitId id, PickupCollector flags) => _battle.ModeSetCollector(id, flags);

        // One drone back in the bay's hangar (loot plan, L9: a salvage drone docks). It applies only when the bay has room,
        // so the mode calls it after the drone's removal, in a later tick than its vanish. False for an unknown unit, a
        // device that is not a drone bay, or a full bay. A battle with remote owners throws (D10).
        public bool RestoreDrone(DroneBayKey bay) => _battle.ModeRestoreDrone(bay);

        // The unit's ship, from this tick on, gets its full armor back after the damage of each tick, so no hit kills it
        // (legacy NoDamageSystem: Frontier's preparation ships and the god mode cheat). Shield, energy and the damage
        // events stay as without it; drains, a forced destruction and the unit's drones are not covered. The flag stays
        // through respawns and ChangeRules until the mode clears it. A battle with remote owners throws (D10).
        public bool SetNoDamage(UnitId id, bool noDamage) => _battle.ModeSetNoDamage(id, noDamage);

        // Each later deployment of the unit starts with this share of its maximum energy (Frontier's arena ships start
        // with none: legacy CombatantShipSpawner.Settings.NoEnergy). The missing energy is a spend, so it starts the
        // recharge delay. The value stays through respawns and ChangeRules until the mode changes it; 1 is the default.
        // A fraction outside 0 to 1 throws. A battle with remote owners throws (D10).
        public bool SetStartEnergy(UnitId id, float fraction) => _battle.ModeSetStartEnergy(id, fraction);

        // At stage 10, after the damage: the unit's active ship, when it has one of a local owner, loses the fractions of
        // its maximum armor and shield. No resistance; both delays restart. Drains apply in call order.
        public void Drain(UnitId id, float armorFraction, float shieldFraction) =>
            _battle.ModeDrain(id, armorFraction, shieldFraction);

        // Adds a BattleEventKind.Mode event to the tick's batch. Also at stage 13, before the result.
        public void Publish(in ModeEvent item) => _battle.ModePublish(item);

        // A counter-based random state for the tick and the purpose: the same on every run and every device.
        public RandomState Random(uint purpose) => CounterRandom.Create(Setup.MatchSeed, Tick, purpose);

        private readonly Battle _battle;
    }
}
