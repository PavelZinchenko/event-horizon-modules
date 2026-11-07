namespace CombatSimulation.Core
{
    // Legacy StealthDevice (DeviceClass Stealth and SuperStealth; phase 5, step 9b). A held bit starts it when the
    // cooldown has passed and the energy cost is available; it spends the cost once and stays on after the release. While
    // on, the ship has target priority None (ShipStatusFlags.Stealth). It ends when another device of the ship becomes
    // active, except the devices that keep it (ShipDevice.KeepsStealth), and for Stealth when the ship takes a contact.
    // Each of these restarts the cooldown, also while stealth is off (legacy Deactivate; owner question 5).
    public sealed class StealthDefinition : DeviceDefinition
    {
        public StealthDefinition(float energyCost, float cooldown, bool endsOnContact)
        {
            EndsOnContact = endsOnContact;
            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtEnd, 0f);
        }

        // Stealth; SuperStealth survives contacts (legacy invulnerability flag).
        public bool EndsOnContact { get; }

        // The use has no duration: only the end rules stop it.
        public ActivationPolicy Activation { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new StealthDevice(this, index);
    }

    // Only the owner starts and ends stealth; a remote copy follows the owner's records (H8) and never ends it on a
    // contact or on its own device activity.
    internal sealed class StealthDevice : ShipDevice
    {
        public StealthDevice(StealthDefinition definition, int index) : base(index) => Definition = definition;

        public StealthDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
        }

        public override void Update(DeviceContext context, ShipEntity ship) => Update(context, ship, IsControlOn(ship));

        // Legacy ShipRetreatEffect blocks the activation only: stealth stays on, and a contact still ends it.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship) => Update(context, ship, false);

        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status)
        {
            if (_use.IsRunning) status.Flags |= ShipStatusFlags.Stealth;
        }

        // Legacy OnSystemActivated: another device of the ship became active at stage 2 of this tick. Legacy counts the
        // cooldown from the next tick when this device already updated in the tick.
        internal void OnDeviceActivated(DeviceContext context, ShipEntity ship, bool updated) =>
            End(context, ship, updated ? context.Tick + 1 : context.Tick);

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (!started) _use.End(Definition.Activation, tick);
            else if (!_use.IsRunning) _use.Start(Definition.Activation, tick);
        }

        // Legacy CanBeActivated: the cooldown and the cost, also while stealth is on.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        // Legacy OnHit comes after the systems of the contact's tick, so the cooldown counts from this tick.
        private void Update(DeviceContext context, ShipEntity ship, bool held)
        {
            var tick = context.Tick;
            if (Definition.EndsOnContact && ship.LastContactTick == tick - 1) End(context, ship, tick);

            IsActive = held && _use.IsReady(Definition.Activation, ship, tick);
            if (!IsActive || _use.IsRunning || !context.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, tick)) return;

            context.CaptureDeviceUse(ship, Index, true, tick);
        }

        // A copy restarts only its cooldown; the owner's record ends its stealth.
        private void End(DeviceContext context, ShipEntity ship, long cooldownTick)
        {
            if (!context.Setup.IsLocalOwner(ship.Owner))
            {
                _use.RestartCooldown(cooldownTick);
                return;
            }

            var running = _use.IsRunning;
            _use.End(Definition.Activation, cooldownTick);
            if (running) context.CaptureDeviceUse(ship, Index, false, context.Tick);
        }

        private DeviceActivation _use;
    }
}
