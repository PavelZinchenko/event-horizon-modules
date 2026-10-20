using System;

namespace CombatSimulation.Core
{
    // Parameters of a regenerating resource (shield, energy). A zero maximum means the ship has none.
    public readonly struct ResourceDefinition
    {
        public ResourceDefinition(float max, float rechargeRate = 0f, float rechargeDelay = 0f)
        {
            if (!(max >= 0f) || float.IsInfinity(max))
                throw new ArgumentOutOfRangeException(nameof(max), "Maximum must be finite and nonnegative.");
            if (float.IsNaN(rechargeRate) || float.IsInfinity(rechargeRate))
                throw new ArgumentOutOfRangeException(nameof(rechargeRate), "Recharge rate must be finite.");
            if (!(rechargeDelay >= 0f) || float.IsInfinity(rechargeDelay))
                throw new ArgumentOutOfRangeException(nameof(rechargeDelay), "Recharge delay must be finite and nonnegative.");

            Max = max;
            RechargeRate = rechargeRate;
            RechargeDelay = rechargeDelay;
        }

        public readonly float Max;

        // Units per second. Negative drains the resource (net consumption, as legacy).
        public readonly float RechargeRate;

        // Seconds without recharge after a spend.
        public readonly float RechargeDelay;
    }
}
