using System;

namespace CombatSimulation.Core
{
    // Legacy ContinuousCannon holds one beam while the key is held. Phase 5, step 4d: a shot beam device is legacy
    // CommonCannon with a beam controller; it fires as a cannon does and holds no beam.
    internal sealed class BeamDevice : ShipDevice
    {
        // Legacy TimeFromLastUse starts at 3600 s: the weapon is ready at once.
        private const long NeverUsed = long.MinValue / 2;

        public BeamDevice(BeamDefinition definition, int index) : base(index) => Definition = definition;

        public BeamDefinition Definition { get; }
        internal long StoppedTick => _stoppedTick;

        // A shot beam's last shot (legacy TimeFromLastUse = 0).
        internal long LastShotTick { get; private set; } = NeverUsed;

        internal override bool IsShotWeapon => Definition.IsShot;

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _handle = default;
            _active = false;
            _stoppedTick = NeverUsed;
            LastShotTick = NeverUsed;
        }

        // Legacy ContinuousCannon: Active is the held bit while the beam is attached, even in the tick the energy runs out.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            if (Definition.IsShot)
            {
                UpdateShot(context, ship);
                return;
            }

            if (_active)
            {
                IsActive = IsControlOn(ship);
                if (!IsActive || !ship.Energy.TryConsume(Definition.EnergyPerSecond * Battle.TickDuration))
                    Deactivate(context);
                return;
            }

            IsActive = IsControlOn(ship) && IsReady(ship, context.Tick);
            if (!IsActive || !ship.Energy.TryConsume(Definition.ActivationCost))
                return;

            ship.RecordBarrelShot(Definition.BarrelIndex, context.Tick);
            context.RequestSpawn(new SpawnRequest(ship, this));
        }

        // Legacy CommonCannon, as CannonDevice in its single mode: the held bit, the weapon cooldown, the barrel cooldown
        // and the energy cost, in that order; a failed check spends nothing.
        private void UpdateShot(DeviceContext context, ShipEntity ship)
        {
            IsActive = false;
            var tick = context.Tick;
            if (!IsControlOn(ship)) return;
            if (tick - LastShotTick < Definition.CooldownTicks) return;
            if (!ship.IsBarrelReady(Definition.BarrelIndex, tick)) return;
            if (!ship.Energy.TryConsume(Definition.ActivationCost)) return;

            IsActive = true;
            LastShotTick = tick;
            ship.RecordBarrelShot(Definition.BarrelIndex, tick);
            context.RequestSpawn(new SpawnRequest(ship, this));
        }

        // A confirmed shot beam at tick T re-syncs the copy's cooldown phase, as CannonDevice.ConfirmShot does.
        internal void ConfirmShot(long tick)
        {
            if (Definition.IsShot && LastShotTick < tick + Definition.CooldownTicks) LastShotTick = tick;
        }

        // Legacy ContinuousCannon.CanBeActivated has no energy check; CommonCannon's has.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick)
        {
            var barrel = DeviceState.Remaining(tick + 1 - ship.GetBarrelShotTick(Definition.BarrelIndex),
                ShipEntity.BarrelCooldownTicks);
            if (Definition.IsShot)
            {
                var shot = DeviceState.Remaining(tick + 1 - LastShotTick, Definition.CooldownTicks);
                var ready = shot == 0f && barrel == 0f && ship.Energy.CanConsume(Definition.ActivationCost);
                return new DeviceState(ready, IsActive, Math.Max(shot, barrel));
            }

            var weapon = _active ? 0f : DeviceState.Remaining(tick + 1 - _stoppedTick, Definition.CooldownTicks);
            var beam = _active && context.Bullets.TryGet(_handle, out var index) ? context.Bullets[index].Id : default;
            return new DeviceState(_active || IsReady(ship, tick + 1), IsActive, Math.Max(weapon, barrel), activeBullet: beam);
        }

        private bool IsReady(ShipEntity ship, long tick) =>
            tick - _stoppedTick >= Definition.CooldownTicks && ship.IsBarrelReady(Definition.BarrelIndex, tick);

        // A continuous beam's device holds the beam it created.
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

        public override void Deactivate(DeviceContext context)
        {
            if (!_active) return;
            context.Bullets.MarkRemoval(_handle, RemovalReason.Disposal);
            _active = false;
            _handle = default;
            _stoppedTick = context.Tick;
        }

        private EntityHandle _handle;
        private bool _active;
        private long _stoppedTick = NeverUsed;
    }
}
