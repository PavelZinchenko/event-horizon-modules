using System;

namespace CombatSimulation.Core
{
    // The legacy damage rules as data (D4, owner 2026-10-28): legacy Impact, Resistance and ShipStats.ApplyDamage.
    // Fully configurable damage types come later; until then the mapper and the tests build their data here.
    public static class LegacyDamageModel
    {
        // Database DamageType order.
        public const int Impact = 0;
        public const int Energy = 1;
        public const int Heat = 2;
        public const int Corrosive = 3;

        // Legacy Impact.ApplyShield: corrosive damage meets the shield before the other types.
        public static readonly DamageTypeTable Types = new DamageTypeTable(new[]
        {
            new DamageTypeDefinition(1),
            new DamageTypeDefinition(1),
            new DamageTypeDefinition(1),
            new DamageTypeDefinition(0),
        }, Impact);

        // A ship's resistances by type, from the legacy percentages (IShipStats). Corrosive damage uses half of the
        // smallest armor resistance and the shield corrosive resistance. The energy absorption percentage applies to
        // energy damage after the shield; legacy divides it by the armor multiplier.
        public static DamageResistance[] Resistances(float kinetic, float energy, float heat, float shieldCorrosive,
            float energyAbsorption = 0f, float armorMultiplier = 1f)
        {
            if (!(armorMultiplier > 0f) || float.IsInfinity(armorMultiplier))
                throw new ArgumentOutOfRangeException(nameof(armorMultiplier), "Armor multiplier must be finite and positive.");

            return new[]
            {
                new DamageResistance(kinetic),
                new DamageResistance(energy, energyGain: energyAbsorption > 0.01f ? energyAbsorption / armorMultiplier : 0f),
                new DamageResistance(heat),
                new DamageResistance(0.5f * Math.Min(kinetic, Math.Min(energy, heat)), shieldCorrosive),
            };
        }
    }
}
