using System;

namespace CombatSimulation.Core
{
    // Immutable ship parameters built at setup. Phase 2 builds them from code fixtures; phase 4 from the database.
    public sealed class ShipDefinition
    {
        public ShipDefinition(float hullRadius, float mass)
        {
            if (!(hullRadius > 0f) || float.IsInfinity(hullRadius))
                throw new ArgumentOutOfRangeException(nameof(hullRadius), "Hull radius must be finite and positive.");
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentOutOfRangeException(nameof(mass), "Mass must be finite and positive.");

            HullRadius = hullRadius;
            Mass = mass;
        }

        // The hull is one circle until phase 4 loads collider outlines.
        public float HullRadius { get; }
        public float Mass { get; }
    }
}
