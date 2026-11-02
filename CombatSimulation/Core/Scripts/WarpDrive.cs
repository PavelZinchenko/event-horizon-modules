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

        public override void Reset()
        {
            base.Reset();
            _use.Reset();
        }

        // Legacy CanBeActivated stays true during a warp, so Active is the held bit.
        public override void Update(Battle battle, ShipEntity ship)
        {
            var held = IsControlOn(ship);
            if (_use.IsRunning)
            {
                IsActive = held;
                UpdateRunning(battle, ship, held);
                return;
            }

            IsActive = held && _use.IsReady(Definition.Activation, ship, battle.Tick);
            if (!IsActive || !battle.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, battle.Tick)) return;

            Start(battle, ship);
            battle.CaptureDeviceUse(ship, Index, true, battle.Tick);
        }

        // A retreat ends the warp as a released bit does.
        internal override void UpdateRetreating(Battle battle, ShipEntity ship)
        {
            IsActive = false;
            if (_use.IsRunning) UpdateRunning(battle, ship, false);
        }

        internal override void AddStatus(Battle battle, ShipEntity ship, ref ShipStatus status)
        {
            if (!_use.IsRunning) return;

            status.Flags |= ShipStatusFlags.NoContacts | ShipStatusFlags.EngineOff | ShipStatusFlags.Faded;
            for (var i = 0; i < ship.DeviceCount; ++i)
                if (ship.GetDevice(i).IsShotWeapon) status.BlockedDevices |= 1UL << i;
        }

        internal override void ApplyRemoteUse(Battle battle, ShipEntity ship, long tick, bool started)
        {
            if (started)
            {
                if (_use.IsRunning) return;
                _use.Start(Definition.Activation, tick);
                Start(battle, ship);
            }
            else
            {
                // A copy that already ended at the full duration takes the owner's cooldown start.
                _use.End(Definition.Activation, tick);
                battle.World.SetLinearVelocity(ship.Body, Vec2.Zero);
            }
        }

        internal override DeviceState GetState(Battle battle, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsRunning || _use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.IsRunning ? 0f : _use.Cooldown(Definition.Activation, tick + 1));

        private void UpdateRunning(Battle battle, ShipEntity ship, bool held)
        {
            var local = battle.Setup.IsLocalOwner(ship.Owner);
            if (!_use.ShouldEnd(Definition.Activation, battle.Tick, held || !local)) return;

            _use.End(Definition.Activation, battle.Tick);
            battle.World.SetLinearVelocity(ship.Body, Vec2.Zero);
            if (local) battle.CaptureDeviceUse(ship, Index, false, battle.Tick);
        }

        private void Start(Battle battle, ShipEntity ship)
        {
            var rotation = battle.World.GetRotation(ship.Body);
            battle.World.SetLinearVelocity(ship.Body, Definition.Speed * new Vec2(rotation.C, rotation.S));
            battle.World.SetAngularVelocity(ship.Body, 0f);
        }

        private DeviceActivation _use;
    }
}
