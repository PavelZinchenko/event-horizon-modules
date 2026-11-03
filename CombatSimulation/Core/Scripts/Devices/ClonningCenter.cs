namespace CombatSimulation.Core
{
    // Legacy ClonningDevice (DeviceClass.ClonningCenter; phase 5, step 7c). While no clone of the device lives and the
    // cooldown has passed, a held bit spends the energy cost and launches a clone. The mapper builds the clone as legacy
    // ShipSpecDecorator does: the ship without drone bays and clonning centers, with 90% armor, shield and shield
    // recharge. The clone is a drone of the mothership's slot (legacy UnitClass.Drone): it stays when the mothership dies
    // or retreats, and it does not keep its side alive. Its loss starts the full cooldown. Deployment starts the timer
    // 80% elapsed.
    public sealed class ClonningCenterDefinition : DeviceDefinition
    {
        // Legacy ClonningDevice: TimeFromLastUse = MaxCooldown x 0.8 at creation.
        public const float InitialElapsedShare = 0.8f;

        public ClonningCenterDefinition(float energyCost, float cooldown, ShipDefinition clone)
        {
            Clone = clone ?? throw new System.ArgumentNullException(nameof(clone));
            DroneBayDefinition.ValidateChild(clone, nameof(clone));

            // A use lasts while its clone lives, so the cooldown starts at its end: the loss of the clone.
            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtEnd, 0f);
        }

        public ShipDefinition Clone { get; }
        public ActivationPolicy Activation { get; }
        public override ShipDefinition ChildShip => Clone;

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new ClonningCenterDevice(this, index);
    }

    // Only the owner launches. A remote copy links the clone from the owner's launch record and hears its loss from the
    // owner's removal record (H8).
    internal sealed class ClonningCenterDevice : ShipDevice
    {
        public ClonningCenterDevice(ClonningCenterDefinition definition, int index) : base(index) => Definition = definition;

        public ClonningCenterDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset(Definition.Activation, tick, ClonningCenterDefinition.InitialElapsedShare);
            _clone = default;
        }

        // Legacy Active is the held bit with CanBeActivated: no clone, the cooldown and the cost.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && IsReady(ship, context.Tick);
            if (!IsActive || !context.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, context.Tick)) return;

            context.RequestSpawn(SpawnRequest.Launch(ship, Index));
        }

        internal override void OnShipLaunched(DeviceContext context, ShipEntity ship, EntityId child, long tick)
        {
            _clone = child;
            if (!_use.IsRunning) _use.Start(Definition.Activation, tick);
        }

        internal override void OnShipLost(DeviceContext context, ShipEntity ship, EntityId child, long tick)
        {
            if (child != _clone) return;
            _clone = default;
            _use.End(Definition.Activation, tick);
        }

        // Legacy Cooldown is 1 while the clone lives. The AI's SpawnDrones counts the device as one clone in the hangar
        // without a clone, and one active clone with it.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick)
        {
            var hasClone = !_clone.IsNull;
            return new DeviceState(IsReady(ship, tick + 1), IsActive,
                hasClone ? 1f : _use.Cooldown(Definition.Activation, tick + 1), hasClone ? 0 : 1, hasClone ? 1 : 0);
        }

        private bool IsReady(ShipEntity ship, long tick) => _clone.IsNull && _use.IsReady(Definition.Activation, ship, tick);

        private DeviceActivation _use;
        private EntityId _clone;
    }
}
