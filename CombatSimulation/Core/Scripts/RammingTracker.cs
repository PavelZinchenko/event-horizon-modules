using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Ramming (Q8). A touch between two enemy ships begins with the first begin event of any of their shape pairs
    // and ends after the last end event. Damage applies once per ship pair per touch, when the count of touching
    // shape pairs goes from 0 to 1, not once per shape pair.
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
        public void Collect(PhysicsWorld world, ShapeTable shapes, ShipStore ships, ContactResolver resolver, int damageType)
        {
            _started.Clear();

            var begins = world.BeginEvents;
            for (var i = 0; i < begins.Length; ++i)
            {
                if (!TryResolve(begins[i], shapes, ships, out var a, out var entryA, out var b, out var entryB)) continue;

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
                if (!TryResolve(ends[i], shapes, ships, out var a, out _, out var b, out _)) continue;

                var pair = EntityPair.Unordered(a.Id, b.Id);
                if (!_touching.TryGetValue(pair, out var count)) continue;
                if (count <= 1) _touching.Remove(pair);
                else _touching[pair] = count - 1;
            }

            for (var i = 0; i < _started.Count; ++i)
            {
                var touch = _started[i];
                var first = ships.Get(touch.FirstShape.Owner);
                var second = ships.Get(touch.SecondShape.Owner);

                var relativeSpeed = (first.PreStepVelocity - second.PreStepVelocity).Length;
                var mass = Math.Min(world.GetMass(first.Body), world.GetMass(second.Body));
                AddCandidate(resolver, first, second, touch.SecondShape, relativeSpeed, mass, damageType);
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

        private static void AddCandidate(ContactResolver resolver, ShipEntity attacker, ShipEntity target,
            in ShapeEntry targetShape, float relativeSpeed, float mass, int damageType)
        {
            var damage = DamageFactor * relativeSpeed * mass * attacker.Definition.RammingDamageMultiplier;
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

        // Both shapes belong to live enemy ships.
        private static bool TryResolve(in ContactEvent contact, ShapeTable shapes, ShipStore ships,
            out ShipEntity a, out ShapeEntry entryA, out ShipEntity b, out ShapeEntry entryB)
        {
            a = b = null;
            if (!shapes.TryGet(contact.ShapeA, out entryA) | !shapes.TryGet(contact.ShapeB, out entryB)) return false;

            a = ships.Get(entryA.Owner);
            b = ships.Get(entryB.Owner);
            return a != null && b != null && a != b && a.Side != b.Side;
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
