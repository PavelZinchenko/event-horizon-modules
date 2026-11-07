namespace CombatSimulation.Core
{
    // Legacy BrakeDevice (DeviceClass Brake; phase 5, step 9c). A passive device: it ignores its bit (legacy
    // CanBeActivated is false) and is never active. While the throttle is 0.001 or less, the engine decelerates the ship
    // against its velocity (ShipStatus.Deceleration); it stops at zero speed (owner question 6). A remote copy reads the
    // throttle of its control frames, so it needs no record.
    public sealed class BrakeDefinition : DeviceDefinition
    {
        // Legacy Power / sqrt(ship weight), in units per second squared; the mapper computes it.
        public BrakeDefinition(float deceleration)
        {
            if (!(deceleration >= 0f) || float.IsInfinity(deceleration))
                throw new System.ArgumentOutOfRangeException(nameof(deceleration),
                    "Deceleration must be finite and not negative.");
            Deceleration = deceleration;
        }

        public float Deceleration { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new BrakeDevice(this, index);
    }

    internal sealed class BrakeDevice : ShipDevice
    {
        public BrakeDevice(BrakeDefinition definition, int index) : base(index) => Definition = definition;

        public BrakeDefinition Definition { get; }

        public override void Update(DeviceContext context, ShipEntity ship) { }

        // Legacy adds the deceleration of each brake.
        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status) =>
            status.Deceleration += Definition.Deceleration;
    }
}
