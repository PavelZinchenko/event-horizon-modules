using System;

namespace CombatSimulation.Core
{
    // Immutable ship parameters built at setup. Phase 2 builds them from code fixtures; phase 4 from the database.
    public sealed class ShipDefinition
    {
        public ShipDefinition(float hullRadius, float mass, EngineDefinition engine, float armor,
            ResourceDefinition shield, ResourceDefinition energy, EngineDefinition engineWithoutEnergy = null)
        {
            if (!(hullRadius > 0f) || float.IsInfinity(hullRadius))
                throw new ArgumentOutOfRangeException(nameof(hullRadius), "Hull radius must be finite and positive.");
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentOutOfRangeException(nameof(mass), "Mass must be finite and positive.");
            if (!(armor > 0f) || float.IsInfinity(armor))
                throw new ArgumentOutOfRangeException(nameof(armor), "Armor must be finite and positive.");

            HullRadius = hullRadius;
            Mass = mass;
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            EngineWithoutEnergy = engineWithoutEnergy;
            Armor = armor;
            Shield = shield;
            Energy = energy;
        }

        // The hull is one circle until phase 4 loads collider outlines.
        public float HullRadius { get; }
        public float Mass { get; }

        public EngineDefinition Engine { get; }

        // The engine while energy is zero. Null: the ship neither thrusts nor turns without energy.
        public EngineDefinition EngineWithoutEnergy { get; }

        // Maximum armor. Armor does not regenerate (Q7).
        public float Armor { get; }

        public ResourceDefinition Shield { get; }
        public ResourceDefinition Energy { get; }
    }
}
