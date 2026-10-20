using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    internal enum EntityKind : byte
    {
        Ship = 1,
    }

    // Internal storage handle: kind, index and generation. Never exposed as a public ID.
    internal readonly struct EntityHandle
    {
        public readonly EntityKind Kind;
        public readonly int Index;
        public readonly int Generation;

        public EntityHandle(EntityKind kind, int index, int generation)
        {
            Kind = kind;
            Index = index;
            Generation = generation;
        }
    }

    // A pooled ship. Drones will be ships without a roster slot (investigation 1.1).
    internal sealed class ShipEntity
    {
        public const long NoRetreat = -1;

        public ShipEntity(int index) => Index = index;

        public int Index { get; }
        public int Generation { get; private set; }
        public bool IsAlive { get; private set; }
        public EntityHandle Handle => new EntityHandle(EntityKind.Ship, Index, Generation);

        public EntityId Id { get; private set; }
        public RosterSlot Slot { get; private set; }
        public int Side { get; private set; }
        public int Owner { get; private set; }
        public BodyId Body { get; private set; }

        // The absolute tick at which the retreat completes (tick stages, rule 5), or NoRetreat.
        public long RetreatCompletionTick { get; set; }

        // Set at stage 11; stage 12 applies it.
        public bool PendingDeath { get; set; }

        internal void Allocate()
        {
            ++Generation;
            IsAlive = true;
        }

        internal void Initialize(EntityId id, RosterSlot slot, BodyId body)
        {
            Id = id;
            Slot = slot;
            Side = slot.Side;
            Owner = slot.Owner;
            Body = body;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
        }

        internal void Release()
        {
            IsAlive = false;
            Id = default;
            Slot = null;
            Body = default;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
        }
    }
}
