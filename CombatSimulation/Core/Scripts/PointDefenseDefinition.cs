using System;

namespace CombatSimulation.Core
{
    // Q4: disarm enemy projectiles and apply energy damage to enemy drones.
    public sealed class PointDefenseDefinition : DeviceDefinition
    {
        public PointDefenseDefinition(float radius, float cooldown, float energyCost, float droneDamage = 0f)
        {
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius));
            if (!(cooldown >= 0f) || float.IsInfinity(cooldown))
                throw new ArgumentOutOfRangeException(nameof(cooldown));
            if (!(energyCost >= 0f) || float.IsInfinity(energyCost))
                throw new ArgumentOutOfRangeException(nameof(energyCost));
            if (!(droneDamage >= 0f) || float.IsInfinity(droneDamage))
                throw new ArgumentOutOfRangeException(nameof(droneDamage));

            Radius = radius;
            Cooldown = cooldown;
            EnergyCost = energyCost;
            DroneDamage = droneDamage;
            QueryCooldownTicks = TickTime.After(cooldown);
            ActivationCooldownTicks = TickTime.AtLeast(cooldown);
        }

        public float Radius { get; }
        public float Cooldown { get; }
        // Paid once on activation and once for each accepted zap.
        public float EnergyCost { get; }
        // Energy damage reduces shield, then armor. It does not drain the target's energy resource.
        public float DroneDamage { get; }
        public int QueryCooldownTicks { get; }
        public int ActivationCooldownTicks { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new PointDefenseDevice(this, index);
    }
}
