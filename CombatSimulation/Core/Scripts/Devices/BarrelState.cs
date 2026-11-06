using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public enum TargetPriority { None, Low, Normal, High }

    // A value copy in BattleSnapshot. Angle is relative to the hull, in radians.
    public readonly struct BarrelState
    {
        public readonly float Angle;
        public readonly EntityId Target;
        internal BarrelState(Barrel barrel) { Angle = barrel.Angle; Target = barrel.Target; }
    }

    // Legacy WeaponPlatformBody. One state per mount, shared by every weapon on it.
    internal sealed class Barrel
    {
        public float Angle;
        public EntityId Target;
        public float IdleTime;
        public long LastSearchTick;
        public float Speed;
        public float Range;
        public float RelativeVelocity;
        public bool Initialized;

        public void Reset(BarrelDefinition definition, long tick)
        {
            Angle = definition.Angle;
            Target = default;
            IdleTime = 0f;
            LastSearchTick = tick - 50;
            Speed = Range = RelativeVelocity = 0f;
            Initialized = false;
        }
    }

    internal static class BarrelAiming
    {
        public static void Update(BattleWorld world, ShipEntity ship)
        {
            for (var i = 0; i < ship.Definition.Barrels.Count; ++i)
            {
                var definition = ship.Definition.Barrels[i];
                if (!(definition.AutoAimingAngle > 0f)) continue;
                var barrel = ship.GetBarrel(i);
                if (!barrel.Initialized)
                {
                    barrel.Initialized = true;
                    foreach (var device in ship.Definition.Devices)
                        if (BarrelIndex(device) == i)
                        {
                            Configure(barrel, device);
                            Search(world, ship, i);
                        }
                }
                barrel.IdleTime += Battle.TickDuration;
                Turn(world, ship, i, Battle.TickDuration);
                if (definition.IsTurret && barrel.Range > 0f) Search(world, ship, i);
            }

            // Pilot requests are consumed once. Repeated frames explicitly renew an ally target, as legacy does.
            for (var d = 0; d < ship.DeviceCount; ++d)
            {
                var index = BarrelIndex(ship.Definition.Devices[d]);
                if (index < 0 || !(ship.Definition.Barrels[index].AutoAimingAngle > 0f)) continue;
                if ((ship.TurretTargetSystems & (1UL << d)) != 0 && world.FindShip(ship.TurretTarget) != null)
                    SetTarget(world, ship, index, ship.TurretTarget);
                if ((ship.SearchTurretSystems & (1UL << d)) != 0)
                {
                    Configure(ship.GetBarrel(index), ship.Definition.Devices[d]);
                    Search(world, ship, index);
                }
            }
            ship.ClearTurretRequests();
        }

        public static void UpdateAttachments(BattleWorld world)
        {
            for (var i = 0; i < world.Bullets.Capacity; ++i)
            {
                if (!world.Bullets.IsAlive(i)) continue;
                ref var bullet = ref world.Bullets[i];
                if (bullet.IsBeam || !bullet.Definition.AttachedToParent) continue;
                var ship = world.FindShip(bullet.ParentId);
                if (ship == null || !(ship.Definition.Devices[bullet.DeviceIndex] is CannonDefinition cannon)) continue;
                var barrel = ship.Definition.Barrels[cannon.BarrelIndex];
                if (!(barrel.AutoAimingAngle > 0f)) continue;
                var angle = ship.GetBarrel(cannon.BarrelIndex).Angle;
                world.Physics.AttachBullet(bullet.Body, ship.Body, new Pose(barrel.PositionAt(angle), Rot.FromAngle(angle)));
            }
        }

        public static int BarrelIndex(DeviceDefinition device) => device is CannonDefinition cannon ? cannon.BarrelIndex
            : device is BeamDefinition beam ? beam.BarrelIndex : -1;

        public static void BeforeShot(BattleWorld world, ShipEntity ship, int index, BulletDefinition bullet, BeamDefinition beam)
        {
            if (index < 0 || !(ship.Definition.Barrels[index].AutoAimingAngle > 0f)) return;
            var barrel = ship.GetBarrel(index);
            barrel.Speed = bullet?.Speed ?? 0f;
            barrel.Range = bullet?.AimingRange ?? beam.Range;
            barrel.RelativeVelocity = bullet?.ParentVelocityEffect ?? 0f;
            Search(world, ship, index);
        }

        private static void Configure(Barrel barrel, DeviceDefinition device)
        {
            var cannon = device as CannonDefinition;
            barrel.Speed = cannon?.Bullet.Speed ?? 0f;
            barrel.Range = cannon?.Bullet.AimingRange ?? ((BeamDefinition)device).Range;
            barrel.RelativeVelocity = cannon?.Bullet.ParentVelocityEffect ?? 0f;
        }

        private static void Search(BattleWorld world, ShipEntity ship, int index)
        {
            var barrel = ship.GetBarrel(index);
            var active = world.FindShip(barrel.Target);
            if (active != null && Allegiances.IsHostile(ship.Allegiance, active.Allegiance) &&
                active.TargetPriority == TargetPriority.None) barrel.Target = default;
            if (world.Tick - barrel.LastSearchTick < 5) return;
            if (world.Tick - barrel.LastSearchTick < 50 && world.FindShip(barrel.Target) != null) return;

            var physics = world.Physics;
            var definition = ship.Definition.Barrels[index];
            var position = physics.GetPosition(ship.Body) + physics.GetRotation(ship.Body).Rotate(definition.PositionAt(barrel.Angle));
            var mountAngle = physics.GetAngle(ship.Body) + definition.Angle;
            ShipEntity best = null;
            var bestDistance = float.MaxValue;
            var validFound = false;
            for (var i = 0; i < world.ShipEntryCount; ++i)
            {
                var candidate = world.GetShipEntry(i);
                if (candidate == null || candidate == ship ||
                    !Allegiances.IsHostile(ship.Allegiance, candidate.Allegiance) ||
                    candidate.TargetPriority == TargetPriority.None) continue;
                var delta = physics.Torus.Delta(position, physics.GetPosition(candidate.Body));
                var distance = delta.Length - candidate.Definition.Scale * 0.5f;
                if (distance > barrel.Range * 2f) continue;
                var valid = distance <= barrel.Range && (definition.AutoAimingAngle >= Math.PI ||
                    Math.Abs(Delta(mountAngle, (float)Math.Atan2(delta.Y, delta.X))) <= definition.AutoAimingAngle);
                if (!valid && validFound) continue;
                if (best != null && valid == validFound &&
                    ((int)best.TargetPriority - (int)candidate.TargetPriority) * 300f +
                    Compare(distance, bestDistance) * 300f + Compare(best.Definition.Scale, candidate.Definition.Scale) * 200f > 0f)
                    continue;
                best = candidate;
                bestDistance = distance;
                validFound = valid;
            }
            SetTarget(world, ship, index, best?.Id ?? default);
        }

        private static float Compare(float first, float second) => Math.Max(first, second) > 0f
            ? (first - second) / Math.Max(first, second) : 0f;

        private static void SetTarget(BattleWorld world, ShipEntity ship, int index, EntityId target)
        {
            var barrel = ship.GetBarrel(index);
            barrel.LastSearchTick = world.Tick;
            if (barrel.Target == target) return;
            barrel.Target = target;
            if (!ship.Definition.Barrels[index].IsTurret && barrel.IdleTime > 0f && world.FindShip(target) != null)
                Turn(world, ship, index, barrel.IdleTime);
        }

        private static void Turn(BattleWorld world, ShipEntity ship, int index, float elapsed)
        {
            var barrel = ship.GetBarrel(index);
            var definition = ship.Definition.Barrels[index];
            var target = world.FindShip(barrel.Target);
            var desired = definition.Angle;
            if (target != null)
            {
                var physics = world.Physics;
                var position = physics.GetPosition(ship.Body) + physics.GetRotation(ship.Body).Rotate(definition.PositionAt(barrel.Angle));
                var delta = physics.Torus.Delta(position, physics.GetPosition(target.Body));
                if (barrel.Speed > 0f && delta.LengthSquared > target.Definition.Scale * target.Definition.Scale * 0.25f)
                {
                    var velocity = physics.GetLinearVelocity(target.Body) - barrel.RelativeVelocity * physics.GetLinearVelocity(ship.Body);
                    // Legacy centre intercept; the radius changes travel time, not the returned lead point.
                    var a = velocity.LengthSquared - barrel.Speed * barrel.Speed;
                    var b = 2f * Vec2.Dot(delta, velocity);
                    var c = delta.LengthSquared;
                    var discriminant = b * b - 4f * a * c;
                    if (discriminant >= 0f)
                    {
                        var root = (float)Math.Sqrt(discriminant);
                        var t1 = (-b - root) / (2f * a);
                        var t2 = (-b + root) / (2f * a);
                        var time = t1 < 0f ? t2 : t2 < 0f ? t1 : Math.Min(t1, t2);
                        if (time >= 0f && !float.IsInfinity(time)) delta += time * velocity;
                    }
                }
                var angle = (float)Math.Atan2(delta.Y, delta.X) - physics.GetAngle(ship.Body);
                desired += Math.Max(-definition.AutoAimingAngle, Math.Min(definition.AutoAimingAngle, Delta(desired, angle)));
                barrel.IdleTime = 0f;
            }
            var limit = definition.RotationSpeed * elapsed;
            barrel.Angle += Math.Max(-limit, Math.Min(limit, Delta(barrel.Angle, desired)));
        }

        internal static float Delta(float current, float target)
        {
            var circle = 2f * (float)Math.PI;
            var delta = (target - current) % circle;
            if (delta < 0f) delta += circle;
            return delta > Math.PI ? delta - circle : delta;
        }
    }
}
