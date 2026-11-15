using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy WarpDrive (DeviceClass.TeleporterV2; phase 5, step 7). A use spends the energy cost, sets the velocity to
    // heading x Speed and stops the spin. While it runs the ship has no contacts, its engine is off, it is faded, and its
    // shot weapons are blocked. It ends after Range / Speed, or at a released bit after MinTime; the ship stops and the
    // cooldown starts. Legacy jumps at once when the ship has a WormTail (V1) device; V1 has no record, so the core
    // always warps.
    public sealed class WarpDriveDefinition : DeviceDefinition
    {
        public const float MinTime = 0.1f;

        public WarpDriveDefinition(float energyCost, float cooldown, float speed, float range)
        {
            if (!(speed > 0f) || float.IsInfinity(speed))
                throw new System.ArgumentOutOfRangeException(nameof(speed), "Speed must be finite and positive.");
            if (!(range >= 0f) || float.IsInfinity(range))
                throw new System.ArgumentOutOfRangeException(nameof(range), "Range must be finite and not negative.");

            Speed = speed;
            Range = range;
            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtEnd, range / speed, MinTime);
        }

        public float Speed { get; }
        public float Range { get; }
        public ActivationPolicy Activation { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new WarpDriveDevice(this, index);
    }

    // Only the owner starts and ends a warp; a remote copy follows the owner's records (H8) and ends at the full
    // duration when the end record is late.
    internal sealed class WarpDriveDevice : ShipDevice
    {
        public WarpDriveDevice(WarpDriveDefinition definition, int index) : base(index) => Definition = definition;

        public WarpDriveDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
        }

        // Legacy CanBeActivated stays true during a warp, so Active is the held bit.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            var held = IsControlOn(ship);
            if (_use.IsRunning)
            {
                IsActive = held;
                UpdateRunning(context, ship, held);
                return;
            }

            IsActive = held && _use.IsReady(Definition.Activation, ship, context.Tick);
            if (!IsActive || !context.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, context.Tick)) return;

            Start(context, ship);
            context.CaptureDeviceUse(ship, Index, true, context.Tick);
        }

        // A retreat ends the warp as a released bit does.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship)
        {
            IsActive = false;
            if (_use.IsRunning) UpdateRunning(context, ship, false);
        }

        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status)
        {
            if (!_use.IsRunning) return;

            status.Flags |= ShipStatusFlags.NoContacts | ShipStatusFlags.EngineOff | ShipStatusFlags.Faded;
            for (var i = 0; i < ship.DeviceCount; ++i)
                if (ship.GetDevice(i).IsShotWeapon) status.BlockedDevices |= 1UL << i;
        }

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (started)
            {
                if (_use.IsRunning) return;
                _use.Start(Definition.Activation, tick);
                Start(context, ship);
            }
            else
            {
                // A copy that already ended at the full duration takes the owner's cooldown start.
                _use.End(Definition.Activation, tick);
                context.Physics.SetLinearVelocity(ship.Body, Vec2.Zero);
            }
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsRunning || _use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.IsRunning ? 0f : _use.Cooldown(Definition.Activation, tick + 1));

        private void UpdateRunning(DeviceContext context, ShipEntity ship, bool held)
        {
            var local = context.Setup.IsLocalOwner(ship.Owner);
            if (!_use.ShouldEnd(Definition.Activation, context.Tick, held || !local)) return;

            _use.End(Definition.Activation, context.Tick);
            context.Physics.SetLinearVelocity(ship.Body, Vec2.Zero);
            if (local) context.CaptureDeviceUse(ship, Index, false, context.Tick);
        }

        private void Start(DeviceContext context, ShipEntity ship)
        {
            var rotation = context.Physics.GetRotation(ship.Body);
            context.Physics.SetLinearVelocity(ship.Body, Definition.Speed * new Vec2(rotation.C, rotation.S));
            context.Physics.SetAngularVelocity(ship.Body, 0f);
        }

        private DeviceActivation _use;
    }
}
