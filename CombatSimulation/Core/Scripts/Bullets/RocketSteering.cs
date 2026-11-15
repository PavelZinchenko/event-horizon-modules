using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy RocketController (phase 5, step 4a), at stage 4 of every tick after the spawn tick: thrust toward
    // max(forward speed, Speed) along the heading. The thrust has full size until the velocity matches, so it also
    // removes the sideways speed, and the forward speed can end up to one tick of thrust above Speed (legacy).
    internal static class RocketSteering
    {
        // Unity's Vector2.normalized returns zero below this length.
        private const float MinThrustDirection = 1e-5f;

        public static void Update(PhysicsWorld world, ref BulletEntity bullet)
        {
            var heading = world.GetBulletRotation(bullet.Body);
            var velocity = world.GetBulletVelocity(bullet.Body);
            var thrusted = Thrust(velocity, new Vec2(heading.C, heading.S), bullet.Speed, bullet.Acceleration);
            if (thrusted.X != velocity.X || thrusted.Y != velocity.Y) world.SetBulletVelocity(bullet.Body, thrusted);
        }

        public static Vec2 Thrust(Vec2 velocity, Vec2 heading, float speed, float acceleration)
        {
            var forward = Vec2.Dot(velocity, heading);
            var change = (forward > speed ? forward : speed) * heading - velocity;
            var length = change.Length;
            return length > MinThrustDirection ? velocity + acceleration * Battle.TickDuration / length * change : velocity;
        }

        // A late spawn (a remote record): the projectile's position and velocity at the start of the present tick. A
        // late record applies at stage 1, so stage 4 of the present tick still thrusts the rocket or launches the beam
        // bolt (phase 5, step 4e); stage 4 first acts in the tick after the spawn tick.
        public static void Advance(Torus torus, BulletDefinition definition, float speedScale, float angle,
            long spawnTick, long tick, ref Vec2 position, ref Vec2 velocity)
        {
            var heading = definition.IsRocket || definition.IsBeamBolt
                ? new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle))
                : default;
            var growth = definition.GrowthTicks(speedScale);
            for (var t = spawnTick; t < tick; ++t)
            {
                if (definition.IsRocket && t > spawnTick)
                    velocity = Thrust(velocity, heading, definition.Speed * speedScale, definition.Acceleration * speedScale);
                else if (definition.IsBeamBolt && t - spawnTick == growth)
                    velocity += definition.Speed * speedScale * heading;
                position = torus.Wrap(position + Battle.TickDuration * velocity);
            }
        }
    }
}
