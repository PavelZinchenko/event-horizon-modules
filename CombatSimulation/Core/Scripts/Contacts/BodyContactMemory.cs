using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // The last body each ship's hull touched (legacy Collider.LastCollision and ActiveCollision; phase 5, step 14c), for
    // the AI node BypassObstacles. Legacy sets the last collision in every solid enter and stay callback, in an order the
    // core cannot copy, so the core keeps one rule. A touch that began in this step wins, the smallest entity ID first.
    // Else the last contact stays while it touches. Else the touching body with the smallest ID takes its place. Without a
    // touch the last contact stays, as in legacy. Only hull contacts with a ship hull, a tail segment, a repair bot or an
    // asteroid count: a bullet is a legacy trigger, a shield is its own legacy unit, and a satellite shape has no entity.
    internal sealed class BodyContactMemory
    {
        // Stage 8, after the physics step.
        public void Update(PhysicsWorld world, ShapeTable shapes, ShipStore ships)
        {
            for (var i = 0; i < _touching.Count; ++i)
            {
                var ship = ships.Get(_touching[i]);
                if (ship != null) ship.TouchesLastBodyContact = false;
            }

            _touching.Clear();
            _entries.Clear();
            _index.Clear();

            var begins = world.BeginEvents;
            for (var i = 0; i < begins.Length; ++i)
            {
                Add(begins[i].ShapeA, begins[i].ShapeB, true, shapes, ships);
                Add(begins[i].ShapeB, begins[i].ShapeA, true, shapes, ships);
            }

            for (var i = 0; i < world.ContactCount; ++i)
            {
                ref readonly var contact = ref world.GetContact(i);
                if (!contact.Touching) continue;
                Add(contact.ShapeA, contact.ShapeB, false, shapes, ships);
                Add(contact.ShapeB, contact.ShapeA, false, shapes, ships);
            }

            for (var i = 0; i < _entries.Count; ++i)
            {
                var entry = _entries[i];
                var ship = ships.Get(entry.Ship);
                if (!entry.Began.IsNull) ship.LastBodyContact = entry.Began;
                else if (!entry.TouchesLast) ship.LastBodyContact = entry.Smallest;
                ship.TouchesLastBodyContact = true;
                _touching.Add(entry.Ship);
            }
        }

        private void Add(ShapeId own, ShapeId other, bool began, ShapeTable shapes, ShipStore ships)
        {
            if (!shapes.TryGet(own, out var ownEntry) || ownEntry.Priority != ShapeHitPriority.Hull) return;
            var ship = ships.Get(ownEntry.Owner);
            if (ship == null || !shapes.TryGet(other, out var otherEntry) || otherEntry.OwnerId == ship.Id) return;

            switch (otherEntry.Owner.Kind)
            {
                case EntityKind.Ship:
                    if (otherEntry.Priority != ShapeHitPriority.Hull) return;
                    break;
                case EntityKind.Segment:
                case EntityKind.RepairBot:
                case EntityKind.Asteroid:
                    break;
                default:
                    return;
            }

            if (!_index.TryGetValue(ship.Id, out var index))
            {
                index = _entries.Count;
                _index.Add(ship.Id, index);
                _entries.Add(new Entry { Ship = ship.Handle });
            }

            var entry = _entries[index];
            var id = otherEntry.OwnerId;
            if (began)
            {
                if (IsSmaller(id, entry.Began)) entry.Began = id;
            }
            else
            {
                if (id == ship.LastBodyContact) entry.TouchesLast = true;
                if (IsSmaller(id, entry.Smallest)) entry.Smallest = id;
            }
            _entries[index] = entry;
        }

        private static bool IsSmaller(EntityId id, EntityId current) => current.IsNull || id.Value < current.Value;

        private struct Entry
        {
            public EntityHandle Ship;
            public EntityId Began;
            public EntityId Smallest;
            public bool TouchesLast;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<EntityId, int> _index = new Dictionary<EntityId, int>();
        private readonly List<EntityHandle> _touching = new List<EntityHandle>();
    }
}
