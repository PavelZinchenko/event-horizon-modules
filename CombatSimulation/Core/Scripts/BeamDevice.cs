namespace CombatSimulation.Core
{
    internal sealed class BeamDevice : ShipDevice
    {
        public BeamDevice(BeamDefinition definition, int index) : base(index) => Definition = definition;

        public BeamDefinition Definition { get; }
        internal long StoppedTick => _stoppedTick;

        public override void Reset()
        {
            _handle = default;
            _active = false;
            _stoppedTick = long.MinValue / 2;
        }

        public override void Update(Battle battle, ShipEntity ship)
        {
            if (_active)
            {
                if (!IsControlOn(ship) || !ship.Energy.TryConsume(Definition.EnergyPerSecond * Battle.TickDuration))
                    Deactivate(battle);
                return;
            }

            if (!IsControlOn(ship) || battle.Tick - _stoppedTick < Definition.CooldownTicks ||
                !ship.IsBarrelReady(Definition.BarrelIndex, battle.Tick) ||
                !ship.Energy.TryConsume(Definition.ActivationCost))
                return;

            ship.RecordBarrelShot(Definition.BarrelIndex, battle.Tick);
            battle.RequestSpawn(new SpawnRequest(ship, this));
        }

        public void Attach(EntityHandle handle)
        {
            _handle = handle;
            _active = true;
        }

        // The attached beam, when active.
        public bool TryGetBeam(out EntityHandle handle)
        {
            handle = _handle;
            return _active;
        }

        // The beam left without the device stopping it: an owner removal (stop at the owner's removal tick) or an
        // unconfirmed remote beam. The stop starts the cooldown, as Deactivate does.
        public void Detach(EntityHandle handle, long tick)
        {
            if (!_active || _handle.Index != handle.Index || _handle.Generation != handle.Generation) return;
            _active = false;
            _handle = default;
            _stoppedTick = tick;
        }

        public override void Deactivate(Battle battle)
        {
            if (!_active) return;
            battle.Bullets.MarkRemoval(_handle, RemovalReason.Disposal);
            _active = false;
            _handle = default;
            _stoppedTick = battle.Tick;
        }

        private EntityHandle _handle;
        private bool _active;
        private long _stoppedTick = long.MinValue / 2;
    }
}
