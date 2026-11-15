using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy TeleporterDevice (DeviceClass.Teleporter; phase 5, step 11b). Obsolete: an early game version had it, and
    // WarpDrive replaced it. Players cannot get it; bosses and mods still use it (owner decision 3). A held bit with the
    // cooldown passed and the energy cost in store spends the cost and moves the ship Range along its heading, with torus
    // wrap and an exact landing. The velocity and the spin go to 0; the cooldown runs from the use. TeleportImmune does
    // not apply: it is for impacts only.
    public sealed class TeleporterDefinition : DeviceDefinition
    {
        public TeleporterDefinition(float energyCost, float cooldown, float range)
        {
            if (!(range >= 0f) || float.IsInfinity(range)) throw new ArgumentOutOfRangeException(nameof(range));

            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtStart, 0f);
            Range = range;
        }

        public ActivationPolicy Activation { get; }

        // Legacy Range: the jump distance.
        public float Range { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new TeleporterDevice(this, index);
    }

    // Only the owner jumps. A remote copy takes the owner's use record for its cooldown and the view, and follows the
    // jump by position corrections, as for teleport impacts (question 2 (a)).
    internal sealed class TeleporterDevice : ShipDevice
    {
        public TeleporterDevice(TeleporterDefinition definition, int index) : base(index) => Definition = definition;

        public TeleporterDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
        }

        // Legacy Active with CanBeActivated: the held bit, the cooldown and the cost. While held, it jumps at each cooldown.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && _use.IsReady(Definition.Activation, ship, context.Tick);
            if (!IsActive || !context.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, context.Tick)) return;

            Jump(context, ship);
            context.CaptureDeviceUse(ship, Index, true, context.Tick);
        }

        // The copy does not move; its flashes use its own pose, so a late record shows them where the copy is.
        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (!started || _use.LastUseTick == tick) return;

            _use.Start(Definition.Activation, tick);
            var from = context.Physics.GetPosition(ship.Body);
            context.AddTeleport(ship, Index, from, context.Physics.Torus.Wrap(from + Definition.Range * Heading(context, ship)));
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        // Legacy OnActivate at the start point, Body.Move, velocity and spin to 0, then OnDeactivate at the end point.
        private void Jump(DeviceContext context, ShipEntity ship)
        {
            var physics = context.Physics;
            var from = physics.GetPosition(ship.Body);
            physics.TeleportGroup(ship.Body, from + Definition.Range * Heading(context, ship));
            physics.SetLinearVelocity(ship.Body, Vec2.Zero);
            physics.SetAngularVelocity(ship.Body, 0f);
            context.AddTeleport(ship, Index, from, physics.GetPosition(ship.Body));
        }

        private static Vec2 Heading(DeviceContext context, ShipEntity ship) =>
            context.Physics.GetRotation(ship.Body).Rotate(new Vec2(1f, 0f));

        private DeviceActivation _use;
    }
}
