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

        public static void Update(PhysicsWorld world, ShipEntity ship, float dt)
        {
            var engine = ship.EngineHasEnergy ? ship.Definition.Engine : ship.Definition.EngineWithoutEnergy;
            if (engine == null) return;

            var body = ship.Body;
            if (ship.Throttle > MinThrottle) ApplyThrust(world, body, engine, ship.Throttle, dt);

            if (ship.HasCourse)
                ApplyTurn(world, body, engine, ship.Course, dt);
            else if (Math.Abs(world.GetAngularVelocity(body)) > MinSpin)
                ApplySpinBrake(world, body, engine, dt);
        }

        // Steers the velocity toward MaxVelocity along the heading. Faster forward motion is kept.
        private static void ApplyThrust(PhysicsWorld world, BodyId body, EngineDefinition engine, float throttle, float dt)
        {
            var rotation = world.GetRotation(body);
            var forward = new Vec2(rotation.C, rotation.S);
            var velocity = world.GetLinearVelocity(body);
            var maxVelocity = engine.MaxVelocity;

            var forwardVelocity = Vec2.Dot(velocity, forward);
            var requiredVelocity = forwardVelocity > maxVelocity ? forwardVelocity : maxVelocity;

            var direction = requiredVelocity * forward - velocity;
            var length = direction.Length;
            if (length < MinPropulsionDelta) return;

            var propulsionVector = direction * (1f / Math.Max(0.5f * length, maxVelocity));
            var acceleration = engine.Propulsion * throttle * propulsionVector;
            world.SetLinearVelocity(body, velocity + dt * acceleration);
        }

        private static void ApplyTurn(PhysicsWorld world, BodyId body, EngineDefinition engine, float course, float dt)
        {
            var turnRate = engine.TurnRate;
            if (!(turnRate > 0f)) return;

            var angularVelocity = world.GetAngularVelocity(body);
            var maxAngularVelocity = engine.MaxAngularVelocity;
            var minDeltaAngle = DeltaAngle(world.GetAngle(body), course);

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

            world.SetAngularVelocity(body, angularVelocity + acceleration);
        }

        private static void ApplySpinBrake(PhysicsWorld world, BodyId body, EngineDefinition engine, float dt)
        {
            var angularVelocity = world.GetAngularVelocity(body);
            var limit = engine.TurnRate * dt;
            world.SetAngularVelocity(body, angularVelocity + Clamp(-angularVelocity, -limit, limit));
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
