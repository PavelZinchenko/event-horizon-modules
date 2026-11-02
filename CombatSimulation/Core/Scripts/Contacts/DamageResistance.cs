using System;

namespace CombatSimulation.Core
{
    // A ship's response to one damage type (D4). The default value takes the damage in full and gains nothing.
    public readonly struct DamageResistance
    {
        public DamageResistance(float armor, float shield = 0f, float energyGain = 0f)
        {
            Armor = armor;
            Shield = shield;
            EnergyGain = energyGain;
        }

        // The fraction removed before armor takes the damage (legacy Resistance.ModifyDamage). Negative adds damage.
        public float Armor { get; }

        // The fraction removed before the shield takes the damage (legacy ShieldCorrosive).
        public float Shield { get; }

        // Energy the ship gains per point of this damage that passes the shield (legacy energy absorption).
        public float EnergyGain { get; }

        // At most 1, so damage never heals.
        internal void Validate(string name)
        {
            if (!(Armor <= 1f) || float.IsInfinity(Armor))
                throw new ArgumentOutOfRangeException(name, "Armor resistance must be finite and at most 1.");
            if (!(Shield <= 1f) || float.IsInfinity(Shield))
                throw new ArgumentOutOfRangeException(name, "Shield resistance must be finite and at most 1.");
            if (!(EnergyGain >= 0f) || float.IsInfinity(EnergyGain))
                throw new ArgumentOutOfRangeException(name, "Energy gain must be finite and not negative.");
        }
    }
}
