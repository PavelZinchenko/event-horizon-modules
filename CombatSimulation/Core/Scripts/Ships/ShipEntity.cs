using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    internal enum EntityKind : byte
    {
        Ship = 1,

        // Bullets and beams (BulletStore).
        Bullet = 2,

        // Worm tail segments (TailSegmentStore; phase 5, step 7d).
        Segment = 3,
        RepairBot = 4,

        // Asteroids (Asteroids; phase 5, step 14).
        Asteroid = 5,
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

    // The engine input of one tick at stage 4: no engine while retreating, else the engine its energy selects. Phase 5,
    // step 9c: the accelerator's scale and the brake's deceleration of the tick's status.
    internal readonly struct EngineInput
    {
        public readonly EngineDefinition Engine;
        public readonly float Throttle;
        public readonly bool HasCourse;
        public readonly float Course;
        public readonly float Scale;
        public readonly float Deceleration;

        public EngineInput(EngineDefinition engine, float throttle, bool hasCourse, float course, float scale = 1f,
            float deceleration = 0f)
        {
            Engine = engine;
            Throttle = throttle;
            HasCourse = hasCourse;
            Course = course;
            Scale = scale;
            Deceleration = deceleration;
        }
    }

    // The effects on a ship besides damage that stage 9 sums and stage 10 applies with it (phase 5, step 5): the
    // shield drain takes the shield before it absorbs damage, the repair nets against the armor damage, and the energy
    // drain comes last (legacy ShipStats.ApplyDamage).
    internal struct PendingEffects
    {
        public float EnergyDrain;
        public float ShieldDrain;
        public float Repair;
        public float EnergyRecharge;
        public float ShieldRecharge;
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

    // A decoy's life (phase 5, step 10c): the device that launched it, its lifetime and its owner's pose corrections,
    // which use the missile correction format as repair bots do.
    internal sealed class DecoyLife
    {
        public DecoyDefinition Definition;
        public long SpawnTick;

        // Stage 11 of this tick marks the decoy's death (legacy elapsed >= lifetime).
        public long ExpiryTick;
        public long LastCorrectionTick, CorrectionTick, LastSentTick;
        public MissileCorrection Correction;

        // Legacy View.Life: 1 at the spawn, 0 at the expiry.
        public float Life(long tick) => ExpiryTick <= SpawnTick ? 0f :
            System.Math.Max(0f, System.Math.Min(1f, 1f - (float)(tick - SpawnTick) / (ExpiryTick - SpawnTick)));
    }

    // A pooled ship. Drones and clones have a persistent bay instead of a roster slot; a decoy has neither.
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

        // Null unless the entity is a decoy (phase 5, step 10c).
        public DecoyLife Decoy { get; private set; }
        public bool IsDecoy => Decoy != null;
        // Null override keeps the original bay association; an explicit null entity means no owner.
        // Capture changes this link, never the bay's inventory or network owner.
        public EntityId? MothershipOverride { get; set; }
        public EntityId MothershipId => MothershipOverride ?? Bay?.Slot.ActiveShip?.Id ?? default;
        public long LastDroneLaunchTick { get; set; }
        public int Side { get; private set; }

        // Hostility and collision (D13); the side stays the team.
        public Allegiance Allegiance { get; set; }
        public int Owner { get; private set; }
        public BodyId Body { get; private set; }
        public ShipDefinition Definition { get; private set; }
        // The definition's priority; a running stealth (stage 3, phase 5, step 9b) gives None, whatever the jammer
        // sets (owner question 4).
        public TargetPriority TargetPriority
        {
            get => (Status & ShipStatusFlags.Stealth) != 0 ? TargetPriority.None : _targetPriority;
            set => _targetPriority = value;
        }

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

        // Stage 3 (phase 5, step 7): the status of this tick's active devices. The devices it blocks stay blocked
        // through stage 2 of the next tick.
        public ShipStatusFlags Status { get; set; }
        public ulong BlockedDevices { get; set; }
        public float Tint { get; set; }
        public float ResistanceShare { get; set; }
        public float EngineScale { get; set; } = 1f;
        public float Deceleration { get; set; }

        // The control bits a device sees at stage 2.
        public ulong UsableSystems => Systems & ~BlockedDevices;

        public const long NoContact = long.MinValue;

        // Legacy Ship.OnCollision runs the OnHit triggers for every contact (phase 5, step 9b): the last tick in which
        // a local ship took one, from stage 9 or 10. A stealth device reads it at stage 2 of the next tick.
        public long LastContactTick { get; set; }

        // The stealth devices, and whether one of them ends at a contact; set at deployment.
        public ulong StealthDevices { get; private set; }
        public bool HasContactStealth { get; private set; }

        // Set at stage 11; stage 12 applies it.
        public bool PendingDeath { get; set; }

        // Set at stage 1 by a vanish command (D12); stage 12 removes the ship without a death.
        public bool PendingVanish { get; set; }

        // Linear velocity at the end of stage 4, before the physics step (ramming speed).
        public Vec2 PreStepVelocity { get; set; }

        // The last body the hull touched and whether it still touches it (legacy Collider.LastCollision and
        // ActiveCollision; phase 5, step 14c): stage 8 sets them (BodyContactMemory). The AI reads them.
        public EntityId LastBodyContact { get; set; }
        public bool TouchesLastBodyContact { get; set; }

        // Damage accumulated at stage 9, by damage type (D4), and the other effects; stage 10 applies them in one pass.
        public float[] PendingDamage => _pendingDamage;
        public float[] PendingDamageIgnoringShield => _pendingDamageIgnoringShield;
        public PendingEffects PendingEffects;
        public bool HasPendingDamage { get; private set; }

        public void AddPendingDamage(int type, float amount, bool ignoreShield = false)
        {
            if (!(amount > 0f)) return;
            var damage = ignoreShield ? _pendingDamageIgnoringShield : _pendingDamage;
            damage[type] = Sum(damage[type], amount);
            HasPendingDamage = true;
        }

        public void AddPendingDrain(float energy, float shield)
        {
            if (energy > 0f) PendingEffects.EnergyDrain = Sum(PendingEffects.EnergyDrain, energy);
            if (shield > 0f) PendingEffects.ShieldDrain = Sum(PendingEffects.ShieldDrain, shield);
            HasPendingDamage |= energy > 0f || shield > 0f;
        }

        public void AddPendingRecharge(float energy, float shield)
        {
            if (energy > 0f) PendingEffects.EnergyRecharge = Sum(PendingEffects.EnergyRecharge, energy);
            if (shield > 0f) PendingEffects.ShieldRecharge = Sum(PendingEffects.ShieldRecharge, shield);
            HasPendingDamage |= energy > 0f || shield > 0f;
        }

        public void AddPendingRepair(float amount)
        {
            if (!(amount > 0f)) return;
            PendingEffects.Repair = Sum(PendingEffects.Repair, amount);
            HasPendingDamage = true;
        }

        // A shield's pending impact (phase 5, step 10d) reaches the ship's pending damage at stage 10.
        public void MarkPendingImpact() => HasPendingDamage = true;

        public void ClearPendingDamage()
        {
            if (!HasPendingDamage) return;
            System.Array.Clear(_pendingDamage, 0, _pendingDamage.Length);
            System.Array.Clear(_pendingDamageIgnoringShield, 0, _pendingDamageIgnoringShield.Length);
            PendingEffects = default;
            HasPendingDamage = false;
        }

        private static float Sum(float a, float b) => (float)System.Math.Min(float.MaxValue, (double)a + b);

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
            => Initialize(id, null, bay, bay.Drone, bay.Slot.Side, bay.Slot.Owner, body, tick, damageTypes);

        // A decoy takes its parent's side, owner and allegiance.
        internal void InitializeDecoy(EntityId id, ShipEntity parent, DecoyDefinition definition, BodyId body, long tick,
            long expiryTick, int damageTypes)
        {
            Initialize(id, null, null, definition.Ship, parent.Side, parent.Owner, body, tick, damageTypes);
            Allegiance = parent.Allegiance;
            if (_decoy == null) _decoy = new DecoyLife();
            _decoy.Definition = definition;
            _decoy.SpawnTick = tick;
            _decoy.ExpiryTick = expiryTick;
            _decoy.LastCorrectionTick = _decoy.CorrectionTick = _decoy.LastSentTick = -1;
            _decoy.Correction = default;
            Decoy = _decoy;
        }

        private void Initialize(EntityId id, RosterSlot slot, DroneBayInventory bay, ShipDefinition definition,
            int side, int owner, BodyId body, long tick, int damageTypes)
        {
            Id = id;
            Slot = slot;
            Bay = bay;
            Decoy = null;
            MothershipOverride = null;
            Side = side;
            Allegiance = Allegiances.OfSide(side);
            Owner = owner;
            Body = body;
            Definition = definition;
            TargetPriority = definition.TargetPriority;
            LastDroneLaunchTick = tick - 1;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
            PendingVanish = false;
            Status = ShipStatusFlags.None;
            BlockedDevices = 0;
            LastContactTick = NoContact;
            LastBodyContact = default;
            TouchesLastBodyContact = false;
            Tint = 0f;
            ResistanceShare = 0f;
            EngineScale = 1f;
            Deceleration = 0f;
            PreStepVelocity = Vec2.Zero;
            if (_pendingDamage.Length != damageTypes) _pendingDamage = new float[damageTypes];
            else System.Array.Clear(_pendingDamage, 0, damageTypes);
            if (_pendingDamageIgnoringShield.Length != damageTypes) _pendingDamageIgnoringShield = new float[damageTypes];
            else System.Array.Clear(_pendingDamageIgnoringShield, 0, damageTypes);
            PendingEffects = default;
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
            if (_barrels.Length < barrels) System.Array.Resize(ref _barrels, barrels);
            for (var i = 0; i < barrels; ++i)
            {
                _barrelShotTicks[i] = tick - 1;
                if (_barrels[i] == null) _barrels[i] = new Barrel();
                _barrels[i].Reset(Definition.Barrels[i], tick);
            }
            ClearTurretRequests();

            // BattleWorld.AddSatellites creates the bodies once the ship has its pose.
            var satellites = Definition.Satellites.Count;
            if (_satellites.Length < satellites) System.Array.Resize(ref _satellites, satellites);
            for (var i = 0; i < satellites; ++i)
                if (_satellites[i] == null) _satellites[i] = new Satellite();

            if (!_deviceSets.TryGetValue(definition, out _devices))
            {
                var devices = definition.Devices;
                _devices = devices.Count == 0 ? System.Array.Empty<ShipDevice>() : new ShipDevice[devices.Count];
                for (var i = 0; i < _devices.Length; ++i) _devices[i] = devices[i].CreateDevice(i);
                _deviceSets.Add(definition, _devices);
            }
            StealthDevices = 0;
            HasContactStealth = false;
            for (var i = 0; i < _devices.Length; ++i)
            {
                _devices[i].Reset(tick);
                if (!(_devices[i] is StealthDevice stealth)) continue;
                StealthDevices |= 1UL << i;
                HasContactStealth |= stealth.Definition.EndsOnContact;
            }
        }

        // In control-bit order. Deployment creates or resets the cached devices for its definition.
        public int DeviceCount => _devices.Length;
        public ShipDevice GetDevice(int index) => _devices[index];

        // Legacy FixedPlatform.IsReady: more than 0.04 s since the barrel's last shot, a strict check. A barrel on an
        // inactive satellite is not ready (Frontier; built-in satellites stay active).
        public bool IsBarrelReady(int barrel, long tick) => tick - _barrelShotTicks[barrel] >= BarrelCooldownTicks &&
            (Definition.Barrels[barrel].Satellite < 0 || _satellites[Definition.Barrels[barrel].Satellite].ActiveTick <= tick);
        internal void RecordBarrelShot(int barrel, long tick) => _barrelShotTicks[barrel] = tick;
        internal long GetBarrelShotTick(int barrel) => _barrelShotTicks[barrel];

        private Barrel[] _barrels = System.Array.Empty<Barrel>();
        internal Barrel GetBarrel(int index) => _barrels[index];

        // One per Definition.Satellites entry (phase 5, step 12c).
        private Satellite[] _satellites = System.Array.Empty<Satellite>();
        internal Satellite GetSatellite(int index) => _satellites[index];
        internal EntityId TurretTarget;
        internal ulong TurretTargetSystems;
        internal ulong SearchTurretSystems;
        internal void ClearTurretRequests()
        {
            TurretTarget = default;
            TurretTargetSystems = SearchTurretSystems = 0;
        }

        internal void ApplyControls(in ControlFrame frame)
        {
            Throttle = frame.Throttle;
            HasCourse = frame.HasCourse;
            Course = frame.Course;
            Systems = frame.Systems;
            TurretTarget = frame.TurretTarget;
            TurretTargetSystems = frame.TurretTargetSystems;
            SearchTurretSystems = frame.SearchTurretSystems;
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
            Decoy = null;
            Body = default;
            Definition = null;
            RetreatCompletionTick = NoRetreat;
            PendingDeath = false;
            Status = ShipStatusFlags.None;
            BlockedDevices = 0;
            LastContactTick = NoContact;
            LastBodyContact = default;
            TouchesLastBodyContact = false;
            StealthDevices = 0;
            HasContactStealth = false;
            Tint = 0f;
            ResistanceShare = 0f;
            EngineScale = 1f;
            Deceleration = 0f;
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

        private TargetPriority _targetPriority;
        private DecoyLife _decoy;
        private ShapeId[] _shapes = new ShapeId[1];
        private float[] _pendingDamage = System.Array.Empty<float>();
        private float[] _pendingDamageIgnoringShield = System.Array.Empty<float>();
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
