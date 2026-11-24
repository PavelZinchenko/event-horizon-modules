using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Ramming (Q8). A touch between two enemy ships begins with the first begin event of any of their shape pairs
    // and ends after the last end event. Damage applies once per ship pair per touch, when the count of touching
    // shape pairs goes from 0 to 1, not once per shape pair. A ship also rams a hostile worm tail segment; a segment
    // deals no ramming damage (legacy WormSegmentBase has no collision behaviour; phase 5, step 7d). An asteroid rams and
    // is rammed as a ship, with its own multiplier (legacy DefaultCollisionBehaviour; step 14). Two asteroids are friends.
    // A repair bot and a destructible satellite are rammed and deal no ramming damage. Frontier's island rams as an
    // asteroid and is never rammed (legacy StaticPlatform ignores its collisions).
    internal sealed class RammingTracker
    {
        // Legacy DefaultCollisionBehaviour: 0.1 × relative speed × the smaller mass × the attacker's multiplier,
        // applied only above 1.
        public const float DamageFactor = 0.1f;
        public const float MinDamage = 1f;

        public int TouchingPairCount => _touching.Count;

        public bool IsTouching(EntityId a, EntityId b) => _touching.ContainsKey(EntityPair.Unordered(a, b));

        // Stage 8. Begin events first, then end events: a pair that swaps one touching shape pair for another in
        // the same step stays touching. Adds one candidate per direction for each touch that started.
        public void Collect(PhysicsWorld world, ShapeTable shapes, ShipStore ships, TailSegmentStore segments,
            ContactResolver resolver, int damageType, RepairBots bots = null, Asteroids asteroids = null,
            SatelliteStore satellites = null, Obstacles obstacles = null)
        {
            _started.Clear();

            var begins = world.BeginEvents;
            for (var i = 0; i < begins.Length; ++i)
            {
                if (!TryResolve(begins[i], shapes, ships, segments, bots, asteroids, satellites, obstacles, out var a,
                        out var entryA, out var b, out var entryB))
                    continue;

                // Order the pair by entity ID, so the result does not depend on the physics shape order.
                if (a.Id.Value > b.Id.Value)
                {
                    (a, b) = (b, a);
                    (entryA, entryB) = (entryB, entryA);
                }

                var pair = new EntityPair(a.Id, b.Id);
                _touching.TryGetValue(pair, out var count);
                _touching[pair] = count + 1;

                if (count == 0) _started.Add(new StartedTouch(pair, entryA, entryB));
                else UpdateStarted(pair, entryA, entryB);
            }

            var ends = world.EndEvents;
            for (var i = 0; i < ends.Length; ++i)
            {
                // A stale shape (a removed ship) resolves to nothing; Forget already dropped its pairs.
                if (!TryResolve(ends[i], shapes, ships, segments, bots, asteroids, satellites, obstacles, out var a, out _,
                        out var b, out _))
                    continue;

                var pair = EntityPair.Unordered(a.Id, b.Id);
                if (!_touching.TryGetValue(pair, out var count)) continue;
                if (count <= 1) _touching.Remove(pair);
                else _touching[pair] = count - 1;
            }

            for (var i = 0; i < _started.Count; ++i)
            {
                var touch = _started[i];
                var first = Party.Of(touch.FirstShape.Owner, ships, segments, bots, asteroids, satellites, obstacles);
                var second = Party.Of(touch.SecondShape.Owner, ships, segments, bots, asteroids, satellites, obstacles);

                var relativeSpeed = (first.PreStepVelocity - second.PreStepVelocity).Length;
                var mass = Math.Min(world.GetMass(first.Body), world.GetMass(second.Body));
                // A shield has no collision behaviour (phase 5, step 10d, owner question 6): a ship that touches with its
                // shield deals no ramming damage; the rammer's damage on a shield pays the shield's energy.
                if (first.Rams && second.IsRammed && touch.FirstShape.Priority != ShapeHitPriority.Shield)
                    AddCandidate(resolver, first, second, touch.SecondShape, relativeSpeed, mass, damageType);
                if (second.Rams && first.IsRammed && touch.SecondShape.Priority != ShapeHitPriority.Shield)
                    AddCandidate(resolver, second, first, touch.FirstShape, relativeSpeed, mass, damageType);
            }
        }

        // On ship removal. A touch with a removed ship ends; its delayed end events are then ignored.
        public void Forget(EntityId id)
        {
            _scratch.Clear();
            foreach (var pair in _touching.Keys)
                if (pair.First == id || pair.Second == id)
                    _scratch.Add(pair);

            for (var i = 0; i < _scratch.Count; ++i)
                _touching.Remove(_scratch[i]);
        }

        private static void AddCandidate(ContactResolver resolver, in Party attacker, in Party target,
            in ShapeEntry targetShape, float relativeSpeed, float mass, int damageType)
        {
            var damage = DamageFactor * relativeSpeed * mass * attacker.RammingDamageMultiplier;
            if (!(damage > MinDamage)) return;

            resolver.Add(new ContactCandidate
            {
                Time = 1f,
                Kind = ContactKind.Hit,
                Attacker = attacker.Id,
                TargetPriority = targetShape.Priority,
                Target = target.Id,
                TargetShapeOrder = targetShape.Order,
                Source = ContactSource.BodyContact,
                AttackerHandle = attacker.Handle,
                TargetHandle = target.Handle,
                Damage = damage,
                DamageType = damageType,
            });
        }

        // The shapes belong to two live hostile entities, at least one of them a ship, an asteroid or an obstacle.
        private static bool TryResolve(in ContactEvent contact, ShapeTable shapes, ShipStore ships,
            TailSegmentStore segments, RepairBots bots, Asteroids asteroids, SatelliteStore satellites,
            Obstacles obstacles, out Party a, out ShapeEntry entryA, out Party b, out ShapeEntry entryB)
        {
            a = b = default;
            if (!shapes.TryGet(contact.ShapeA, out entryA) | !shapes.TryGet(contact.ShapeB, out entryB)) return false;

            a = Party.Of(entryA.Owner, ships, segments, bots, asteroids, satellites, obstacles);
            b = Party.Of(entryB.Owner, ships, segments, bots, asteroids, satellites, obstacles);
            return a.IsAlive && b.IsAlive && (a.Rams || b.Rams) && a.Id != b.Id &&
                   Allegiances.IsHostile(a.Allegiance, b.Allegiance);
        }

        // A ship, a tail segment, a repair bot, a destructible satellite, an asteroid or an obstacle in a touch.
        private readonly struct Party
        {
            public readonly ShipEntity Ship;
            public readonly TailSegment Segment;
            public readonly RepairBot Bot;
            public readonly Satellite Satellite;
            public readonly Asteroid Asteroid;

            public readonly Obstacle Obstacle;

            private Party(ShipEntity ship, TailSegment segment, RepairBot bot, Satellite satellite, Asteroid asteroid,
                Obstacle obstacle)
            {
                Ship = ship;
                Segment = segment;
                Bot = bot;
                Satellite = satellite;
                Asteroid = asteroid;
                Obstacle = obstacle;
            }

            public static Party Of(EntityHandle handle, ShipStore ships, TailSegmentStore segments, RepairBots bots,
                Asteroids asteroids, SatelliteStore satellites, Obstacles obstacles)
            {
                var ship = ships.Get(handle);
                return new Party(ship, ship == null ? segments.Get(handle) : null, bots?.Get(handle), satellites?.Get(handle),
                    asteroids?.Get(handle), obstacles?.Get(handle));
            }

            public bool IsAlive => Ship != null || Segment != null || Bot != null || Satellite != null || Asteroid != null ||
                                   Obstacle != null;

            // Only a ship, an asteroid and an obstacle have a collision behaviour that deals damage.
            public bool Rams => Ship != null || Asteroid != null || Obstacle != null;

            // An obstacle takes no damage.
            public bool IsRammed => Obstacle == null;

            public float RammingDamageMultiplier => Ship?.Definition.RammingDamageMultiplier ??
                Asteroid?.Setup.RammingDamageMultiplier ?? Obstacle.Setup.RammingDamageMultiplier;
            public EntityId Id => Ship?.Id ?? Segment?.Id ?? Bot?.Id ?? Satellite?.Id ?? Asteroid?.Id ?? Obstacle.Id;
            public EntityHandle Handle => Ship?.Handle ?? Segment?.Handle ?? Bot?.Handle ?? Satellite?.Handle ??
                Asteroid?.Handle ?? Obstacle.Handle;
            public Allegiance Allegiance => Ship?.Allegiance ?? Segment?.Allegiance ?? Bot?.Allegiance ??
                Satellite?.Allegiance ?? Asteroid?.Allegiance ?? Obstacle.Allegiance;
            public BodyId Body => Ship?.Body ?? Segment?.Body ?? Bot?.Body ?? Satellite?.Body ?? Asteroid?.Body ?? Obstacle.Body;

            // An obstacle never moves.
            public Vec2 PreStepVelocity => Ship?.PreStepVelocity ?? Segment?.PreStepVelocity ?? Bot?.PreStepVelocity ??
                Satellite?.PreStepVelocity ?? Asteroid?.PreStepVelocity ?? Vec2.Zero;
        }

        // Keeps the target shape with the smallest (priority, order) among the pair's begin events in this step.
        private void UpdateStarted(EntityPair pair, in ShapeEntry first, in ShapeEntry second)
        {
            for (var i = 0; i < _started.Count; ++i)
            {
                var touch = _started[i];
                if (!touch.Pair.Equals(pair)) continue;

                _started[i] = new StartedTouch(pair,
                    IsBefore(first, touch.FirstShape) ? first : touch.FirstShape,
                    IsBefore(second, touch.SecondShape) ? second : touch.SecondShape);
                return;
            }
        }

        private static bool IsBefore(in ShapeEntry a, in ShapeEntry b) =>
            a.Priority != b.Priority ? a.Priority < b.Priority : a.Order < b.Order;

        private readonly Dictionary<EntityPair, int> _touching = new Dictionary<EntityPair, int>();
        private readonly List<StartedTouch> _started = new List<StartedTouch>();
        private readonly List<EntityPair> _scratch = new List<EntityPair>();

        // The pair is ordered by entity ID; each shape belongs to the entity of the same position.
        private readonly struct StartedTouch
        {
            public readonly EntityPair Pair;
            public readonly ShapeEntry FirstShape;
            public readonly ShapeEntry SecondShape;

            public StartedTouch(EntityPair pair, in ShapeEntry firstShape, in ShapeEntry secondShape)
            {
                Pair = pair;
                FirstShape = firstShape;
                SecondShape = secondShape;
            }
        }
    }
}
