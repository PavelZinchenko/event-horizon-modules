using System;
using CombatSimulation.Core;
using CombatSimulation.Physics;

namespace CombatSimulation.Ai
{
    // Legacy ShipNavigationHandler on the snapshot. Offsets use the shortest torus offset, as legacy WrapDirection does;
    // angles are radians. The engine values are the full engine (the snapshot does not show the engine without energy).
    internal static class Navigation
    {
        public enum Status
        {
            NoTarget,
            Following,
            Chasing,
            FullThrottle,
        }

        private const float ThrustAngle = 30 * LegacyMath.Degree;
        private const float KeepDistanceThrustAngle = 10 * LegacyMath.Degree;
        private const float ReachVelocityThrustAngle = 60 * LegacyMath.Degree;

        // Legacy TryInterceptTarget gives up after this time.
        private const float FollowTime = 60f;

        // Legacy FlyAround: true while the target is between min and max; else steers along a tangent (outside) or away
        // (inside). Spin below 0 turns left, above 0 right, 0 the nearer way.
        public static bool FlyAround(AiContext context, in ShipState target, float min, float max, int spin)
        {
            var ship = context.Ship;
            var minDistance = min + context.Profile.ModelScale / 3 + context.ProfileOf(target).ModelScale / 3;
            var maxDistance = minDistance - min + max;

            var direction = context.Delta(ship.Position, target.Position);
            var alpha = LegacyMath.Angle(direction);

            var distance = direction.Length;
            if (distance >= minDistance && distance <= maxDistance)
                return true;

            var controls = context.Controls;
            if (controls.MovementLocked)
                return false;

            float delta;
            if (distance > maxDistance)
                delta = (float)Math.Asin(minDistance / distance);
            else
                delta = (float)(0.5 * Math.PI) + (float)(0.25 * Math.PI) * (minDistance - distance) / minDistance;

            if (spin < 0)
                alpha += delta;
            else if (spin > 0)
                alpha -= delta;
            else if (LegacyMath.DeltaAngle(ship.Angle, alpha) < 0)
                alpha += delta;
            else
                alpha -= delta;

            controls.Course = alpha;
            if (Math.Abs(LegacyMath.DeltaAngle(alpha, ship.Angle)) < ThrustAngle)
                controls.Thrust = 1;

            return false;
        }

        // Legacy KeepDistance: true while the target is between min and max; else steers toward the velocity that
        // backs off (inside) or brakes in time (outside).
        public static bool KeepDistance(AiContext context, in ShipState target, float min, float max)
        {
            var ship = context.Ship;
            var engine = ship.Definition.Engine;

            var shipSummarySize = context.Profile.ModelScale / 3 + context.ProfileOf(target).ModelScale / 3;
            var minDistance = min + shipSummarySize;
            var maxDistance = max + shipSummarySize;

            var direction = context.Delta(ship.Position, target.Position);
            var distance = direction.Length;
            if (distance >= minDistance && distance <= maxDistance)
                return true;

            var controls = context.Controls;
            if (controls.MovementLocked)
                return false;

            Vec2 requiredVelocity;
            if (distance < minDistance)
            {
                var excess = (1 + minDistance - distance) / (1 + minDistance);
                requiredVelocity = -LegacyMath.Normalize(direction) * excess * engine.MaxVelocity;
            }
            else
            {
                var length = distance - maxDistance;
                var speed = (float)Math.Sqrt(2 * length * engine.Propulsion);
                requiredVelocity = LegacyMath.Normalize(direction) * speed;
            }

            var newVelocity = requiredVelocity - ship.LinearVelocity;
            var course = LegacyMath.Angle(newVelocity);

            controls.Course = course;
            if (Math.Abs(LegacyMath.DeltaAngle(course, ship.Angle)) < KeepDistanceThrustAngle)
                controls.Thrust = newVelocity.Length / engine.Propulsion;

            return false;
        }

        // Legacy ChaseAndRam: heads for the intercept point at full speed. A target that left the battle or hides
        // (AttackHelpers.CantDetectTarget) gives NoTarget. The delta angle is in radians.
        public static Status ChaseAndRam(AiContext context, EntityId enemyId, out float timeToHit, out float deltaAngle)
        {
            deltaAngle = float.MaxValue;
            timeToHit = float.MaxValue;
            if (!context.TryGetShip(enemyId, out var enemy)) return Status.NoTarget;
            if (context.IsEnemy(enemy) && AimAndAttack.IsHidden(context, enemy)) return Status.NoTarget;

            var controls = context.Controls;
            if (controls.MovementLocked) return Status.Chasing;

            var ship = context.Ship;
            var canHit = TryInterceptTarget(context, enemy, out var target, out timeToHit);
            var status = canHit ? Status.Chasing : Status.Following;

            var course = LegacyMath.Angle(target - ship.Position);
            controls.Course = course;

            deltaAngle = Math.Abs(LegacyMath.DeltaAngle(ship.Angle, course));
            if (deltaAngle < ThrustAngle)
            {
                status = Status.FullThrottle;
                controls.Thrust = 1.0f;
            }

            return status;
        }

        // Legacy TryReachVelocity: true while the velocity is within the threshold of the desired one.
        public static bool TryReachVelocity(AiContext context, Vec2 desiredVelocity, float speedThreshold)
        {
            var ship = context.Ship;
            var relativeVelocity = ship.LinearVelocity - desiredVelocity;

            var speed = relativeVelocity.Length;
            if (speed <= speedThreshold)
                return true;

            var controls = context.Controls;
            if (controls.MovementLocked)
                return false;

            var course = LegacyMath.Angle(-relativeVelocity);
            controls.Course = course;

            var deltaAngle = Math.Abs(LegacyMath.DeltaAngle(ship.Angle, course));
            if (deltaAngle < ReachVelocityThrustAngle)
                controls.Thrust = speed / ship.Definition.Engine.MaxVelocity;

            return false;
        }

        // Legacy TryInterceptTarget: the target point is unwrapped around the ship.
        private static bool TryInterceptTarget(AiContext context, in ShipState enemy, out Vec2 target, out float timeToHit)
        {
            var ship = context.Ship;
            var enemyPosition = ship.Position + context.Delta(ship.Position, enemy.Position);
            var maxVelocity = Math.Max(ship.LinearVelocity.Length, ship.Definition.Engine.MaxVelocity);

            var canHit = LegacyMath.TryIntercept(enemyPosition, enemy.LinearVelocity, ship.Position, maxVelocity,
                out target, out timeToHit);

            if (!canHit)
            {
                target = enemyPosition + enemy.LinearVelocity;
                timeToHit = FollowTime;
            }

            return canHit;
        }
    }
}
