using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    internal enum EntityKind : byte
    {
        Ship = 1,

        // Bullets and beams (BulletStore).
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

    // A remote ship's state at stage 5 of one tick, before any spawn: the muzzle state its owner fired from.
    internal readonly struct SpawnSample
    {
        public readonly long Tick;
        public readonly Vec2 Position;
        public readonly Rot Rotation;
        public readonly float Angle;
        public readonly Vec2 LinearVelocity;

        public SpawnSample(long tick, Vec2 position, Rot rotation, float angle, Vec2 linearVelocity)
        {
            Tick = tick;
            Position = position;
            Rotation = rotation;
            Angle = angle;
            LinearVelocity = linearVelocity;
        }
    }

    // The engine input of one tick at stage 4: no engine while retreating, else the engine its energy selects.
    internal readonly struct EngineInput
    {
        public readonly EngineDefinition Engine;
        public readonly float Throttle;
        public readonly bool HasCourse;
        public readonly float Course;

        public EngineInput(EngineDefinition engine, float throttle, bool hasCourse, float course)
        {
            Engine = engine;
            Throttle = throttle;
            HasCourse = hasCourse;
            Course = course;
        }
    }

    // A remote ship's received correction, held until its tick has run here. Arrays are reused across pool cycles.
    internal sealed class PendingCorrection
    {
        public bool IsSet;
        public long Tick;
        public ulong AppliedHitSequence;
        public BodyCorrection Body;
        public PointDefenseTimers[] Timers = System.Array.Empty<PointDefenseTimers>();

        public void Store(long tick, ulong appliedHitSequence, in BodyCorrection body,
            System.ReadOnlySpan<PointDefenseTimers> timers)
        {
            IsSet = true;
            Tick = tick;
            AppliedHitSequence = appliedHitSequence;
            Body = body;
            if (Timers.Length < timers.Length) Timers = new PointDefenseTimers[timers.Length];
            timers.CopyTo(Timers);
        }
    }

    // A pooled ship. Drones have a persistent bay instead of a roster slot.
    internal sealed class ShipEntity
    {
        public const long NoRetreat = -1;

        // Legacy ShipFactory weapon platform cooldown, shared by the weapons on one barrel.
        public const float BarrelCooldown = 0.04f;
        public static readonly int BarrelCooldownTicks = TickTime.After(BarrelCooldown);

        // Spawn samples kept for a remote ship (network plan, step 4; step 5 shares the 16-tick length).
        public const int SpawnHistoryLength = 16;

        // End-of-tick body states kept for a remote ship (network plan, step 5).
        public const int BodyHistoryLength = 16;

        public const long NoCorrection = long.MinValue;

        public ShipEntity(int index) => Index = index;

        public int Index { get; }
        public int Generation { get; private set; }
        public bool IsAlive { get; private set; }
        public EntityHandle Handle => new EntityHandle(EntityKind.Ship, Index, Generation);

        public EntityId Id { get; private set; }
        public RosterSlot Slot { get; private set; }
        public DroneBayInventory Bay { get; private set; }
        public bool IsDrone => Bay != null;
        public EntityId MothershipId => Bay?.Slot.ActiveShip?.Id ?? default;
        public long LastDroneLaunchTick { get; set; }
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

        // Damage accumulated at stage 9, by damage type (D4); stage 10 applies it in one pass.
        public float[] PendingDamage => _pendingDamage;
        public bool HasPendingDamage { get; private set; }

        public void AddPendingDamage(int type, float amount)
        {
            if (!(amount > 0f)) return;
            _pendingDamage[type] = (float)System.Math.Min(float.MaxValue, (double)_pendingDamage[type] + amount);
            HasPendingDamage = true;
        }

        public void ClearPendingDamage()
        {
            if (!HasPendingDamage) return;
            System.Array.Clear(_pendingDamage, 0, _pendingDamage.Length);
            HasPendingDamage = false;
        }

        // The owner tick of the last applied correction, or NoCorrection.
        public long LastCorrectionTick { get; set; }
        public PendingCorrection Correction { get; } = new PendingCorrection();

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
        internal void Initialize(EntityId id, RosterSlot slot, BodyId body, long tick, int damageTypes)
            => Initialize(id, slot, null, slot.Setup.Ship, slot.Side, slot.Owner, body, tick, damageTypes);

        internal void InitializeDrone(EntityId id, DroneBayInventory bay, BodyId body, long tick, int damageTypes)
            => Initialize(id, null, bay, bay.Definition.Drone, bay.Slot.Side, bay.Slot.Owner, body, tick, damageTypes);

        private void Initialize(EntityId id, RosterSlot slot, DroneBayInventory bay, ShipDefinition definition,
            int side, int owner, BodyId body, long tick, int damageTypes)
        {
            Id = id;
            Slot = slot;
            Bay = bay;
            Side = side;
            Owner = owner;
            Body = body;
            Definition = definition;
            LastDroneLaunchTick = tick - 1;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
            PreStepVelocity = Vec2.Zero;
            if (_pendingDamage.Length != damageTypes) _pendingDamage = new float[damageTypes];
            else System.Array.Clear(_pendingDamage, 0, damageTypes);
            HasPendingDamage = false;
            _spawnSampleCount = 0;
            _bodySampleCount = 0;
            LastCorrectionTick = NoCorrection;
            Correction.IsSet = false;

            Armor = ResourcePool.Create(Definition.ArmorResource);
            Shield = ResourcePool.Create(Definition.Shield);
            Energy = ResourcePool.Create(Definition.Energy);

            // As legacy ShipInfo.Create: the deficit is a spend, so it starts the recharge delay.
            if (slot != null && slot.ArmorDeficit > 0f) Armor.Consume(slot.ArmorDeficit);
            if (slot != null && slot.ShieldDeficit > 0f) Shield.Consume(slot.ShieldDeficit);

            EngineHasEnergy = false;
            ClearControls();

            // Legacy FixedPlatform starts its shot timer at zero: a barrel is ready BarrelCooldownTicks after
            // the tick before deployment, as if it fired then.
            var barrels = Definition.Barrels.Count;
            if (_barrelShotTicks.Length < barrels) _barrelShotTicks = new long[barrels];
            for (var i = 0; i < barrels; ++i) _barrelShotTicks[i] = tick - 1;

            if (!_deviceSets.TryGetValue(definition, out _devices))
            {
                var devices = definition.Devices;
                _devices = devices.Count == 0 ? System.Array.Empty<ShipDevice>() : new ShipDevice[devices.Count];
                for (var i = 0; i < _devices.Length; ++i) _devices[i] = devices[i].CreateDevice(i);
                _deviceSets.Add(definition, _devices);
            }
            for (var i = 0; i < _devices.Length; ++i) _devices[i].Reset();
        }

        // In control-bit order. Deployment creates or resets the cached devices for its definition.
        public int DeviceCount => _devices.Length;
        public ShipDevice GetDevice(int index) => _devices[index];

        // Legacy FixedPlatform.IsReady: more than 0.04 s since the barrel's last shot, a strict check.
        public bool IsBarrelReady(int barrel, long tick) => tick - _barrelShotTicks[barrel] >= BarrelCooldownTicks;
        internal void RecordBarrelShot(int barrel, long tick) => _barrelShotTicks[barrel] = tick;
        internal long GetBarrelShotTick(int barrel) => _barrelShotTicks[barrel];

        internal void ApplyControls(in ControlFrame frame)
        {
            Throttle = frame.Throttle;
            HasCourse = frame.HasCourse;
            Course = frame.Course;
            Systems = frame.Systems;
        }

        // Stage 1, before this tick's frames.
        internal void BeginControlTick() => PreviousSystems = Systems;

        // Stage 5, once per tick in increasing tick order, so the kept samples cover consecutive ticks.
        internal void RecordSpawnSample(in SpawnSample sample)
        {
            _spawnSamples[(int)(sample.Tick % SpawnHistoryLength)] = sample;
            _newestSpawnSample = sample.Tick;
            if (_spawnSampleCount < SpawnHistoryLength) ++_spawnSampleCount;
        }

        // The sample of the tick, or the nearest kept one when the tick is outside the history. False without samples.
        internal bool TryGetSpawnSample(long tick, out SpawnSample sample)
        {
            if (_spawnSampleCount == 0)
            {
                sample = default;
                return false;
            }

            var oldest = _newestSpawnSample - _spawnSampleCount + 1;
            if (tick < oldest) tick = oldest;
            else if (tick > _newestSpawnSample) tick = _newestSpawnSample;
            sample = _spawnSamples[(int)(tick % SpawnHistoryLength)];
            return true;
        }

        // Stage 4: the engine input of this tick, kept with the body sample at stage 14.
        internal EngineInput StepEngineInput { get; set; }

        // Stage 14, once per tick in increasing tick order: the state an owner correction for this tick describes,
        // and the engine input that produced it.
        internal void RecordBodySample(long tick, in SpawnPose state)
        {
            _bodySamples[(int)(tick % BodyHistoryLength)] = state;
            _engineInputs[(int)(tick % BodyHistoryLength)] = StepEngineInput;
            _newestBodySample = tick;
            if (_bodySampleCount < BodyHistoryLength) ++_bodySampleCount;
        }

        // Only an exact tick inside the history; a correction older than it skips the movement part.
        internal bool TryGetBodySample(long tick, out SpawnPose state)
        {
            if (!HasBodySample(tick))
            {
                state = default;
                return false;
            }

            state = _bodySamples[(int)(tick % BodyHistoryLength)];
            return true;
        }

        private bool HasBodySample(long tick) =>
            _bodySampleCount > 0 && tick <= _newestBodySample && tick > _newestBodySample - _bodySampleCount;

        // The engine input of a tick inside the body history.
        internal EngineInput GetEngineInput(long tick) => _engineInputs[(int)(tick % BodyHistoryLength)];

        // C1: an applied correction moves each kept state by the error the re-run gives for its tick. A later
        // correction then measures only the error that remains. Ticks outside the history are skipped.
        internal void ShiftBodySample(long tick, in Torus torus, Vec2 position, float angle, Vec2 linearVelocity,
            float angularVelocity)
        {
            if (!HasBodySample(tick)) return;
            ref var state = ref _bodySamples[(int)(tick % BodyHistoryLength)];
            state = new SpawnPose(torus.Wrap(state.Position + position), state.Angle + angle,
                state.LinearVelocity + linearVelocity, state.AngularVelocity + angularVelocity);
        }

        internal void ShiftSpawnSample(long tick, in Torus torus, Vec2 position, float angle, Vec2 linearVelocity)
        {
            if (_spawnSampleCount == 0 || tick > _newestSpawnSample || tick <= _newestSpawnSample - _spawnSampleCount) return;
            ref var sample = ref _spawnSamples[(int)(tick % SpawnHistoryLength)];
            var shifted = sample.Angle + angle;
            sample = new SpawnSample(sample.Tick, torus.Wrap(sample.Position + position), Rot.FromAngle(shifted),
                shifted, sample.LinearVelocity + linearVelocity);
        }

        internal void Release()
        {
            IsAlive = false;
            Id = default;
            Slot = null;
            Bay = null;
            Body = default;
            Definition = null;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
            PreStepVelocity = Vec2.Zero;
            ClearPendingDamage();
            _spawnSampleCount = 0;
            _bodySampleCount = 0;
            System.Array.Clear(_engineInputs, 0, _engineInputs.Length);
            StepEngineInput = default;
            LastCorrectionTick = NoCorrection;
            Correction.IsSet = false;
            ShapeCount = 0;
            Armor = default;
            Shield = default;
            Energy = default;
            EngineHasEnergy = false;
            _devices = System.Array.Empty<ShipDevice>();
            ClearControls();
        }

        private ShapeId[] _shapes = new ShapeId[1];
        private float[] _pendingDamage = System.Array.Empty<float>();
        private long[] _barrelShotTicks = System.Array.Empty<long>();
        private ShipDevice[] _devices = System.Array.Empty<ShipDevice>();
        private readonly SpawnSample[] _spawnSamples = new SpawnSample[SpawnHistoryLength];
        private int _spawnSampleCount;
        private long _newestSpawnSample;
        private readonly SpawnPose[] _bodySamples = new SpawnPose[BodyHistoryLength];
        private readonly EngineInput[] _engineInputs = new EngineInput[BodyHistoryLength];
        private int _bodySampleCount;
        private long _newestBodySample;
        private readonly System.Collections.Generic.Dictionary<ShipDefinition, ShipDevice[]> _deviceSets =
            new System.Collections.Generic.Dictionary<ShipDefinition, ShipDevice[]>();

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
