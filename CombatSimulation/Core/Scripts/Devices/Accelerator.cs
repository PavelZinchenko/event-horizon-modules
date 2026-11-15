namespace CombatSimulation.Core
{
    // Legacy AcceleratorDevice (DeviceClass Accelerator; phase 5, step 9c). It runs while the bit is held, the cooldown
    // has passed and the energy of the tick is available: EnergyPerSecond x the tick duration. While on, the ship has
    // throttle 1 (ShipStatusFlags.FullThrottle), and propulsion and velocity x Power; EngineDefinition.VelocityLimit caps
    // the velocity. A released bit or a failed payment ends it and starts the cooldown.
    public sealed class AcceleratorDefinition : DeviceDefinition
    {
        public AcceleratorDefinition(float energyPerSecond, float cooldown, float power)
        {
            if (!(energyPerSecond >= 0f) || float.IsInfinity(energyPerSecond))
                throw new System.ArgumentOutOfRangeException(nameof(energyPerSecond), "Energy must be finite and not negative.");
            if (!(power >= 0f) || float.IsInfinity(power))
                throw new System.ArgumentOutOfRangeException(nameof(power), "Power must be finite and not negative.");

            EnergyPerSecond = energyPerSecond;
            Power = power;
            Activation = new ActivationPolicy(0f, cooldown, CooldownStart.AtEnd, 0f);
        }

        // Legacy max(1, EnergyConsumption x the ship's EngineEnergyConsumption); the mapper computes it.
        public float EnergyPerSecond { get; }

        public float Power { get; }

        // No cost at the start and no duration: the energy of each tick and the release end the use.
        public ActivationPolicy Activation { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new AcceleratorDevice(this, index);
    }

    // Only the owner starts and ends a use; a remote copy follows the owner's records (H8). The copy pays the energy of
    // its held bit as the owner does, so its energy stays close until the correction; a failed payment ends nothing.
    internal sealed class AcceleratorDevice : ShipDevice
    {
        public AcceleratorDevice(AcceleratorDefinition definition, int index) : base(index) => Definition = definition;

        public AcceleratorDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
        }

        public override void Update(DeviceContext context, ShipEntity ship) => Update(context, ship, IsControlOn(ship));

        // Legacy ShipRetreatEffect blocks the activation, so a retreat ends the use as a released bit does.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship) => Update(context, ship, false);

        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status)
        {
            if (!_use.IsRunning) return;
            status.Flags |= ShipStatusFlags.FullThrottle;
            status.ScaleEngine(Definition.Power);
        }

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (!started) _use.End(Definition.Activation, tick);
            else if (!_use.IsRunning) _use.Start(Definition.Activation, tick);
        }

        // Legacy CanBeActivated: the cooldown only; the energy decides in each tick.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsCooledDown(Definition.Activation, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        // Legacy Active is the held bit with the cooldown passed, also when the payment fails.
        private void Update(DeviceContext context, ShipEntity ship, bool held)
        {
            var tick = context.Tick;
            IsActive = held && _use.IsCooledDown(Definition.Activation, tick);
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

        private DeviceActivation _use;
    }
}
