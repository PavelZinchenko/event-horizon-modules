using System;

namespace CombatSimulation.Core
{
    // Q3: a continuous, attached ray. Costs and damage are absolute, without database modifiers.
    public sealed class BeamDefinition : DeviceDefinition
    {
        public BeamDefinition(int barrelIndex, float cooldown, float activationCost, float energyPerSecond,
            float range, float damagePerSecond, int damageType = 0)
        {
            if (barrelIndex < 0) throw new ArgumentOutOfRangeException(nameof(barrelIndex));
            ValidateAmount(cooldown, nameof(cooldown));
            ValidateAmount(activationCost, nameof(activationCost));
            ValidateAmount(energyPerSecond, nameof(energyPerSecond));
            ValidateAmount(damagePerSecond, nameof(damagePerSecond));
            if (!(range > 0f) || float.IsInfinity(range)) throw new ArgumentOutOfRangeException(nameof(range));
            DamageTypeTable.ValidateIndex(damageType, nameof(damageType));

            BarrelIndex = barrelIndex;
            Cooldown = cooldown;
            CooldownTicks = TickTime.AtLeast(cooldown);
            ActivationCost = activationCost;
            EnergyPerSecond = energyPerSecond;
            Range = range;
            DamagePerSecond = damagePerSecond;
            DamageType = damageType;
        }

        public int BarrelIndex { get; }
        public float Cooldown { get; }
        public int CooldownTicks { get; }
        public float ActivationCost { get; }
        public float EnergyPerSecond { get; }
        public float Range { get; }
        public float DamagePerSecond { get; }

        // An index into the battle's damage type table (D4).
        public int DamageType { get; }

        internal override void Validate(int barrelCount)
        {
            if (BarrelIndex >= barrelCount)
                throw new ArgumentOutOfRangeException(nameof(BarrelIndex), $"Beam barrel {BarrelIndex} does not exist.");
        }

        internal override ShipDevice CreateDevice(int index) => new BeamDevice(this, index);

        private static void ValidateAmount(float value, string name)
        {
            if (!(value >= 0f) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(name);
        }
    }
}
