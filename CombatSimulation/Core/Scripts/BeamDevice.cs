using System;

namespace CombatSimulation.Core
{
    internal sealed class BeamDevice : ShipDevice
    {
        public BeamDevice(BeamDefinition definition, int index) : base(index) => Definition = definition;

        public BeamDefinition Definition { get; }
        internal long StoppedTick => _stoppedTick;

        public override void Reset()
        {
            base.Reset();
            _handle = default;
            _active = false;
            _stoppedTick = long.MinValue / 2;
        }

        // Legacy ContinuousCannon: Active is the held bit while the beam is attached, even in the tick the energy runs out.
        public override void Update(Battle battle, ShipEntity ship)
        {
            if (_active)
            {
                IsActive = IsControlOn(ship);
                if (!IsActive || !ship.Energy.TryConsume(Definition.EnergyPerSecond * Battle.TickDuration))
                    Deactivate(battle);
                return;
            }

            IsActive = IsControlOn(ship) && IsReady(ship, battle.Tick);
            if (!IsActive || !ship.Energy.TryConsume(Definition.ActivationCost))
                return;

            ship.RecordBarrelShot(Definition.BarrelIndex, battle.Tick);
            battle.RequestSpawn(new SpawnRequest(ship, this));
        }

        // Legacy ContinuousCannon.CanBeActivated has no energy check.
        internal override DeviceState GetState(Battle battle, ShipEntity ship, long tick)
        {
            var weapon = _active ? 0f : DeviceState.Remaining(tick + 1 - _stoppedTick, Definition.CooldownTicks);
            var barrel = DeviceState.Remaining(tick + 1 - ship.GetBarrelShotTick(Definition.BarrelIndex),
                ShipEntity.BarrelCooldownTicks);
            var beam = _active && battle.Bullets.TryGet(_handle, out var index) ? battle.Bullets[index].Id : default;
            return new DeviceState(_active || IsReady(ship, tick + 1), IsActive, Math.Max(weapon, barrel), beam: beam);
        }

        private bool IsReady(ShipEntity ship, long tick) =>
            tick - _stoppedTick >= Definition.CooldownTicks && ship.IsBarrelReady(Definition.BarrelIndex, tick);

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
