using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    internal enum EntityKind : byte
    {
        Ship = 1,

        // Bullets and beams (struct store, step 6). The contact resolver already keys its walk state by them.
        Bullet = 2,
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
        public ShipDefinition Definition { get; private set; }

        // Resources are fields so that stages update them in place.
        public ResourcePool Armor;
        public ResourcePool Shield;
        public ResourcePool Energy;

        // Energy > 0 at the start of stage 2, before recharge; stage 4 picks the engine from it (legacy order).
        public bool EngineHasEnergy { get; set; }

        // Controls of the last applied frame. They stay in force until the next frame (commands, rule 3).
        public float Throttle { get; private set; }
        public bool HasCourse { get; private set; }
        public float Course { get; private set; }
        public ulong Systems { get; private set; }

        // Systems at the end of the previous tick. The core detects edges; the driver sends no events.
        public ulong PreviousSystems { get; private set; }
        public ulong PressedSystems => Systems & ~PreviousSystems;
        public ulong ReleasedSystems => PreviousSystems & ~Systems;

        // The absolute tick at which the retreat completes (tick stages, rule 5), or NoRetreat.
        public long RetreatCompletionTick { get; set; }

        // Set at stage 11; stage 12 applies it.
        public bool PendingDeath { get; set; }

        // Linear velocity at the end of stage 4, before the physics step (ramming speed).
        public Vec2 PreStepVelocity { get; set; }

        // Damage accumulated at stage 9; stage 10 applies it in one pass.
        public float PendingDamage { get; set; }

        // Hull shapes in definition order. The array is reused across pool cycles.
        public int ShapeCount { get; private set; }
        public ShapeId GetShape(int index) => _shapes[index];

        internal void AddShape(ShapeId shape)
        {
            if (ShapeCount == _shapes.Length) System.Array.Resize(ref _shapes, _shapes.Length * 2);
            _shapes[ShapeCount++] = shape;
        }

        internal void Allocate()
        {
            ++Generation;
            IsAlive = true;
        }

        // A deployment starts with empty controls and full resources, minus the deficits the slot retained (B4).
        internal void Initialize(EntityId id, RosterSlot slot, BodyId body)
        {
            Id = id;
            Slot = slot;
            Side = slot.Side;
            Owner = slot.Owner;
            Body = body;
            Definition = slot.Setup.Ship;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
            PreStepVelocity = Vec2.Zero;
            PendingDamage = 0f;

            Armor = ResourcePool.CreateArmor(Definition.Armor);
            Shield = ResourcePool.Create(Definition.Shield);
            Energy = ResourcePool.Create(Definition.Energy);

            // As legacy ShipInfo.Create: the deficit is a spend, so it starts the recharge delay.
            if (slot.ArmorDeficit > 0f) Armor.Consume(slot.ArmorDeficit);
            if (slot.ShieldDeficit > 0f) Shield.Consume(slot.ShieldDeficit);

            EngineHasEnergy = false;
            ClearControls();
        }

        internal void ApplyControls(in ControlFrame frame)
        {
            Throttle = frame.Throttle;
            HasCourse = frame.HasCourse;
            Course = frame.Course;
            Systems = frame.Systems;
        }

        // Stage 1, before this tick's frames.
        internal void BeginControlTick() => PreviousSystems = Systems;

        internal void Release()
        {
            IsAlive = false;
            Id = default;
            Slot = null;
            Body = default;
            Definition = null;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
            PreStepVelocity = Vec2.Zero;
            PendingDamage = 0f;
            ShapeCount = 0;
            Armor = default;
            Shield = default;
            Energy = default;
            EngineHasEnergy = false;
            ClearControls();
        }

        private ShapeId[] _shapes = new ShapeId[1];

        private void ClearControls()
        {
            Throttle = 0f;
            HasCourse = false;
            Course = 0f;
            Systems = 0;
            PreviousSystems = 0;
        }
    }
}
