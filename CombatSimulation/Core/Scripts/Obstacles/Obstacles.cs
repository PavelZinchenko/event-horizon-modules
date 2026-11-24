using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One static obstacle that a mode adds (Frontier's island, TODO/other_modes.md, O8; legacy
    // SpaceObjectFactory.CreateAsteroidPlatform and StaticPlatform). The outline is in body units around the origin,
    // concave allowed; the constructor splits it into convex parts, so an obstacle that exists is valid. Allocates; for
    // setup, not for the step.
    public sealed class ObstacleSetup
    {
        public ObstacleSetup(IReadOnlyList<Vec2> outline, Vec2 position, float angle, Allegiance allegiance, float mass,
            float rammingDamageMultiplier, long tag = 0)
        {
            if (outline == null) throw new ArgumentNullException(nameof(outline));
            if (!position.IsFinite) throw new ArgumentOutOfRangeException(nameof(position), "The position must be finite.");
            if (float.IsNaN(angle) || float.IsInfinity(angle))
                throw new ArgumentOutOfRangeException(nameof(angle), "The angle must be finite.");
            if (!Allegiances.IsValid(allegiance))
                throw new ArgumentOutOfRangeException(nameof(allegiance), "Unknown allegiance.");
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentOutOfRangeException(nameof(mass), "The mass must be finite and positive.");
            if (!(rammingDamageMultiplier >= 0f) || float.IsInfinity(rammingDamageMultiplier))
                throw new ArgumentOutOfRangeException(nameof(rammingDamageMultiplier),
                    "The ramming multiplier must be finite and not negative.");

            var copy = new Vec2[outline.Count];
            for (var i = 0; i < copy.Length; ++i)
            {
                if (!outline[i].IsFinite) throw new ArgumentOutOfRangeException(nameof(outline), "Outline points must be finite.");
                copy[i] = outline[i];
            }

            Outline = Array.AsReadOnly(copy);
            Parts = ConvexDecomposition.Decompose(copy).AsReadOnly();
            Position = position;
            Angle = angle;
            Allegiance = allegiance;
            Mass = mass;
            RammingDamageMultiplier = rammingDamageMultiplier;
            Tag = tag;
        }

        public IReadOnlyList<Vec2> Outline { get; }

        // Canonical torus coordinates and radians.
        public Vec2 Position { get; }
        public float Angle { get; }

        // Legacy UnitSide of the platform: it blocks the hostile ships and lets its friends through.
        public Allegiance Allegiance { get; }

        // Legacy body weight. The body never moves; the mass counts only in the ramming damage (the smaller mass).
        public float Mass { get; }

        // Legacy DefaultCollisionBehaviour(1) of StaticPlatform: the damage the obstacle deals when it rams.
        public float RammingDamageMultiplier { get; }

        // Opaque to the core: the application maps it to its view. The snapshot carries it.
        public long Tag { get; }

        // The convex parts of the outline, in decomposition order.
        internal IReadOnlyList<Polygon> Parts { get; }
    }

    internal sealed class Obstacle
    {
        public EntityId Id;
        public EntityHandle Handle;
        public ObstacleSetup Setup;
        public BodyId Body;
        public ShapeId[] Shapes;

        // A local owner: only a mode adds an obstacle, and a battle with remote owners has no mode actions (D10).
        public int Owner;

        // ModeContext.RemoveObstacle: it leaves at stage 12.
        public bool PendingRemoval;

        public Allegiance Allegiance => Setup.Allegiance;
    }

    // One obstacle at the end of a completed tick, for the view.
    public readonly struct ObstacleState
    {
        public readonly EntityId Id;
        public readonly long Tag;
        public readonly Allegiance Allegiance;

        // ObstacleSetup.Outline, for the view.
        public readonly IReadOnlyList<Vec2> Outline;

        // Canonical torus coordinates and radians. They never change.
        public readonly Vec2 Position;
        public readonly float Angle;

        internal ObstacleState(Obstacle obstacle, PhysicsWorld world)
        {
            Id = obstacle.Id;
            Tag = obstacle.Setup.Tag;
            Allegiance = obstacle.Setup.Allegiance;
            Outline = obstacle.Setup.Outline;
            Position = world.GetPosition(obstacle.Body);
            Angle = world.GetAngle(obstacle.Body);
        }
    }

    // The island (TODO/other_modes.md, O8). A body with a fixed position and rotation and one shape for each convex part.
    // Legacy layer Platform: it blocks asteroids and the ships that are hostile to it, and ignores bullets, missiles,
    // drones and its friends (CollisionFilters.Obstacle). It takes no damage and no push. It rams a hostile ship or an
    // asteroid as a ship does (RammingTracker); a ship that touches it deals it nothing. Local battles only.
    internal sealed class Obstacles
    {
        public Obstacles(BattleWorld world) => _world = world;

        // In storage order: each addition in the first free entry. A removed obstacle leaves a null entry.
        public IReadOnlyList<Obstacle> Items => _items;
        public int AliveCount { get; private set; }

        public Obstacle Get(EntityHandle handle) => handle.Kind == EntityKind.Obstacle && handle.Index >= 0 &&
            handle.Index < _items.Count && _items[handle.Index]?.Handle.Generation == handle.Generation
                ? _items[handle.Index] : null;

        public Obstacle Find(EntityId id) => _world.Entities.TryGet(id, out var handle) ? Get(handle) : null;

        // A mode's addition at stage 1. The ID takes the next obstacle number, so it never repeats in the battle.
        public Obstacle Add(ObstacleSetup setup)
        {
            var index = _items.IndexOf(null);
            if (index < 0)
            {
                index = _items.Count;
                _items.Add(null);
                _generations.Add(0);
            }

            var id = SpawnIdentity.CreateObstacle(_world.BattleId, _nextNumber++);
            var handle = new EntityHandle(EntityKind.Obstacle, index, ++_generations[index]);
            if (!_world.TryRegister(id, handle)) return null;

            var physics = _world.Physics;
            var body = physics.CreateBody(new BodyDef
            {
                Position = setup.Position,
                Angle = setup.Angle,
                Mass = setup.Mass,
                Flags = BodyFlags.FixedPosition | BodyFlags.FixedRotation,
            });
            var shapeDef = ShapeDef.Default;
            shapeDef.Filter = CollisionFilters.Obstacle(setup.Allegiance);
            var shapes = new ShapeId[setup.Parts.Count];
            for (var i = 0; i < shapes.Length; ++i)
            {
                shapes[i] = physics.AddPolygon(body, setup.Parts[i], shapeDef);
                _world.Shapes.Register(shapes[i], new ShapeEntry(handle, id, ShapeHitPriority.Hull, i));
            }

            var obstacle = new Obstacle
            {
                Id = id,
                Handle = handle,
                Setup = setup,
                Body = body,
                Shapes = shapes,
                Owner = _world.Setup.Units.Count > 0 ? _world.Setup.Units[0].Owner : 0,
            };
            _items[index] = obstacle;
            ++AliveCount;
            return obstacle;
        }

        // A mode's removal at stage 1; stage 12 removes it.
        public bool Remove(EntityId id)
        {
            var obstacle = Find(id);
            if (obstacle == null) return false;
            obstacle.PendingRemoval = true;
            return true;
        }

        // Stage 12, in storage order. No event: the snapshot no longer lists it.
        public void RemoveFinished(Battle battle)
        {
            for (var i = 0; i < _items.Count; ++i)
            {
                var obstacle = _items[i];
                if (obstacle == null || !obstacle.PendingRemoval) continue;

                foreach (var shape in obstacle.Shapes)
                    _world.Shapes.Release(shape);
                battle.ForgetContacts(obstacle.Id);
                _world.Physics.DestroyBody(obstacle.Body);
                _world.Entities.Remove(obstacle.Id);
                _items[i] = null;
                --AliveCount;
            }
        }

        private readonly BattleWorld _world;
        private readonly List<Obstacle> _items = new List<Obstacle>();

        // By storage entry: the generation of its last obstacle.
        private readonly List<int> _generations = new List<int>();

        // The number of the next obstacle ID (SpawnIdentity.CreateObstacle).
        private uint _nextNumber;
    }
}
