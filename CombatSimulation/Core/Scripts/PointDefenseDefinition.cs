using System;

namespace CombatSimulation.Core
{
    // Q4: an overlap around the ship. Drone damage joins this device in step 10.
    public sealed class PointDefenseDefinition : DeviceDefinition
    {
        public PointDefenseDefinition(float radius, float cooldown, float energyCost)
        {
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius));
            if (!(cooldown >= 0f) || float.IsInfinity(cooldown))
                throw new ArgumentOutOfRangeException(nameof(cooldown));
            if (!(energyCost >= 0f) || float.IsInfinity(energyCost))
                throw new ArgumentOutOfRangeException(nameof(energyCost));

            Radius = radius;
            Cooldown = cooldown;
            EnergyCost = energyCost;
            QueryCooldownTicks = TickTime.After(cooldown);
            ActivationCooldownTicks = TickTime.AtLeast(cooldown);
        }

        public float Radius { get; }
        public float Cooldown { get; }
        // Paid once on activation and once for each accepted zap.
        public float EnergyCost { get; }
        public int QueryCooldownTicks { get; }
        public int ActivationCooldownTicks { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new PointDefenseDevice(this, index);
    }
}
