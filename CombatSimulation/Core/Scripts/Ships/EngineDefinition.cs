using System;

namespace CombatSimulation.Core
{
    // Derived engine stats, in core units (radians). Phase 4 computes them from engine power and weight.
    public sealed class EngineDefinition
    {
        public EngineDefinition(float propulsion, float maxVelocity, float turnRate, float maxAngularVelocity,
            float velocityLimit = float.PositiveInfinity)
        {
            Propulsion = Check(propulsion, nameof(propulsion));
            MaxVelocity = Check(maxVelocity, nameof(maxVelocity));
            TurnRate = Check(turnRate, nameof(turnRate));
            MaxAngularVelocity = Check(maxAngularVelocity, nameof(maxAngularVelocity));
            if (!(velocityLimit >= 0f))
                throw new ArgumentOutOfRangeException(nameof(velocityLimit), "Velocity limit must not be negative.");
            VelocityLimit = velocityLimit;
        }

        // Acceleration at full throttle, in units per second squared.
        public float Propulsion { get; }

        // The speed the thrust steers toward, in units per second. It is a soft cap.
        public float MaxVelocity { get; }

        // Legacy EngineStats.VelocityLimit (ShipSettings.MaxVelocity): the cap of MaxVelocity after an engine status
        // scales it (phase 5, step 9c).
        public float VelocityLimit { get; }

        // Angular acceleration, in radians per second squared.
        public float TurnRate { get; }

        // Angular speed of the turn controller, in radians per second.
        public float MaxAngularVelocity { get; }

        // Legacy EngineStats.IsNull: an engine that neither thrusts nor turns.
        public bool IsNull => Propulsion <= 0.0001f && TurnRate <= 0.0001f;

        private static float Check(float value, string name)
        {
            if (!(value >= 0f) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Engine stats must be finite and nonnegative.");
            return value;
        }
    }
}
