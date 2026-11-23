using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One pickup that a mode spawns (loot plan, L1; legacy SpaceObjectFactory.CreateLoot). The core knows only the reward
    // ID: the application maps it to its item and grants it on the Collected removal.
    public readonly struct PickupSetup
    {
        // Legacy Loot: 180 s.
        public const long DefaultLifetimeTicks = 9000;

        public PickupSetup(Vec2 position, Vec2 velocity, float angle, float angularVelocity, float scale, long rewardId,
            long lifetimeTicks = DefaultLifetimeTicks)
        {
            Position = position;
            Velocity = velocity;
            Angle = angle;
            AngularVelocity = angularVelocity;
            Scale = scale;
            RewardId = rewardId;
            LifetimeTicks = lifetimeTicks;
        }

        public readonly Vec2 Position;
        public readonly Vec2 Velocity;

        // Radians and radians per second.
        public readonly float Angle;
        public readonly float AngularVelocity;

        // The legacy body scale. The collection circle has radius Scale / 2.
        public readonly float Scale;

        // Opaque to the core. The removal event and the snapshot carry it.
        public readonly long RewardId;

        // The pickup lives this many ticks, its spawn tick included.
        public readonly long LifetimeTicks;

        internal void Validate()
        {
            if (!Position.IsFinite) throw new ArgumentOutOfRangeException(nameof(Position), "The position must be finite.");
            if (!Velocity.IsFinite) throw new ArgumentOutOfRangeException(nameof(Velocity), "The velocity must be finite.");
            if (float.IsNaN(Angle) || float.IsInfinity(Angle))
                throw new ArgumentOutOfRangeException(nameof(Angle), "The angle must be finite.");
            if (float.IsNaN(AngularVelocity) || float.IsInfinity(AngularVelocity))
                throw new ArgumentOutOfRangeException(nameof(AngularVelocity), "The angular velocity must be finite.");
            if (!(Scale > 0f) || float.IsInfinity(Scale))
                throw new ArgumentOutOfRangeException(nameof(Scale), "The scale must be finite and positive.");
            if (LifetimeTicks < 1) throw new ArgumentOutOfRangeException(nameof(LifetimeTicks), "A pickup lives at least one tick.");
        }
    }

    // What a unit's ship does with pickups (ModeContext.SetCollector; loot plan, L5).
    [Flags]
    public enum PickupCollector : byte
    {
        None = 0,

        // The ship takes a pickup it touches.
        Collect = 1,

        // The legacy magnet pulls pickups toward the ship (legacy Loot: only toward Scene.PlayerShip).
        Attract = 2,
    }

    internal sealed class Pickup
    {
        public EntityId Id;
        public EntityHandle Handle;
        public bool IsAlive;
        public long RewardId;
        public float Scale;
        public Vec2 Position;
        public Vec2 Velocity;
        public float Angle;
        public float AngularVelocity;
        public long LifetimeTicks;

        // The last tick the pickup lives: stage 12 of it removes the pickup with Expiry.
        public long ExpiryTick;

        public RemovalReason PendingRemoval;

        // The ship that took it (Collected).
        public EntityId Collector;
    }

    // One pickup at the end of a completed tick, for the view and the radar.
    public readonly struct PickupState
    {
        public readonly EntityId Id;
        public readonly long RewardId;
        public readonly float Scale;

        // Canonical torus coordinates and radians.
        public readonly Vec2 Position;
        public readonly float Angle;
        public readonly Vec2 LinearVelocity;

        // The last tick the pickup lives.
        public readonly long ExpiryTick;

        // The remaining share of the lifetime, from about 1 at the spawn to 0 in the last tick (legacy Loot view fade).
        public readonly float Life;

        internal PickupState(Pickup pickup, long tick)
        {
            Id = pickup.Id;
            RewardId = pickup.RewardId;
            Scale = pickup.Scale;
            Position = pickup.Position;
            Angle = pickup.Angle;
            LinearVelocity = pickup.Velocity;
            ExpiryTick = pickup.ExpiryTick;
            Life = Math.Max(0f, Math.Min(1f, (float)(pickup.ExpiryTick - tick) / pickup.LifetimeTicks));
        }
    }

    // Loot plan (L1, L4, L6). A pickup is kinematic: no physics body, no contacts. Stage 4 moves it at a constant velocity
    // with the torus wrap, except that the legacy magnet pulls it toward an attracting ship. After stage 9 a collecting
    // ship within reach takes it. Stage 12 removes the collected, expired and vanished pickups. Local battles only:
    // only a mode spawns pickups, and a battle with remote owners has no mode actions (D10).
    internal sealed class Pickups
    {
        // Legacy Loot.UpdatePhysics: the magnet acts within 4 + the ship scale / 2 + the pickup scale / 2, and the
        // acceleration is (direction x 10 + ship velocity - pickup velocity) x 10 per second.
        public const float MagnetRange = 4f;
        public const float MagnetSpeed = 10f;
        public const float MagnetRate = 10f;

        public Pickups(BattleWorld world) => _world = world;

        // In storage order. A removed pickup leaves its entry dead for reuse.
        public IReadOnlyList<Pickup> Items => _items;
        public int AliveCount { get; private set; }

        public Pickup Get(EntityHandle handle) => handle.Kind == EntityKind.Pickup && handle.Index >= 0 &&
            handle.Index < _items.Count && _items[handle.Index].IsAlive &&
            _items[handle.Index].Handle.Generation == handle.Generation ? _items[handle.Index] : null;

        public Pickup Find(EntityId id) => _world.Entities.TryGet(id, out var handle) ? Get(handle) : null;

        // A mode's spawn at stage 1. The ID takes the next pickup number, so it never repeats in the battle.
        public EntityId Spawn(in PickupSetup setup, long tick)
        {
            setup.Validate();
            var index = 0;
            while (index < _items.Count && _items[index].IsAlive) ++index;
            if (index == _items.Count)
            {
                _items.Add(new Pickup());
                _generations.Add(0);
            }

            var id = SpawnIdentity.CreatePickup(_world.BattleId, _nextNumber++);
            var handle = new EntityHandle(EntityKind.Pickup, index, ++_generations[index]);
            if (!_world.TryRegister(id, handle)) return default;

            var pickup = _items[index];
            pickup.Id = id;
            pickup.Handle = handle;
            pickup.IsAlive = true;
            pickup.RewardId = setup.RewardId;
            pickup.Scale = setup.Scale;
            pickup.Position = _world.Physics.Torus.Wrap(setup.Position);
            pickup.Velocity = setup.Velocity;
            pickup.Angle = setup.Angle;
            pickup.AngularVelocity = setup.AngularVelocity;
            pickup.LifetimeTicks = setup.LifetimeTicks;
            pickup.ExpiryTick = tick + setup.LifetimeTicks - 1;
            pickup.PendingRemoval = RemovalReason.None;
            pickup.Collector = default;
            ++AliveCount;
            return id;
        }

        // A mode's vanish at stage 1; stage 12 removes it.
        public bool Vanish(EntityId id)
        {
            var pickup = Find(id);
            if (pickup == null) return false;
            if (pickup.PendingRemoval == RemovalReason.None) pickup.PendingRemoval = RemovalReason.Vanish;
            return true;
        }

        // Stage 4, with the ship poses from the start of the step. The nearest attracting ship within its range pulls; the
        // lower entity ID wins a tie.
        public void Move()
        {
            if (AliveCount == 0) return;
            const float dt = Battle.TickDuration;
            var physics = _world.Physics;
            var torus = physics.Torus;
            CollectShips(PickupCollector.Attract, _attractors);
            foreach (var pickup in _items)
            {
                if (!pickup.IsAlive) continue;

                ShipEntity magnet = null;
                var magnetDelta = Vec2.Zero;
                var magnetDistance = float.MaxValue;
                foreach (var ship in _attractors)
                {
                    var delta = torus.Delta(pickup.Position, physics.GetPosition(ship.Body));
                    var distance = delta.Length;
                    if (distance >= MagnetRange + 0.5f * ship.Definition.Scale + 0.5f * pickup.Scale) continue;
                    if (distance > magnetDistance || distance == magnetDistance && ship.Id.CompareTo(magnet.Id) > 0)
                        continue;
                    magnet = ship;
                    magnetDelta = delta;
                    magnetDistance = distance;
                }

                if (magnet != null)
                {
                    var direction = magnetDistance > 0f ? magnetDelta * (1f / magnetDistance) : Vec2.Zero;
                    var acceleration = MagnetRate *
                        (MagnetSpeed * direction + physics.GetLinearVelocity(magnet.Body) - pickup.Velocity);
                    pickup.Velocity += dt * acceleration;
                }

                pickup.Position = torus.Wrap(pickup.Position + dt * pickup.Velocity);
                pickup.Angle = Battle.WrapAngle(pickup.Angle + dt * pickup.AngularVelocity);
            }
        }

        // After stage 9, with the ship poses after this tick's physics. A pickup is collected when the torus distance to a
        // collecting ship is below the pickup radius plus the ship's hull radius. Not a contact, so NoContacts does not
        // stop it. The lower entity ID wins when two ships reach the pickup.
        public void Collect()
        {
            if (AliveCount == 0) return;
            var physics = _world.Physics;
            var torus = physics.Torus;
            CollectShips(PickupCollector.Collect, _collectors);
            if (_collectors.Count == 0) return;
            foreach (var pickup in _items)
            {
                if (!pickup.IsAlive || pickup.PendingRemoval != RemovalReason.None) continue;

                ShipEntity winner = null;
                foreach (var ship in _collectors)
                {
                    var reach = 0.5f * pickup.Scale + ship.Definition.HullRadius;
                    if (torus.Delta(pickup.Position, physics.GetPosition(ship.Body)).Length >= reach) continue;
                    if (winner == null || ship.Id.CompareTo(winner.Id) < 0) winner = ship;
                }

                if (winner == null) continue;
                pickup.PendingRemoval = RemovalReason.Collected;
                pickup.Collector = winner.Id;
            }
        }

        // Stage 12, in storage order: the collected, the vanished, then the expired.
        public void RemoveFinished()
        {
            for (var i = 0; i < _items.Count; ++i)
            {
                var pickup = _items[i];
                if (!pickup.IsAlive) continue;
                if (pickup.PendingRemoval == RemovalReason.None && pickup.ExpiryTick <= _world.Tick)
                    pickup.PendingRemoval = RemovalReason.Expiry;
                if (pickup.PendingRemoval == RemovalReason.None) continue;

                _world.Events.Add(BattleEvent.PickupRemoval(_world.BattleId, _world.Tick, _world.Events.Count, pickup));
                _world.Entities.Remove(pickup.Id);
                pickup.IsAlive = false;
                --AliveCount;
            }
        }

        // The live local ships with the flag: roster ships by their unit's collector flags (ModeContext.SetCollector),
        // drones by ShipDefinition.CollectsPickups (they collect, never attract).
        private void CollectShips(PickupCollector flag, List<ShipEntity> ships)
        {
            ships.Clear();
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship == null || !_world.Setup.IsLocalOwner(ship.Owner)) continue;
                var flags = ship.Slot?.Collector ?? (ship.IsDrone && ship.Definition.CollectsPickups
                    ? PickupCollector.Collect : PickupCollector.None);
                if ((flags & flag) != 0) ships.Add(ship);
            }
        }

        private readonly BattleWorld _world;
        private readonly List<Pickup> _items = new List<Pickup>();

        // By storage entry: the generation of its last pickup.
        private readonly List<int> _generations = new List<int>();
        private readonly List<ShipEntity> _attractors = new List<ShipEntity>();
        private readonly List<ShipEntity> _collectors = new List<ShipEntity>();

        // The number of the next pickup ID (SpawnIdentity.CreatePickup).
        private uint _nextNumber;
    }
}
