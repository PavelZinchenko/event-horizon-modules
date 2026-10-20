using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // After time, kind, attacker and ray distance: a shield shape sorts before a hull shape.
    internal enum ShapeHitPriority : byte
    {
        Shield = 0,
        Hull = 1,
    }

    internal readonly struct ShapeEntry
    {
        public readonly EntityHandle Owner;
        public readonly EntityId OwnerId;
        public readonly ShapeHitPriority Priority;

        // Final contact tie-breaker: order within the owner's definition, never a physics index.
        public readonly int Order;

        public ShapeEntry(EntityHandle owner, EntityId ownerId, ShapeHitPriority priority, int order)
        {
            Owner = owner;
            OwnerId = ownerId;
            Priority = priority;
            Order = order;
        }
    }

    // Maps each live physics shape to its entity. Physics has no user data, so the core keeps this table.
    // A lookup checks the shape generation: a stale ShapeId (a delayed end event of a destroyed body,
    // or a reused index) resolves to nothing.
    internal sealed class ShapeTable
    {
        public void Register(ShapeId shape, in ShapeEntry entry)
        {
            if (shape.Index >= _generations.Length)
            {
                var size = Math.Max(shape.Index + 1, _generations.Length * 2);
                Array.Resize(ref _generations, size);
                Array.Resize(ref _entries, size);
            }

            _generations[shape.Index] = shape.Generation;
            _entries[shape.Index] = entry;
        }

        public void Release(ShapeId shape)
        {
            if (!Matches(shape)) return;
            _generations[shape.Index] = 0;
            _entries[shape.Index] = default;
        }

        public bool TryGet(ShapeId shape, out ShapeEntry entry)
        {
            if (Matches(shape))
            {
                entry = _entries[shape.Index];
                return true;
            }

            entry = default;
            return false;
        }

        private bool Matches(ShapeId shape) =>
            !shape.IsNull && shape.Index >= 0 && shape.Index < _generations.Length &&
            _generations[shape.Index] == shape.Generation;

        private int[] _generations = new int[16];
        private ShapeEntry[] _entries = new ShapeEntry[16];
    }
}
