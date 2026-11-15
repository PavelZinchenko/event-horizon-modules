using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Legacy ContinuousCannon holds one beam while the key is held. Phase 5, step 4d: a shot beam device is legacy
    // CommonCannon with a beam controller; it fires as a cannon does and holds no beam. Step 4f: a charged beam device
    // is legacy ChargeableCannon with a beam controller; it fires its charge at the release.
    internal sealed class BeamDevice : ShipDevice
    {
        // Legacy TimeFromLastUse starts at 3600 s: the weapon is ready at once.
        private const long NeverUsed = long.MinValue / 2;

        public BeamDevice(BeamDefinition definition, int index) : base(index)
        {
            Definition = definition;
            if (definition.IsCharged) _charge = new WeaponCharge(definition.ChargeTime, definition.ActivationCost);
        }

        public BeamDefinition Definition { get; }
        internal long StoppedTick => _stoppedTick;

        // A shot beam's last shot (legacy TimeFromLastUse = 0).
        internal long LastShotTick { get; private set; } = NeverUsed;

        // Legacy WarpDrive blocks a shot beam (a Common weapon), not a charged one.
        internal override bool IsShotWeapon => Definition.IsShot && !Definition.IsCharged;

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _handle = default;
            _active = false;
            _stoppedTick = NeverUsed;
            LastShotTick = NeverUsed;
            _shot = default;
            _charge?.Reset();
        }

        // A charged beam device's beam for a shot of the given charge ticks; the device definition otherwise.
        internal BeamDefinition BeamFor(int chargeTicks)
        {
            if (_charge == null || chargeTicks <= 0) return Definition;
            if (!_charged.TryGetValue(chargeTicks, out var beam))
                _charged.Add(chargeTicks, beam = Definition.WithCharge(_charge.LevelOf(chargeTicks)));
            return beam;
        }

        // Stage 5 created a shot beam of this device, or a copy took the owner's record of it. A charged beam device
        // shows it as its active bullet while it lives (legacy ChargeableCannon.ActiveBullet); CommonCannon shows none.
        internal void OnShotCreated(EntityHandle handle)
        {
            if (Definition.IsCharged) _shot = handle;
        }

        // Legacy ContinuousCannon: Active is the held bit while the beam is attached, even in the tick the energy runs out.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            if (Definition.IsCharged)
            {
                UpdateCharged(context, ship, IsControlOn(ship));
                return;
            }
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

        // Legacy ChargeableCannon.OnUpdatePhysics, as CannonDevice in its charging mode.
        private void UpdateCharged(DeviceContext context, ShipEntity ship, bool controlOn)
        {
            var tick = context.Tick;
            var shot = _charge.Update(ship, Definition.BarrelIndex, controlOn, tick);
            IsActive = _charge.Ticks > 0;
            if (shot == 0) return;

            ship.RecordBarrelShot(Definition.BarrelIndex, tick);
            context.RequestSpawn(new SpawnRequest(ship, this, shot));
        }

        // Legacy ShipRetreatEffect releases the key: a charged beam fires its charge.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship)
        {
            if (!Definition.IsCharged)
            {
                base.UpdateRetreating(context, ship);
                return;
            }
            UpdateCharged(context, ship, false);
        }

        // A confirmed shot beam at tick T re-syncs the copy's cooldown phase, as CannonDevice.ConfirmShot does. A charged
        // beam has no weapon cooldown.
        internal void ConfirmShot(long tick)
        {
            _charge?.EndAt(tick);
            if (Definition.IsShot && !Definition.IsCharged && LastShotTick < tick + Definition.CooldownTicks)
                LastShotTick = tick;
        }

        // The shortest spacing of two shots (the copy window of a confirmed shot).
        internal int ShotSpacingTicks => Definition.IsCharged ? WeaponCharge.ShotSpacingTicks
            : Math.Max(Definition.CooldownTicks, ShipEntity.BarrelCooldownTicks);

        // Legacy ContinuousCannon.CanBeActivated has no energy check; CommonCannon's has.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick)
        {
            var barrel = DeviceState.Remaining(tick + 1 - ship.GetBarrelShotTick(Definition.BarrelIndex),
                ShipEntity.BarrelCooldownTicks);
            if (Definition.IsCharged)
                return new DeviceState(_charge.IsReady(ship, Definition.BarrelIndex, tick + 1), IsActive, barrel,
                    activeBullet: context.Bullets.TryGet(_shot, out var shot) ? context.Bullets[shot].Id : default,
                    charge: _charge.Level);
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

        // Charged only (phase 5, step 4f): the charge, the beam of each charge that fired, and the last shot's beam.
        private readonly WeaponCharge _charge;
        private readonly Dictionary<int, BeamDefinition> _charged = new Dictionary<int, BeamDefinition>();
        private EntityHandle _shot;
    }
}
