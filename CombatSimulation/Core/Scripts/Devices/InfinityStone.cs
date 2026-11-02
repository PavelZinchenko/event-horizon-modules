namespace CombatSimulation.Core
{
    // Legacy InfinityStone (DeviceClass.TimeMachine; phase 5, step 7). A use restores armor, shield and energy and gives
    // NoContacts for Lifetime; the cooldown starts with it. It starts while the bit is held and the device is ready, or
    // when the damage of a tick leaves no armor (H3); then NoContacts runs from the next tick. Lethal damage during
    // NoContacts restores again without a cooldown.
    public sealed class InfinityStoneDefinition : DeviceDefinition
    {
        public InfinityStoneDefinition(float cooldown, float lifetime) =>
            Activation = new ActivationPolicy(0f, cooldown, CooldownStart.AtStart, lifetime);

        public ActivationPolicy Activation { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new InfinityStoneDevice(this, index);
    }

    // Only the owner starts a use; a remote copy follows the owner's record (H8).
    internal sealed class InfinityStoneDevice : ShipDevice
    {
        public InfinityStoneDevice(InfinityStoneDefinition definition, int index) : base(index) =>
            Definition = definition;

        public InfinityStoneDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
        }

        public override void Update(Battle battle, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && _use.IsReady(Definition.Activation, ship, battle.Tick);
            if (!IsActive || !battle.Setup.IsLocalOwner(ship.Owner)) return;

            _use.Start(Definition.Activation, battle.Tick);
            Restore(ship);
            battle.CaptureDeviceUse(ship, Index, true, battle.Tick);
        }

        // NoContacts runs on without the key.
        internal override void UpdateRetreating(Battle battle, ShipEntity ship) => IsActive = false;

        internal override void AddStatus(Battle battle, ShipEntity ship, ref ShipStatus status)
        {
            if (_use.IsRunningAt(Definition.Activation, battle.Tick)) status.Flags |= ShipStatusFlags.NoContacts;
        }

        internal override bool OnLethalDamage(Battle battle, ShipEntity ship)
        {
            var tick = battle.Tick;
            if (_use.IsRunningAt(Definition.Activation, tick))
            {
                Restore(ship);
                return true;
            }

            if (!_use.IsReady(Definition.Activation, ship, tick)) return false;
            _use.StartNextTick(Definition.Activation, tick);
            Restore(ship);
            battle.CaptureDeviceUse(ship, Index, true, tick + 1);
            return true;
        }

        // The record names the first tick of NoContacts; the copy's cooldown counts from it.
        internal override void ApplyRemoteUse(Battle battle, ShipEntity ship, long tick, bool started)
        {
            if (!started) return;
            _use.Start(Definition.Activation, tick);
            Restore(ship);
        }

        internal override DeviceState GetState(Battle battle, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        // Legacy RestoreShip: Get(-MaxValue) on each resource; a gain starts no recharge delay.
        private static void Restore(ShipEntity ship)
        {
            ship.Energy.Consume(-ship.Energy.Max);
            ship.Armor.Consume(-ship.Armor.Max);
            ship.Shield.Consume(-ship.Shield.Max);
        }

        private DeviceActivation _use;
    }
}
