using System;

namespace CombatSimulation.Core
{
    // A recharging resource at the end of a tick (legacy Energy): value and seconds left before the recharge resumes.
    public readonly struct RechargeState
    {
        public readonly float Value;
        public readonly float DelayRemaining;

        public RechargeState(float value, float delayRemaining)
        {
            Value = value;
            DelayRemaining = delayRemaining;
        }
    }

    // The timers of one device. The fields mean, by device:
    // cannon: LastTick = last shot; beam: LastTick = last stop;
    // point defense: LastTick = last stop, NextTick = next query, Enabled, Started (the first query was scheduled).
    // A drone bay has no device state: its launch timer runs only on its owner.
    public readonly struct DeviceState
    {
        public readonly long LastTick;
        public readonly long NextTick;
        public readonly bool Enabled;
        public readonly bool Started;

        public DeviceState(long lastTick, long nextTick = 0, bool enabled = false, bool started = false)
        {
            LastTick = lastTick;
            NextTick = nextTick;
            Enabled = enabled;
            Started = started;
        }
    }

    // The owner's state of one ship or drone at the end of the batch tick (network plan, step 5).
    // Motion is additive on the receiver; the other values are set exactly. Device and barrel states are ranges
    // of the batch arrays, in definition order.
    public readonly struct BodyCorrection
    {
        public readonly EntityId EntityId;
        public readonly SpawnPose Motion;
        public readonly float Armor;
        public readonly RechargeState Shield;
        public readonly RechargeState Energy;
        // The absolute retreat completion tick, or -1 without a retreat.
        public readonly long RetreatCompletionTick;
        public readonly int FirstDevice;
        public readonly int DeviceCount;
        public readonly int FirstBarrel;
        public readonly int BarrelCount;

        internal BodyCorrection(EntityId entityId, in SpawnPose motion, float armor, in RechargeState shield,
            in RechargeState energy, long retreatCompletionTick, int firstDevice, int deviceCount, int firstBarrel,
            int barrelCount)
        {
            EntityId = entityId;
            Motion = motion;
            Armor = armor;
            Shield = shield;
            Energy = energy;
            RetreatCompletionTick = retreatCompletionTick;
            FirstDevice = firstDevice;
            DeviceCount = deviceCount;
            FirstBarrel = firstBarrel;
            BarrelCount = barrelCount;
        }
    }

    // One owner's corrections for one tick. AppliedHitSequence is the last hit sequence from the receiving owner
    // that the sender had applied by that tick. Reusable: the arrays keep their capacity after warm-up.
    public sealed class CorrectionBatch
    {
        public BattleId BattleId { get; private set; }
        public int Owner { get; private set; }
        public long Tick { get; private set; }
        public ulong AppliedHitSequence { get; private set; }

        public ReadOnlySpan<BodyCorrection> Bodies => new ReadOnlySpan<BodyCorrection>(_bodies, 0, _bodyCount);
        public ReadOnlySpan<DeviceState> Devices => new ReadOnlySpan<DeviceState>(_devices, 0, _deviceCount);
        public ReadOnlySpan<long> BarrelShotTicks => new ReadOnlySpan<long>(_barrels, 0, _barrelCount);

        public ReadOnlySpan<DeviceState> GetDevices(in BodyCorrection body) =>
            new ReadOnlySpan<DeviceState>(_devices, body.FirstDevice, body.DeviceCount);

        public ReadOnlySpan<long> GetBarrelShotTicks(in BodyCorrection body) =>
            new ReadOnlySpan<long>(_barrels, body.FirstBarrel, body.BarrelCount);

        // Clears the batch and sets its header.
        public void Begin(BattleId battleId, int owner, long tick, ulong appliedHitSequence)
        {
            BattleId = battleId;
            Owner = owner;
            Tick = tick;
            AppliedHitSequence = appliedHitSequence;
            _bodyCount = _deviceCount = _barrelCount = 0;
            _bodyDevices = _bodyBarrels = 0;
        }

        public void Add(EntityId entityId, in SpawnPose motion, float armor, in RechargeState shield,
            in RechargeState energy, long retreatCompletionTick, ReadOnlySpan<DeviceState> devices,
            ReadOnlySpan<long> barrelShotTicks)
        {
            for (var i = 0; i < devices.Length; ++i) AddDevice(devices[i]);
            for (var i = 0; i < barrelShotTicks.Length; ++i) AddBarrel(barrelShotTicks[i]);
            CommitBody(entityId, motion, armor, shield, energy, retreatCompletionTick);
        }

        // The devices and barrels added since the last body belong to the next committed body.
        internal void AddDevice(in DeviceState state)
        {
            if (_deviceCount == _devices.Length) Array.Resize(ref _devices, _devices.Length * 2);
            _devices[_deviceCount++] = state;
        }

        internal void AddBarrel(long shotTick)
        {
            if (_barrelCount == _barrels.Length) Array.Resize(ref _barrels, _barrels.Length * 2);
            _barrels[_barrelCount++] = shotTick;
        }

        internal void CommitBody(EntityId entityId, in SpawnPose motion, float armor, in RechargeState shield,
            in RechargeState energy, long retreatCompletionTick)
        {
            if (_bodyCount == _bodies.Length) Array.Resize(ref _bodies, _bodies.Length * 2);
            _bodies[_bodyCount++] = new BodyCorrection(entityId, motion, armor, shield, energy, retreatCompletionTick,
                _bodyDevices, _deviceCount - _bodyDevices, _bodyBarrels, _barrelCount - _bodyBarrels);
            _bodyDevices = _deviceCount;
            _bodyBarrels = _barrelCount;
        }

        private BodyCorrection[] _bodies = new BodyCorrection[8];
        private DeviceState[] _devices = new DeviceState[16];
        private long[] _barrels = new long[16];
        private int _bodyCount;
        private int _deviceCount;
        private int _barrelCount;
        private int _bodyDevices;
        private int _bodyBarrels;
    }
}
