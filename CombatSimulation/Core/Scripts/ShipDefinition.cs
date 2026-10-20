using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Immutable ship parameters built at setup. Phase 2 builds them from code fixtures; phase 4 from the database.
    public sealed class ShipDefinition
    {
        public ShipDefinition(float hullRadius, float mass, EngineDefinition engine, float armor,
            ResourceDefinition shield, ResourceDefinition energy, EngineDefinition engineWithoutEnergy = null,
            IReadOnlyList<Circle> extraHullParts = null, float rammingDamageMultiplier = 1f)
        {
            if (!(hullRadius > 0f) || float.IsInfinity(hullRadius))
                throw new ArgumentOutOfRangeException(nameof(hullRadius), "Hull radius must be finite and positive.");
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentOutOfRangeException(nameof(mass), "Mass must be finite and positive.");
            if (!(armor > 0f) || float.IsInfinity(armor))
                throw new ArgumentOutOfRangeException(nameof(armor), "Armor must be finite and positive.");
            if (!(rammingDamageMultiplier >= 0f) || float.IsInfinity(rammingDamageMultiplier))
                throw new ArgumentOutOfRangeException(nameof(rammingDamageMultiplier), "Ramming multiplier must be finite and not negative.");

            var parts = new Circle[1 + (extraHullParts?.Count ?? 0)];
            parts[0] = new Circle(Vec2.Zero, hullRadius);
            for (var i = 1; i < parts.Length; ++i)
            {
                var part = extraHullParts[i - 1];
                if (!part.Center.IsFinite || float.IsInfinity(part.Radius))
                    throw new ArgumentOutOfRangeException(nameof(extraHullParts), "Hull parts must be finite.");
                parts[i] = part;
            }

            HullRadius = hullRadius;
            HullParts = parts;
            Mass = mass;
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            EngineWithoutEnergy = engineWithoutEnergy;
            Armor = armor;
            Shield = shield;
            Energy = energy;
            RammingDamageMultiplier = rammingDamageMultiplier;
        }

        // The main hull circle at the body origin. Phase 4 replaces circles with collider outlines.
        public float HullRadius { get; }

        // Hull shapes in definition order: the main circle first, then the extra parts. The order is the shape
        // order within the entity (contact ordering, key 6).
        public IReadOnlyList<Circle> HullParts { get; }

        public float Mass { get; }

        public EngineDefinition Engine { get; }

        // The engine while energy is zero. Null: the ship neither thrusts nor turns without energy.
        public EngineDefinition EngineWithoutEnergy { get; }

        // Maximum armor. Armor does not regenerate (Q7).
        public float Armor { get; }

        public ResourceDefinition Shield { get; }
        public ResourceDefinition Energy { get; }

        // Multiplies the ramming damage this ship deals (Q8).
        public float RammingDamageMultiplier { get; }
    }
}
