using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy HomingController (Q2b), at stage 4 of every tick after the spawn tick: target search, thrust along the
    // current heading, then the turn. Physics never turns a bullet, so the core sets the heading the legacy body
    // reaches after its physics step. A magnetic bullet (legacy MagneticController, phase 5, step 4c) shares the search
    // and the network rules, but steers its velocity.
    internal static class HomingSteering
    {
        // Legacy searches when more than 2 s have passed since the last search.
        public static readonly int SearchTicks = TickTime.After(BulletDefinition.SearchInterval);

        private static readonly float DeadZoneTan = (float)Math.Tan(BulletDefinition.TurnDeadZone);

        // Unity's Vector2.normalized returns zero below this length.
        private const float MinThrustDirection = 1e-5f;

        // Legacy MagneticController.UpdateRotation: the angular velocity is 10 x the angle to the velocity, per second.
        private const float MagneticTurnFactor = 10f;

        public static void Update(Battle battle, ref BulletEntity bullet)
        {
            var world = battle.World;
            var definition = bullet.Definition;
            var position = world.GetBulletPosition(bullet.Body);
            var rotation = world.GetBulletRotation(bullet.Body);
            var heading = new Vec2(rotation.C, rotation.S);

            // Q2b redesign (owner, 2026-10-20): a removed target starts a search at once, not up to 2 s later.
            // Every search restarts the period, as the legacy timer does.
            var target = bullet.TargetId.IsNull ? null : battle.FindShip(bullet.TargetId);
            if (battle.Tick >= bullet.NextSearchTick || (target == null && !bullet.TargetId.IsNull))
            {
                battle.Targets.FindMissileTarget(position, heading, bullet.Allegiance,
                    definition.Range * BulletDefinition.SearchRangeFactor, definition.Radius, out var id,
                    definition.IsMagnetic && !definition.HasDirection);
                if (id != bullet.TargetId) battle.CaptureMissileTarget(bullet, id);
                bullet.TargetId = id;
                bullet.NextSearchTick = battle.Tick + SearchTicks;
                target = id.IsNull ? null : battle.FindShip(id);
            }

            var velocity = world.GetBulletVelocity(bullet.Body);
            if (definition.IsMagnetic)
            {
                var thrusted = target == null ? velocity : MagneticThrust(velocity,
                    world.Torus.Delta(position, world.GetPosition(target.Body)), world.GetLinearVelocity(target.Body),
                    bullet.Speed, bullet.Acceleration);
                if (thrusted.X != velocity.X || thrusted.Y != velocity.Y) world.SetBulletVelocity(bullet.Body, thrusted);
                if (!definition.HasDirection) return;
                var angle = TurnToVelocity(rotation.Angle, thrusted);
                if (angle != rotation.Angle) world.SetBulletPose(bullet.Body, position, angle);
                return;
            }

            // Legacy UpdateVelocity: below full speed along the heading, accelerate toward speed x heading.
            if (Vec2.Dot(velocity, heading) < bullet.Speed)
            {
                var change = bullet.Speed * heading - velocity;
                var length = change.Length;
                if (length > MinThrustDirection)
                    world.SetBulletVelocity(bullet.Body,
                        velocity + bullet.Acceleration * Battle.TickDuration / length * change);
            }

            // Legacy UpdateRotation: full turn rate toward the target unless it is within the dead zone.
            if (target == null) return;

            var delta = world.Torus.Delta(position, world.GetPosition(target.Body));
            var along = Vec2.Dot(heading, delta);
            var across = Vec2.Cross(heading, delta);
            if (along == 0f && across == 0f) return;
            if (along > 0f && Math.Abs(across) <= along * DeadZoneTan) return;

            var turn = across >= 0f ? definition.TurnRate : -definition.TurnRate;
            world.SetBulletPose(bullet.Body, position, rotation.Angle + turn * Battle.TickDuration);
        }

        // Network plan, P3: a remote missile's state at the end of an owner tick, advanced by count ticks as stage 4
        // and physics move it, toward the target's present position and without a search or hit tests.
        public static void Advance(Battle battle, in BulletEntity bullet, int count, ref Vec2 position, ref float angle,
            ref Vec2 velocity)
        {
            var world = battle.World;
            var definition = bullet.Definition;
            var target = bullet.TargetId.IsNull ? null : battle.FindShip(bullet.TargetId);
            var targetPosition = target == null ? Vec2.Zero : world.GetPosition(target.Body);
            var targetVelocity = target == null ? Vec2.Zero : world.GetLinearVelocity(target.Body);
            for (var i = 0; i < count; ++i)
            {
                if (definition.IsMagnetic)
                {
                    if (target != null)
                        velocity = MagneticThrust(velocity, world.Torus.Delta(position, targetPosition), targetVelocity,
                            bullet.Speed, bullet.Acceleration);
                    if (definition.HasDirection) angle = TurnToVelocity(angle, velocity);
                    position = world.Torus.Wrap(position + Battle.TickDuration * velocity);
                    continue;
                }

                var heading = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
                if (Vec2.Dot(velocity, heading) < bullet.Speed)
                {
                    var change = bullet.Speed * heading - velocity;
                    var length = change.Length;
                    if (length > MinThrustDirection) velocity += bullet.Acceleration * Battle.TickDuration / length * change;
                }
                if (target != null)
                {
                    var delta = world.Torus.Delta(position, targetPosition);
                    var along = Vec2.Dot(heading, delta);
                    var across = Vec2.Cross(heading, delta);
                    if ((along != 0f || across != 0f) && !(along > 0f && Math.Abs(across) <= along * DeadZoneTan))
                        angle += (across >= 0f ? definition.TurnRate : -definition.TurnRate) * Battle.TickDuration;
                }
                position = world.Torus.Wrap(position + Battle.TickDuration * velocity);
            }
        }

        // Legacy MagneticController.UpdateVelocity (phase 5, step 4c): full thrust toward the velocity of Speed that
        // leads to the target, aimed at the target position plus its velocity x the time to cover the distance at
        // Speed. The thrust has full size every tick, so the speed jitters around Speed (legacy). Legacy measures the
        // distance without the torus wrap; the core uses the nearest image, as the homing turn does.
        internal static Vec2 MagneticThrust(Vec2 velocity, Vec2 delta, Vec2 targetVelocity, float speed, float acceleration)
        {
            var lead = speed > 0f ? delta + delta.Length / speed * targetVelocity : delta;
            var change = speed * Normalized(lead) - velocity;
            return velocity + acceleration * Battle.TickDuration * Normalized(change);
        }

        // Legacy MagneticController.UpdateRotation without a parent: the heading turns toward the velocity, with or
        // without a target. A zero velocity points at angle 0 (legacy RotationHelpers.Angle).
        internal static float TurnToVelocity(float angle, Vec2 velocity)
        {
            var delta = Battle.WrapAngle((float)Math.Atan2(velocity.Y, velocity.X) - angle);
            return delta == 0f ? angle : angle + MagneticTurnFactor * Battle.TickDuration * delta;
        }

        private static Vec2 Normalized(Vec2 value)
        {
            var length = value.Length;
            return length > MinThrustDirection ? new Vec2(value.X / length, value.Y / length) : Vec2.Zero;
        }
    }
}
