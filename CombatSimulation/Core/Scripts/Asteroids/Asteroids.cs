using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One asteroid of the battle setup (phase 5, step 14; legacy SpaceObjectFactory.CreateAsteroid). The adapter draws
    // the values from the battle seed and the star level; the core holds no asteroid rule of its own (owner question 3).
    public sealed class AsteroidSetup
    {
        // Legacy Asteroid prefab: angular drag 0.02, linear drag 0.
        public const float AngularDamping = 0.02f;

        public AsteroidSetup(Vec2 position, Vec2 velocity, float size, float mass, float hitPoints,
            float rammingDamageMultiplier)
        {
            if (!position.IsFinite) throw new ArgumentOutOfRangeException(nameof(position), "The position must be finite.");
            if (!velocity.IsFinite) throw new ArgumentOutOfRangeException(nameof(velocity), "The velocity must be finite.");
            if (!IsPositive(size)) throw new ArgumentOutOfRangeException(nameof(size), "The size must be finite and positive.");
            if (!IsPositive(mass)) throw new ArgumentOutOfRangeException(nameof(mass), "The mass must be finite and positive.");
            if (!IsPositive(hitPoints))
                throw new ArgumentOutOfRangeException(nameof(hitPoints), "Hit points must be finite and positive.");
            if (!(rammingDamageMultiplier >= 0f) || float.IsInfinity(rammingDamageMultiplier))
                throw new ArgumentOutOfRangeException(nameof(rammingDamageMultiplier),
                    "The ramming multiplier must be finite and not negative.");

            Position = position;
            Velocity = velocity;
            Size = size;
            Mass = mass;
            HitPoints = hitPoints;
            RammingDamageMultiplier = rammingDamageMultiplier;
        }

        public Vec2 Position { get; }
        public Vec2 Velocity { get; }

        // The circle diameter: the legacy body scale (the prefab collider has radius 0.5).
        public float Size { get; }
        public float Mass { get; }
        public float HitPoints { get; }

        // Legacy DefaultCollisionBehaviour(powerMultiplier): the damage the asteroid deals when it rams.
        public float RammingDamageMultiplier { get; }

        private static bool IsPositive(float value) => value > 0f && !float.IsInfinity(value);
    }

    internal sealed class Asteroid
    {
        public EntityId Id;
        public EntityHandle Handle;
        public AsteroidSetup Setup;
        public BodyId Body;
        public ShapeId Shape;

        // A local owner: a battle with asteroids has no remote owner (owner question 2).
        public int Owner;
        public float HitPoints;

        // The damage of this tick's accepted hits; stage 10 applies it.
        public float Damage;
        public bool PendingDeath;

        // Linear velocity at the end of stage 4 (ramming speed).
        public Vec2 PreStepVelocity;

        public float Scale => Setup.Size;

        // Legacy side Undefined: the enemy of every other side, the friend of another asteroid (and of a crazy drone).
        public Allegiance Allegiance => Allegiance.Undefined;
    }

    // One asteroid at the end of a completed tick, for the view and the pilots.
    public readonly struct AsteroidState
    {
        public readonly EntityId Id;

        // The circle diameter.
        public readonly float Size;
        public readonly float Mass;
        public readonly float HitPoints;
        public readonly float MaxHitPoints;

        // Canonical torus coordinates and radians.
        public readonly Vec2 Position;
        public readonly float Angle;
        public readonly Vec2 LinearVelocity;

        internal AsteroidState(Asteroid asteroid, PhysicsWorld world)
        {
            Id = asteroid.Id;
            Size = asteroid.Setup.Size;
            Mass = asteroid.Setup.Mass;
            HitPoints = asteroid.HitPoints;
            MaxHitPoints = asteroid.Setup.HitPoints;
            Position = world.GetPosition(asteroid.Body);
            Angle = world.GetAngle(asteroid.Body);
            LinearVelocity = world.GetLinearVelocity(asteroid.Body);
        }
    }

    // Phase 5, step 14 (H10; legacy Asteroid). The battle creates the setup's asteroids before tick 0. An asteroid takes the
    // total damage of every hit without resistance and the push of a projectile or a gravity wave; no other effect acts
    // on it. It dies below 0 hit points. It rams hostile ships, drones and tail segments with its own multiplier
    // (RammingTracker). Nothing ever spawns one later, so each storage index is used once.
    internal sealed class Asteroids
    {
        public Asteroids(BattleWorld world) => _world = world;

        // In setup order; a removed asteroid leaves a null entry.
        public IReadOnlyList<Asteroid> Items => _items;
        public int AliveCount { get; private set; }

        public Asteroid Get(EntityHandle handle) => handle.Kind == EntityKind.Asteroid && handle.Index >= 0 &&
            handle.Index < _items.Count && _items[handle.Index]?.Handle.Generation == handle.Generation
                ? _items[handle.Index] : null;

        public Asteroid Find(EntityId id) => _world.Entities.TryGet(id, out var handle) ? Get(handle) : null;

        public void Create()
        {
            var setup = _world.Setup;
            var physics = _world.Physics;
            var shapeDef = ShapeDef.Default;
            shapeDef.Filter = CollisionFilters.Asteroid;
            for (var i = 0; i < setup.Asteroids.Count; ++i)
            {
                var definition = setup.Asteroids[i];
                var id = SpawnIdentity.CreateAsteroid(setup.BattleId, (uint)i);
                var handle = new EntityHandle(EntityKind.Asteroid, i, 1);
                if (!_world.TryRegister(id, handle))
                {
                    _items.Add(null);
                    continue;
                }

                var body = physics.CreateBody(new BodyDef
                {
                    Position = definition.Position,
                    LinearVelocity = definition.Velocity,
                    Mass = definition.Mass,
                    AngularDamping = AsteroidSetup.AngularDamping,
                });
                var shape = physics.AddCircle(body, new Circle(Vec2.Zero, 0.5f * definition.Size), shapeDef);
                _world.Shapes.Register(shape, new ShapeEntry(handle, id, ShapeHitPriority.Hull, 0));
                _items.Add(new Asteroid
                {
                    Id = id,
                    Handle = handle,
                    Setup = definition,
                    Body = body,
                    Shape = shape,
                    Owner = setup.Roster[0].Owner,
                    HitPoints = definition.HitPoints,
                });
                ++AliveCount;
            }
        }

        // Stage 4: physics reports a touch after the solve, so ramming uses the velocity from before the step.
        public void RecordPreStepVelocities()
        {
            foreach (var asteroid in _items)
                if (asteroid != null) asteroid.PreStepVelocity = _world.Physics.GetLinearVelocity(asteroid.Body);
        }

        // Stage 9. Legacy Asteroid.OnCollision: the impulse at once, the damage at stage 10.
        public void Resolve(in ContactCandidate hit, Asteroid asteroid, Vec2 impulse)
        {
            asteroid.Damage += hit.Damage;
            if (impulse.X != 0f || impulse.Y != 0f)
                _world.Physics.ApplyImpulse(asteroid.Body, impulse, _world.Physics.GetPosition(asteroid.Body));
        }

        // Stage 10. Legacy dies when its hit points go below 0, so an asteroid at exactly 0 lives.
        public void ApplyDamage()
        {
            foreach (var asteroid in _items)
            {
                if (asteroid == null || !(asteroid.Damage > 0f)) continue;
                asteroid.HitPoints -= asteroid.Damage;
                asteroid.Damage = 0f;
                if (asteroid.HitPoints < 0f) asteroid.PendingDeath = true;
            }
        }

        // Stage 12, in setup order. A bullet stuck to it or a harpoon on it lets go by its own rule (target gone).
        public void RemoveDead(Battle battle)
        {
            for (var i = 0; i < _items.Count; ++i)
            {
                var asteroid = _items[i];
                if (asteroid == null || !asteroid.PendingDeath) continue;

                var physics = _world.Physics;
                _world.Events.Add(BattleEvent.AsteroidRemoval(_world.BattleId, _world.Tick, _world.Events.Count, asteroid,
                    physics.GetPosition(asteroid.Body)));
                _world.Shapes.Release(asteroid.Shape);
                battle.ForgetContacts(asteroid.Id);
                physics.DestroyBody(asteroid.Body);
                _world.Entities.Remove(asteroid.Id);
                _items[i] = null;
                --AliveCount;
            }
        }

        private readonly BattleWorld _world;
        private readonly List<Asteroid> _items = new List<Asteroid>();
    }
}
