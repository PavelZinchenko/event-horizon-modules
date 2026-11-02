using System;

namespace CombatSimulation.Core
{
    internal sealed class PointDefenseDevice : ShipDevice
    {
        public PointDefenseDevice(PointDefenseDefinition definition, int index) : base(index) =>
            Definition = definition;

        public PointDefenseDefinition Definition { get; }
        public bool Enabled { get; private set; }
        public long NextQueryTick { get; private set; }

        public override void Reset()
        {
            base.Reset();
            Enabled = false;
            NextQueryTick = 0;
            _started = false;
            _stoppedTick = long.MinValue / 2;
            _ship = null;
            _queryTick = 0;
        }

        public override void Update(Battle battle, ShipEntity ship)
        {
            if (!IsControlOn(ship))
            {
                IsActive = false;
                Deactivate(battle);
                return;
            }

            IsActive = IsReady(ship, battle.Tick);
            if (Enabled || !IsActive || !ship.Energy.TryConsume(Definition.EnergyCost)) return;

            Enabled = true;
            if (!_started)
            {
                NextQueryTick = battle.Tick + Definition.QueryCooldownTicks;
                _started = true;
            }
        }

        public override void Deactivate(Battle battle) => Disable(battle.Tick);

        // Legacy PointDefenseSystem.CanBeActivated: the activation cooldown, and enabled or the cost available.
        internal override DeviceState GetState(Battle battle, ShipEntity ship, long tick) =>
            new DeviceState(IsReady(ship, tick + 1), IsActive,
                DeviceState.Remaining(tick + 1 - _stoppedTick, Definition.ActivationCooldownTicks));

        // Enabled implies that the activation cooldown has passed.
        private bool IsReady(ShipEntity ship, long tick) => Enabled ||
            tick - _stoppedTick >= Definition.ActivationCooldownTicks && ship.Energy.CanConsume(Definition.EnergyCost);

        // The timers at the end of tick t (network plan, G1). Enabled and started are not sent: the copy derives
        // them from the owner's controls and its corrected energy.
        internal PointDefenseTimers Export(long tick)
        {
            var stop = tick - _stoppedTick;
            var query = tick - (NextQueryTick - Definition.QueryCooldownTicks);
            return new PointDefenseTimers(
                stop >= Definition.ActivationCooldownTicks - 1 ? PointDefenseTimers.Ready : (int)Math.Max(0L, stop),
                query >= Definition.QueryCooldownTicks - 1 ? PointDefenseTimers.Ready : (int)Math.Max(0L, query));
        }

        // A ready timer only stops blocking; an age sets the timer exactly. A query age means the device has started.
        internal void Import(long tick, in PointDefenseTimers timers)
        {
            _stoppedTick = timers.StopAge == PointDefenseTimers.Ready
                ? Math.Min(_stoppedTick, tick + 1 - Definition.ActivationCooldownTicks) : tick - timers.StopAge;
            if (timers.QueryAge == PointDefenseTimers.Ready) NextQueryTick = Math.Min(NextQueryTick, tick + 1);
            else
            {
                NextQueryTick = tick - timers.QueryAge + Definition.QueryCooldownTicks;
                _started = true;
            }
        }

        // An age is valid only below its ready threshold.
        internal bool IsValid(in PointDefenseTimers timers) =>
            (timers.StopAge == PointDefenseTimers.Ready ||
             (timers.StopAge >= 0 && timers.StopAge < Definition.ActivationCooldownTicks - 1)) &&
            (timers.QueryAge == PointDefenseTimers.Ready ||
             (timers.QueryAge >= 0 && timers.QueryAge < Definition.QueryCooldownTicks - 1));

        // Stage 6 captures the payment context. A successful zap delays the next query, not the other
        // candidates from this query. Empty queries and candidates skipped by the resolver spend nothing.
        public bool PrepareQuery(ShipEntity ship, long tick)
        {
            if (!Enabled || tick < NextQueryTick) return false;
            _ship = ship;
            _queryTick = tick;
            return true;
        }

        // Stage 9, after the resolver checked validity. No unpaid zap can invalidate a bullet.
        public bool TryPayForZap()
        {
            if (!Enabled) return false;
            if (!_ship.Energy.TryConsume(Definition.EnergyCost))
            {
                Disable(_queryTick);
                return false;
            }

            NextQueryTick = _queryTick + Definition.QueryCooldownTicks;
            return true;
        }

        private void Disable(long tick)
        {
            if (!Enabled) return;
            Enabled = false;
            _stoppedTick = tick;
        }

        private bool _started;
        private long _stoppedTick = long.MinValue / 2;
        private ShipEntity _ship;
        private long _queryTick;
    }
}
