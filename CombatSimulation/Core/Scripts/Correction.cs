using System;

namespace CombatSimulation.Core
{
    // A resource at the end of the batch tick on an 8-bit grid (network plan, G1). Full: the maximum, and no delay.
    // Otherwise Level / 256 of the maximum, and whether the recharge delay runs. Armor rounds up, so a living ship
    // never reads as empty; shield and energy round to the nearest step.
    public readonly struct ResourceLevel : IEquatable<ResourceLevel>
    {
        public const int Steps = 256;
        public static readonly ResourceLevel Full = new ResourceLevel(true, false, 0);

        public readonly bool IsFull;
        public readonly bool DelayRunning;
        public readonly byte Level;

        public ResourceLevel(bool isFull, bool delayRunning, byte level)
        {
            IsFull = isFull;
            DelayRunning = delayRunning;
            Level = level;
        }

        public static ResourceLevel Partial(byte level, bool delayRunning) => new ResourceLevel(false, delayRunning, level);

        // A resource with maximum 0 is always full. A delay runs only on a resource that recharges.
        internal static ResourceLevel Of(in ResourcePool pool, bool roundUp)
        {
            if (!(pool.Value < pool.Max)) return Full;
            var steps = pool.Value / (double)pool.Max * Steps;
            var level = roundUp ? Math.Ceiling(steps) : Math.Round(steps);
            return Partial((byte)Math.Max(0.0, Math.Min(Steps - 1, level)), pool.DelayRemaining > 0f && pool.RechargeRate > 0f);
        }

        internal float Value(float max) => IsFull ? max : Level * max / Steps;

        internal bool IsValidFor(in ResourcePool pool) =>
            (pool.Max > 0f || IsFull) && !(IsFull && DelayRunning) && !(DelayRunning && !(pool.RechargeRate > 0f));

        public bool Equals(ResourceLevel other) => IsFull == other.IsFull && DelayRunning == other.DelayRunning && Level == other.Level;
        public override bool Equals(object obj) => obj is ResourceLevel other && Equals(other);
        public override int GetHashCode() => (IsFull ? 512 : 0) | (DelayRunning ? 256 : 0) | Level;
    }

    // The timers of one point-defense device at the batch tick t, as ages in ticks: since the activation stop, and
    // since the last query start (a zap or the first activation). Ready: the timer no longer blocks any tick after t.
    // Other devices send nothing: cannon, beam and barrel timers follow fire confirmations and removals, and a
    // drone bay runs only on its owner.
    public readonly struct PointDefenseTimers : IEquatable<PointDefenseTimers>
    {
        public const int Ready = -1;
        public static readonly PointDefenseTimers AllReady = new PointDefenseTimers(Ready, Ready);

        public readonly int StopAge;
        public readonly int QueryAge;

        public PointDefenseTimers(int stopAge, int queryAge)
        {
            StopAge = stopAge;
            QueryAge = queryAge;
        }

        public bool Equals(PointDefenseTimers other) => StopAge == other.StopAge && QueryAge == other.QueryAge;
        public override bool Equals(object obj) => obj is PointDefenseTimers other && Equals(other);
        public override int GetHashCode() => StopAge * 397 ^ QueryAge;
    }

    // The owner's state of one ship or drone at the end of the batch tick (network plan, step 5 and G1).
    // Motion is additive on the receiver. Point-defense timers are a range of the batch array, in device order.
    // Retreat is a reliable owner operation, not part of a correction.
    public readonly struct BodyCorrection
    {
        public readonly EntityId EntityId;
        public readonly SpawnPose Motion;
        public readonly ResourceLevel Armor;
        public readonly ResourceLevel Shield;
        public readonly ResourceLevel Energy;
        public readonly int FirstTimer;
        public readonly int TimerCount;

        internal BodyCorrection(EntityId entityId, in SpawnPose motion, in ResourceLevel armor, in ResourceLevel shield,
            in ResourceLevel energy, int firstTimer, int timerCount)
        {
            EntityId = entityId;
            Motion = motion;
            Armor = armor;
            Shield = shield;
            Energy = energy;
            FirstTimer = firstTimer;
            TimerCount = timerCount;
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

        public ReadOnlySpan<PointDefenseTimers> GetTimers(in BodyCorrection body) =>
            new ReadOnlySpan<PointDefenseTimers>(_timers, body.FirstTimer, body.TimerCount);

        // Clears the batch and sets its header.
        public void Begin(BattleId battleId, int owner, long tick, ulong appliedHitSequence)
        {
            BattleId = battleId;
            Owner = owner;
            Tick = tick;
            AppliedHitSequence = appliedHitSequence;
            _bodyCount = _timerCount = _bodyTimers = 0;
        }

        public void Add(EntityId entityId, in SpawnPose motion, in ResourceLevel armor, in ResourceLevel shield,
            in ResourceLevel energy, ReadOnlySpan<PointDefenseTimers> timers)
        {
            for (var i = 0; i < timers.Length; ++i) AddTimers(timers[i]);
            CommitBody(entityId, motion, armor, shield, energy);
        }

        // The timers added since the last body belong to the next committed body.
        internal void AddTimers(in PointDefenseTimers timers)
        {
            if (_timerCount == _timers.Length) Array.Resize(ref _timers, _timers.Length * 2);
            _timers[_timerCount++] = timers;
        }

        internal void CommitBody(EntityId entityId, in SpawnPose motion, in ResourceLevel armor, in ResourceLevel shield,
            in ResourceLevel energy)
        {
            if (_bodyCount == _bodies.Length) Array.Resize(ref _bodies, _bodies.Length * 2);
            _bodies[_bodyCount++] = new BodyCorrection(entityId, motion, armor, shield, energy, _bodyTimers,
                _timerCount - _bodyTimers);
            _bodyTimers = _timerCount;
        }

        private BodyCorrection[] _bodies = new BodyCorrection[8];
        private PointDefenseTimers[] _timers = new PointDefenseTimers[8];
        private int _bodyCount;
        private int _timerCount;
        private int _bodyTimers;
    }
}
