namespace CombatSimulation.Core
{
    // Legacy GhostDevice (DeviceClass Ghost; phase 5, step 9d). A held bit starts it when the cooldown has passed and the
    // energy cost is available; the start spends the cost. In each later tick it spends the cost per second, for at most
    // Lifetime after the start tick. While on, the ship has no contacts (ShipStatusFlags.NoContacts) and every other
    // device except the stealth devices is blocked. A released bit, the end of the lifetime or a failed payment ends it
    // and starts the cooldown. The ship colour moves to the device colour (ShipStatus.Tint).
    public sealed class GhostDefinition : DeviceDefinition
    {
        public GhostDefinition(float energyCost, float cooldown, float lifetime) =>
            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtEnd, lifetime);

        // Legacy EnergyConsumption: the cost of the start, then the cost per second.
        public float EnergyCost => Activation.EnergyCost;

        // Duration is the lifetime after the start tick.
        public ActivationPolicy Activation { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new GhostDevice(this, index);
    }

    // Only the owner starts and ends a use; a remote copy follows the owner's records (H8). The copy pays the energy of
    // its held bit as the owner does; a failed payment ends nothing.
    internal sealed class GhostDevice : ShipDevice
    {
        public GhostDevice(GhostDefinition definition, int index) : base(index) => Definition = definition;

        public GhostDefinition Definition { get; }

        // Legacy StealthDevice.OnSystemActivated keeps stealth on for a ghost.
        internal override bool KeepsStealth => true;

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
            _tint = default;
        }

        public override void Update(DeviceContext context, ShipEntity ship) => Update(context, ship, IsControlOn(ship));

        // Legacy ShipRetreatEffect blocks the activation, so a retreat ends the use as a released bit does.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship) => Update(context, ship, false);

        // Legacy CanActivateSystem: the ghost itself and the stealth devices stay usable.
        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status)
        {
            _tint.Update(_use.IsRunning);
            _tint.AddTo(ref status);
            if (!_use.IsRunning) return;

            status.Flags |= ShipStatusFlags.NoContacts;
            for (var i = 0; i < ship.DeviceCount; ++i)
                if (i != Index && (ship.StealthDevices & (1UL << i)) == 0) status.BlockedDevices |= 1UL << i;
        }

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (!started) _use.End(Definition.Activation, tick);
            else if (!_use.IsRunning) _use.Start(Definition.Activation, tick);
        }

        // Legacy CanBeActivated: the cooldown, and the cost unless the ghost is on.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsRunning || _use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        // Legacy OnUpdatePhysics: the start tick spends only the cost; a later tick pays while the time left is above 0,
        // that is up to Duration ticks after the start. A copy that takes the start record before its start tick pays
        // nothing in that tick, and never pays the cost.
        private void Update(DeviceContext context, ShipEntity ship, bool held)
        {
            var tick = context.Tick;
            var owner = context.Setup.IsLocalOwner(ship.Owner);
            if (_use.IsRunning)
            {
                IsActive = held;
                if (tick == _use.StartTick) return;
                var paid = held && tick - _use.StartTick <= Definition.Activation.DurationTicks &&
                    ship.Energy.TryConsume(Definition.EnergyCost * Battle.TickDuration);
                if (paid || !owner) return;

                _use.End(Definition.Activation, tick);
                context.CaptureDeviceUse(ship, Index, false, tick);
                return;
            }

            IsActive = held && _use.IsReady(Definition.Activation, ship, tick);
            if (!IsActive || !owner || !_use.TryStart(Definition.Activation, ship, tick)) return;
            context.CaptureDeviceUse(ship, Index, true, tick);
        }

        private DeviceActivation _use;
        private DeviceTint _tint;
    }
}
