using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One segment of a worm tail (legacy WormSegmentBase; phase 5, step 7d): a physics body on a hinge behind its parent,
    // the ship or the segment before it. Segment 1 is the head of the tail. A segment that loses its parent is detached:
    // it leaves the ship's chain and keeps only the segments behind it. Pooled; the entity outlives its ship when it is
    // detached, so it keeps the ship facts it needs.
    internal sealed class TailSegment
    {
        public const long NoTimer = long.MaxValue;

        public TailSegment(int index) => Index = index;

        public int Index { get; }
        public int Generation { get; private set; }
        public bool IsAlive { get; private set; }
        public EntityHandle Handle => new EntityHandle(EntityKind.Segment, Index, Generation);

        public EntityId Id { get; private set; }
        public EntityId ShipId { get; private set; }
        public int Side { get; private set; }
        public Allegiance Allegiance { get; set; }
        public int Owner { get; private set; }
        public int DeviceIndex { get; private set; }
        public WormTailDefinition Definition { get; private set; }

        // 1 for the head of the tail.
        public int Number { get; private set; }
        public bool IsFixed => Number <= WormTailDefinition.UnbreakableSegments;

        public BodyId Body { get; private set; }
        public ShapeId Shape { get; private set; }

        // The hinge to the parent; null when detached.
        public JointId Joint { get; set; }

        // Null handles: no parent segment (the head, or detached) or no next segment.
        public EntityHandle Parent { get; set; }
        public EntityHandle Next { get; set; }
        public bool Detached { get; set; }

        public float Scale { get; private set; }
        public float Mass { get; private set; }
        public float MaxHitPoints { get; private set; }
        public float HitPoints { get; set; }

        // Legacy WormSegmentDestroyable._recentDamage: a hit that brings it above the hit points tears the segment off.
        public float RecentDamage { get; set; }

        // The owner grows the next segment from this tick, while it has none and its place is free.
        public long NextGrowthTick { get; set; }

        // Legacy WormSegmentFactory spawn counter: a regrown segment and every segment after it grow at the slow delay.
        public bool GrowsSlowly { get; private set; }
        public bool HasGrown { get; set; }

        // The owner destroys a detached segment at this tick (legacy InitiateSelfDestruct).
        public long SelfDestructTick { get; set; }

        // Set at stage 10 or by the self-destruct timer; stage 12 removes the segment.
        public bool PendingDeath { get; set; }

        // Linear velocity at the end of stage 4 (ramming speed).
        public Vec2 PreStepVelocity { get; set; }

        internal void Allocate()
        {
            ++Generation;
            IsAlive = true;
        }

        internal void Initialize(EntityId id, ShipEntity ship, int deviceIndex, WormTailDefinition definition, int number,
            BodyId body, ShapeId shape, float scale, float mass, bool growsSlowly)
        {
            Id = id;
            ShipId = ship.Id;
            Side = ship.Side;
            Allegiance = ship.Allegiance;
            Owner = ship.Owner;
            DeviceIndex = deviceIndex;
            Definition = definition;
            Number = number;
            Body = body;
            Shape = shape;
            Joint = default;
            Parent = default;
            Next = default;
            Detached = false;
            Scale = scale;
            Mass = mass;
            MaxHitPoints = HitPoints = definition.Power * ship.Definition.Armor;
            RecentDamage = 0f;
            NextGrowthTick = NoTimer;
            GrowsSlowly = growsSlowly;
            HasGrown = false;
            SelfDestructTick = NoTimer;
            PendingDeath = false;
            PreStepVelocity = Vec2.Zero;
        }

        internal void Release()
        {
            IsAlive = false;
            Id = default;
            ShipId = default;
            Definition = null;
            Body = default;
            Shape = default;
            Joint = default;
            Parent = default;
            Next = default;
        }
    }

    // Pooled tail segments with a LIFO free list. Allocation order never affects public IDs.
    internal sealed class TailSegmentStore
    {
        private readonly List<TailSegment> _items = new List<TailSegment>();
        private readonly List<int> _free = new List<int>();

        public int AliveCount { get; private set; }
        public int Capacity => _items.Count;

        // The pooled entry at the storage index, alive or not.
        public TailSegment this[int index] => _items[index];

        public TailSegment Allocate()
        {
            TailSegment segment;
            if (_free.Count > 0)
            {
                var last = _free.Count - 1;
                segment = _items[_free[last]];
                _free.RemoveAt(last);
            }
            else
            {
                segment = new TailSegment(_items.Count);
                _items.Add(segment);
                if (_free.Capacity < _items.Count) _free.Capacity = _items.Capacity;
            }

            segment.Allocate();
            ++AliveCount;
            return segment;
        }

        public void Release(TailSegment segment)
        {
            segment.Release();
            _free.Add(segment.Index);
            --AliveCount;
        }

        // Returns null for a null, stale or foreign handle.
        public TailSegment Get(EntityHandle handle)
        {
            if (handle.Kind != EntityKind.Segment || handle.Index < 0 || handle.Index >= _items.Count)
                return null;

            var segment = _items[handle.Index];
            return segment.IsAlive && segment.Generation == handle.Generation ? segment : null;
        }
    }

    // One tail segment at the end of a completed tick, for the view.
    public readonly struct TailSegmentState
    {
        public readonly EntityId Id;
        public readonly EntityId ShipId;
        public readonly int Side;
        public readonly Allegiance Allegiance;
        public readonly int Owner;
        public readonly WormTailDefinition Definition;
        public readonly int Number;
        public readonly bool Detached;
        public readonly float Mass;

        // The legacy body scale: the view draws the segment sprite at it.
        public readonly float Scale;

        // Canonical torus coordinates and radians.
        public readonly Vec2 Position;
        public readonly float Angle;

        internal TailSegmentState(TailSegment segment, PhysicsWorld world)
        {
            Id = segment.Id;
            ShipId = segment.ShipId;
            Side = segment.Side;
            Allegiance = segment.Allegiance;
            Owner = segment.Owner;
            Definition = segment.Definition;
            Number = segment.Number;
            Detached = segment.Detached;
            Mass = segment.Mass;
            Scale = segment.Scale;
            Position = world.GetPosition(segment.Body);
            Angle = world.GetAngle(segment.Body);
        }
    }
}
