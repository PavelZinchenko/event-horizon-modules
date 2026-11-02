using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy ShipEngine (BattleSimulator module), in radians. The formulas are linear in the angle unit,
    // so the conversion from degrees changes no result. Stage 4 runs it before the physics step.
    internal static class ShipEngine
    {
        private const float MinThrottle = 0.01f;
        private const float MinPropulsionDelta = 0.001f;
        private const float BrakeBand = (float)(120.0 * Math.PI / 180.0);
        private const float MinSpin = (float)(0.01 * Math.PI / 180.0);
        private const float FullCircle = (float)(2.0 * Math.PI);

        // Stage 4: the engine input of this tick. No engine while retreating (legacy ShipRetreatEffect).
        public static EngineInput Input(ShipEntity ship) => new EngineInput(
            ship.RetreatCompletionTick != ShipEntity.NoRetreat ? null
                : ship.EngineHasEnergy ? ship.Definition.Engine : ship.Definition.EngineWithoutEnergy,
            ship.Throttle, ship.HasCourse, ship.Course);

        public static void Update(PhysicsWorld world, BodyId body, in EngineInput input, float dt)
        {
            if (input.Engine == null) return;
            var velocity = world.GetLinearVelocity(body);
            var angularVelocity = world.GetAngularVelocity(body);
            Apply(input, world.GetRotation(body), ref velocity, ref angularVelocity, dt);
            world.SetLinearVelocity(body, velocity);
            world.SetAngularVelocity(body, angularVelocity);
        }

        // The same formulas on values, so a correction can re-run them (C1).
        public static void Apply(in EngineInput input, Rot rotation, ref Vec2 velocity, ref float angularVelocity, float dt)
        {
            var engine = input.Engine;
            if (engine == null) return;
            if (input.Throttle > MinThrottle) ApplyThrust(engine, input.Throttle, rotation, ref velocity, dt);

            if (input.HasCourse)
                ApplyTurn(engine, input.Course, rotation.Angle, ref angularVelocity, dt);
            else if (Math.Abs(angularVelocity) > MinSpin)
                ApplySpinBrake(engine, ref angularVelocity, dt);
        }

        // Steers the velocity toward MaxVelocity along the heading. Faster forward motion is kept.
        private static void ApplyThrust(EngineDefinition engine, float throttle, Rot rotation, ref Vec2 velocity, float dt)
        {
            var forward = new Vec2(rotation.C, rotation.S);
            var maxVelocity = engine.MaxVelocity;

            var forwardVelocity = Vec2.Dot(velocity, forward);
            var requiredVelocity = forwardVelocity > maxVelocity ? forwardVelocity : maxVelocity;

            var direction = requiredVelocity * forward - velocity;
            var length = direction.Length;
            if (length < MinPropulsionDelta) return;

            var propulsionVector = direction * (1f / Math.Max(0.5f * length, maxVelocity));
            var acceleration = engine.Propulsion * throttle * propulsionVector;
            velocity = velocity + dt * acceleration;
        }

        private static void ApplyTurn(EngineDefinition engine, float course, float angle, ref float angularVelocity, float dt)
        {
            var turnRate = engine.TurnRate;
            if (!(turnRate > 0f)) return;

            var maxAngularVelocity = engine.MaxAngularVelocity;
            var minDeltaAngle = DeltaAngle(angle, course);

            float deltaAngle;
            if (minDeltaAngle > 0 && angularVelocity < 0)
                deltaAngle = FullCircle - minDeltaAngle;
            else if (minDeltaAngle < 0 && angularVelocity > 0)
                deltaAngle = FullCircle + minDeltaAngle;
            else
                deltaAngle = Math.Abs(minDeltaAngle);

            // Legacy has three bands per direction, but its first two use the same turn rate.
            float acceleration;
            if (deltaAngle < BrakeBand && deltaAngle < angularVelocity * angularVelocity / turnRate)
            {
                acceleration = Clamp(-angularVelocity, -turnRate * dt, turnRate * dt);
            }
            else if (minDeltaAngle < 0 && angularVelocity > -maxAngularVelocity * 1.5f)
            {
                var min = angularVelocity > -maxAngularVelocity ? -turnRate : -turnRate * 0.1f;
                acceleration = Math.Max(minDeltaAngle, min * dt);
            }
            else if (minDeltaAngle > 0 && angularVelocity < maxAngularVelocity * 1.5f)
            {
                var max = angularVelocity < maxAngularVelocity ? turnRate : turnRate * 0.1f;
                acceleration = Math.Min(minDeltaAngle, max * dt);
            }
            else
            {
                return;
            }

            angularVelocity = angularVelocity + acceleration;
        }

        private static void ApplySpinBrake(EngineDefinition engine, ref float angularVelocity, float dt)
        {
            var limit = engine.TurnRate * dt;
            angularVelocity = angularVelocity + Clamp(-angularVelocity, -limit, limit);
        }

        // Mathf.DeltaAngle in radians: the shortest signed angle from current to target, in [-pi, pi].
        internal static float DeltaAngle(float current, float target)
        {
            var delta = Repeat(target - current, FullCircle);
            if (delta > Math.PI) delta -= FullCircle;
            return delta;
        }

        private static float Repeat(float value, float length)
        {
            var result = value - (float)Math.Floor(value / length) * length;
            return result < 0f ? 0f : result > length ? length : result;
        }

        private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
    }
}
