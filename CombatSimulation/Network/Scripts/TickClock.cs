using System;

namespace CombatSimulation.Network
{
    // Monotonic timestamps are in seconds, from each device's own clock origin.
    // Synchronization assumes symmetric transit times; asymmetric bias is exposed, not hidden.
    public sealed class TickClock
    {
        public const double TicksPerSecond = 50;
        public const double ProbeInterval = 0.25;
        public bool IsReference { get; }
        public bool HasEstimate => IsReference || _sampleCount != 0;
        public bool IsStarted { get; private set; }
        public double RoundTripSeconds { get; private set; }
        public double OffsetSeconds { get; private set; }
        public double Rate { get; private set; } = 1;
        public double TickPosition { get; private set; }
        public double ErrorTicks { get; private set; }
        public double ResendSeconds => _sampleCount == 0 ? 0.2 : Math.Max(0.1, Math.Min(1, 2 * RoundTripSeconds));
        // For a packet sent before one the peer has acknowledged: it is probably lost, so wait one round trip, not two.
        public double FastResendSeconds => _sampleCount == 0 ? ResendSeconds : Math.Max(0.05, Math.Min(ResendSeconds, 1.25 * RoundTripSeconds));

        public TickClock(bool isReference) { IsReference = isReference; }

        // t1/t4: local send/receive; t2/t3: remote receive/send. Reject invalid samples atomically.
        public bool Observe(double t1, double t2, double t3, double t4)
        {
            if (!Finite(t1) || !Finite(t2) || !Finite(t3) || !Finite(t4) || t4 < t1 || t3 < t2) return false;
            var roundTrip = (t4 - t1) - (t3 - t2);
            var offset = ((t2 - t1) + (t3 - t4)) * 0.5;
            if (!Finite(roundTrip) || !Finite(offset) || roundTrip < 0) return false;
            RoundTripSeconds = _sampleCount == 0 ? roundTrip : RoundTripSeconds + (roundTrip - RoundTripSeconds) * 0.125;
            _samples[_sampleIndex] = new Sample { RoundTrip = roundTrip, Offset = offset };
            _sampleIndex = (_sampleIndex + 1) % _samples.Length;
            _sampleCount = Math.Min(_samples.Length, _sampleCount + 1);
            var best = _samples[(_sampleIndex + _samples.Length - 1) % _samples.Length];
            for (var i = 0; i < _sampleCount; ++i)
                if (_samples[i].RoundTrip < best.RoundTrip) best = _samples[i];
            OffsetSeconds = IsReference ? 0 : best.Offset;
            return true;
        }

        // Call once before simulation starts. Both peers receive the same reference-clock epoch.
        public void Start(double localTime, double referenceStartTime)
        {
            if (IsStarted || !HasEstimate) throw new InvalidOperationException("Clock must be synchronized and not started.");
            if (!Finite(localTime) || !Finite(referenceStartTime)) throw new ArgumentOutOfRangeException(nameof(localTime));
            var position = (localTime + OffsetSeconds - referenceStartTime) * TicksPerSecond;
            if (!Finite(position)) throw new ArgumentOutOfRangeException(nameof(referenceStartTime));
            _lastTime = localTime;
            _epoch = referenceStartTime;
            TickPosition = position;
            IsStarted = true;
        }

        public void Advance(double localTime)
        {
            if (!IsStarted) throw new InvalidOperationException("Start the clock before advancing it.");
            if (!Finite(localTime) || localTime < _lastTime) throw new ArgumentOutOfRangeException(nameof(localTime));
            TickPosition += (localTime - _lastTime) * TicksPerSecond * Rate;
            _lastTime = localTime;
            ErrorTicks = (localTime + OffsetSeconds - _epoch) * TicksPerSecond - TickPosition;
            Rate = IsReference ? 1 : Math.Max(0.98, Math.Min(1.02, 1 + ErrorTicks / TicksPerSecond));
        }

        public bool IsTickDue(long nextTick) => IsStarted && nextTick >= 0 && nextTick <= TickPosition;
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private struct Sample { internal double RoundTrip, Offset; }
        private readonly Sample[] _samples = new Sample[16];
        private int _sampleIndex, _sampleCount;
        private double _epoch, _lastTime;
    }
}
