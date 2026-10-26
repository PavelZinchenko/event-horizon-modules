using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy HomingController (Q2b), at stage 4 of every tick after the spawn tick: target search, thrust along the
    // current heading, then the turn. Physics never turns a bullet, so the core sets the heading the legacy body
    // reaches after its physics step.
    internal static class HomingSteering
    {
        // Legacy searches when more than 2 s have passed since the last search.
        public static readonly int SearchTicks = TickTime.After(BulletDefinition.SearchInterval);

        private static readonly float DeadZoneTan = (float)Math.Tan(BulletDefinition.TurnDeadZone);

        // Unity's Vector2.normalized returns zero below this length.
        private const float MinThrustDirection = 1e-5f;

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
                battle.Targets.FindMissileTarget(position, heading, bullet.Side,
                    definition.Range * BulletDefinition.SearchRangeFactor, definition.Radius, out var id);
                if (id != bullet.TargetId) battle.CaptureMissileTarget(bullet, id);
                bullet.TargetId = id;
                bullet.NextSearchTick = battle.Tick + SearchTicks;
                target = id.IsNull ? null : battle.FindShip(id);
            }

            // Legacy UpdateVelocity: below full speed along the heading, accelerate toward speed x heading.
            var velocity = world.GetBulletVelocity(bullet.Body);
            if (Vec2.Dot(velocity, heading) < definition.Speed)
            {
                var change = definition.Speed * heading - velocity;
                var length = change.Length;
                if (length > MinThrustDirection)
                    world.SetBulletVelocity(bullet.Body,
                        velocity + definition.Acceleration * Battle.TickDuration / length * change);
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
            for (var i = 0; i < count; ++i)
            {
                var heading = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
                if (Vec2.Dot(velocity, heading) < definition.Speed)
                {
                    var change = definition.Speed * heading - velocity;
                    var length = change.Length;
                    if (length > MinThrustDirection) velocity += definition.Acceleration * Battle.TickDuration / length * change;
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
    }
}
