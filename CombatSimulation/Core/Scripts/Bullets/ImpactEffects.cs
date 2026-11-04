using System;

namespace CombatSimulation.Core
{
    // Effects of a bullet or beam on a ship or drone target besides damage and push (phase 5, step 5). A hit applies
    // each once, at full power (legacy actions have no falloff); damage over time and a beam apply each per second.
    // A missile or a tail segment target takes none of them.
    public readonly struct ImpactEffects
    {
        public ImpactEffects(float energyDrain = 0f, float shieldDrain = 0f, float siphon = 0f)
        {
            Validate(energyDrain, nameof(energyDrain));
            Validate(shieldDrain, nameof(shieldDrain));
            Validate(siphon, nameof(siphon));
            EnergyDrain = energyDrain;
            ShieldDrain = shieldDrain;
            Siphon = siphon;
        }

        // Legacy DrainEnergyAction: the target's energy falls by it, clamped at 0, and its recharge delay restarts.
        public float EnergyDrain { get; }

        // Legacy DamageShieldAction: the target's shield falls by it before the shield absorbs damage.
        public float ShieldDrain { get; }

        // Legacy SiphonHitPointsAction Factor, on the first damage effect: the attacker's ship regains that damage x
        // (1 - the target's armor resistance of its type) x Siphon, also while the target's shield absorbs it
        // (finding 7a). Ship and drone targets only.
        public float Siphon { get; }

        public bool HasDrain => EnergyDrain > 0f || ShieldDrain > 0f;

        // Legacy BulletStats with PowerLevel c: effect powers x c. The siphon factor stays.
        internal ImpactEffects WithCharge(float charge) =>
            new ImpactEffects(EnergyDrain * charge, ShieldDrain * charge, Siphon);

        private static void Validate(float value, string name)
        {
            if (!(value >= 0f) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "An effect must be finite and not negative.");
        }
    }
}
