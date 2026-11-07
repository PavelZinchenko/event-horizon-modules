namespace CombatSimulation.Core
{
    // Legacy FortificationDevice (DeviceClass Fortification; phase 5, step 9d). It runs while the bit is held, the
    // cooldown has passed and the energy of the tick is available: EnergyPerSecond x the tick duration. A start also
    // needs EnergyPerSecond in store. While on, each energy, heat and kinetic armor resistance r becomes
    // Share + r (1 - Share) (ShipStatus.ResistanceShare), and the ship colour moves to the device colour. A released bit
    // or a failed payment ends it and starts the cooldown.
    public sealed class FortificationDefinition : DeviceDefinition
    {
        public FortificationDefinition(float energyPerSecond, float cooldown, float share)
        {
            if (!(energyPerSecond >= 0f) || float.IsInfinity(energyPerSecond))
                throw new System.ArgumentOutOfRangeException(nameof(energyPerSecond), "Energy must be finite and not negative.");
            if (!(share >= 0f && share <= 1f))
                throw new System.ArgumentOutOfRangeException(nameof(share), "Share must be from 0 to 1.");

            EnergyPerSecond = energyPerSecond;
            Share = share;
            Activation = new ActivationPolicy(0f, cooldown, CooldownStart.AtEnd, 0f);
        }

        // Legacy EnergyConsumption.
        public float EnergyPerSecond { get; }

        // Legacy Power / 100, 0.5 when Power is 0; the mapper computes it.
        public float Share { get; }

        // No cost at the start and no duration: the energy of each tick and the release end the use.
        public ActivationPolicy Activation { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new FortificationDevice(this, index);
    }

    // Only the owner starts and ends a use; a remote copy follows the owner's records (H8). The copy pays the energy of
    // its held bit as the owner does; a failed payment ends nothing.
    internal sealed class FortificationDevice : ShipDevice
    {
        public FortificationDevice(FortificationDefinition definition, int index) : base(index) => Definition = definition;

        public FortificationDefinition Definition { get; }

        // Legacy StealthDevice.OnSystemActivated keeps stealth on for a fortification.
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

        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status)
        {
            _tint.Update(_use.IsRunning);
            _tint.AddTo(ref status);
            if (_use.IsRunning) status.AddResistanceShare(Definition.Share);
        }

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (!started) _use.End(Definition.Activation, tick);
            else if (!_use.IsRunning) _use.Start(Definition.Activation, tick);
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(CanBeActivated(ship, tick + 1), IsActive, _use.Cooldown(Definition.Activation, tick + 1));

        // Legacy Active is the held bit with CanBeActivated, also when the payment fails.
        private void Update(DeviceContext context, ShipEntity ship, bool held)
        {
            var tick = context.Tick;
            IsActive = held && CanBeActivated(ship, tick);
            var paid = IsActive && ship.Energy.TryConsume(Definition.EnergyPerSecond * Battle.TickDuration);
            if (!context.Setup.IsLocalOwner(ship.Owner)) return;

            if (paid)
            {
                if (_use.IsRunning) return;
                _use.Start(Definition.Activation, tick);
                context.CaptureDeviceUse(ship, Index, true, tick);
            }
            else if (_use.IsRunning)
            {
                _use.End(Definition.Activation, tick);
                context.CaptureDeviceUse(ship, Index, false, tick);
            }
        }

        // Legacy CanBeActivated: the cooldown, and EnergyConsumption in store unless the fortification is on.
        private bool CanBeActivated(ShipEntity ship, long tick) =>
            _use.IsCooledDown(Definition.Activation, tick) &&
            (_use.IsRunning || ship.Energy.CanConsume(Definition.EnergyPerSecond));

        private DeviceActivation _use;
        private DeviceTint _tint;
    }
}
