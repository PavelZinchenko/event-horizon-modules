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
                Deactivate(battle);
                return;
            }

            if (Enabled || battle.Tick - _stoppedTick < Definition.ActivationCooldownTicks ||
                !ship.Energy.TryConsume(Definition.EnergyCost)) return;

            Enabled = true;
            if (!_started)
            {
                NextQueryTick = battle.Tick + Definition.QueryCooldownTicks;
                _started = true;
            }
        }

        public override void Deactivate(Battle battle) => Disable(battle.Tick);

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
