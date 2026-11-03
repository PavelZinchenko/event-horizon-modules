using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    // Legacy ShipWeaponList: a weapon selection with its shortest and longest range.
    public sealed class AiWeaponList
    {
        public AiWeaponList(IReadOnlyList<AiWeapon> weapons)
        {
            List = weapons ?? Array.Empty<AiWeapon>();
            foreach (var weapon in List)
            {
                if (weapon.Range > RangeMax) RangeMax = weapon.Range;
                if (weapon.Range < RangeMin || RangeMin <= 0) RangeMin = weapon.Range;
            }
        }

        public IReadOnlyList<AiWeapon> List { get; }
        public float RangeMin { get; }
        public float RangeMax { get; }
    }

    // Legacy angle and vector helpers in radians: RotationHelpers.Angle, Mathf.DeltaAngle, Vector2.normalized and
    // Geometry.GetTargetPosition.
    internal static class LegacyMath
    {
        public const float Degree = (float)(Math.PI / 180.0);
        public const float FullCircle = (float)(2.0 * Math.PI);
        private const float HalfCircle = (float)Math.PI;

        // Vector2.normalized gives zero below this length.
        private const float NormalizeEpsilon = 1e-5f;

        // Legacy RotationHelpers.Angle: the heading of a direction, in [0, 2π).
        public static float Angle(Vec2 direction)
        {
            var result = (float)Math.Atan2(direction.Y, direction.X);
            if (result < 0f) result += FullCircle;
            return result;
        }

        // Legacy Mathf.DeltaAngle: the shortest turn from current to target, in (−π, π].
        public static float DeltaAngle(float current, float target)
        {
            var delta = Repeat(target - current, FullCircle);
            if (delta > HalfCircle) delta -= FullCircle;
            return delta;
        }

        public static Vec2 Direction(float angle) => new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));

        public static Vec2 Normalize(Vec2 vector)
        {
            var length = vector.Length;
            return length > NormalizeEpsilon ? vector * (1f / length) : Vec2.Zero;
        }

        public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        // Legacy Geometry.GetTargetPosition without the enemy radius: where a bullet of the given speed from the gun meets
        // an enemy that keeps its velocity.
        public static bool TryIntercept(Vec2 enemyPosition, Vec2 enemyVelocity, Vec2 gunPosition, float bulletSpeed,
            out Vec2 position, out float timeInterval)
        {
            var offset = enemyPosition - gunPosition;
            var a = enemyVelocity.LengthSquared - bulletSpeed * bulletSpeed;
            var b = 2 * offset.X * enemyVelocity.X + 2 * offset.Y * enemyVelocity.Y;
            var c = offset.LengthSquared;

            if (SolveQuadraticEquation(a, b, c, out var t1, out var t2) && (t1 >= 0 || t2 >= 0))
            {
                timeInterval = t1 < 0 ? t2 : t2 < 0 ? t1 : t1 < t2 ? t1 : t2;
                position = enemyPosition + enemyVelocity * timeInterval;
                return true;
            }

            position = Vec2.Zero;
            timeInterval = 0;
            return false;
        }

        // Legacy Geometry.GetTargetPosition with the enemy radius: the bullet needs to reach the enemy's edge only.
        public static bool TryIntercept(Vec2 enemyPosition, Vec2 enemyVelocity, Vec2 gunPosition, float bulletSpeed,
            float enemyRadius, out Vec2 position, out float timeInterval)
        {
            const float epsilon = 0.01f;

            var relativePosition = enemyPosition - gunPosition;
            var sqrDistance = relativePosition.LengthSquared;
            if (sqrDistance <= enemyRadius * enemyRadius)
            {
                position = enemyPosition;
                timeInterval = 0;
                return true;
            }

            var a = enemyVelocity.LengthSquared - bulletSpeed * bulletSpeed;
            var b = 2 * (relativePosition.X * enemyVelocity.X + relativePosition.Y * enemyVelocity.Y);
            var c = sqrDistance;

            if (!SolveQuadraticEquation(a, b, c, out var t1, out var t2) || t1 < 0 && t2 < 0)
            {
                position = Vec2.Zero;
                timeInterval = 0;
                return false;
            }

            timeInterval = t1 < t2 ? (t1 < 0 ? t2 : t1) : (t2 < 0 ? t1 : t2);
            position = enemyPosition + enemyVelocity * timeInterval;
            if (timeInterval <= epsilon) return true;

            var distance = (position - gunPosition).Length;
            timeInterval -= timeInterval * (enemyRadius / distance);
            return true;
        }

        // Mathf.Repeat.
        private static float Repeat(float value, float length)
        {
            var result = value - (float)Math.Floor(value / length) * length;
            return result < 0f ? 0f : result > length ? length : result;
        }

        private static bool SolveQuadraticEquation(float a, float b, float c, out float x1, out float x2)
        {
            x1 = 0;
            x2 = 0;

            var d = b * b - 4 * a * c;
            if (d < 0)
                return false;

            var root = (float)Math.Sqrt(d);
            x1 = (-b - root) / (2 * a);
            x2 = (-b + root) / (2 * a);
            return true;
        }
    }

    // The core facts of one AI weapon: its barrel in world space and what legacy WeaponInfo reads from the bullet. A beam
    // has no bullet, so its speed and velocity effect are 0; legacy aims beams directly.
    internal readonly struct WeaponMount
    {
        public readonly Vec2 Position;
        public readonly float MountAngle;
        public readonly float BulletSpeed;
        public readonly float RelativeVelocityEffect;
        public readonly float EnergyCost;
        public readonly float FireRate;

        private WeaponMount(Vec2 position, float mountAngle, float bulletSpeed, float relativeVelocityEffect,
            float energyCost, float fireRate)
        {
            Position = position;
            MountAngle = mountAngle;
            BulletSpeed = bulletSpeed;
            RelativeVelocityEffect = relativeVelocityEffect;
            EnergyCost = energyCost;
            FireRate = fireRate;
        }

        // Legacy Platform.Body.WorldPosition is the muzzle point, as the core barrel position is. A cooldown of 0 maps a
        // fire rate of 0 (step 3a, row 3).
        public static WeaponMount Of(in ShipState ship, AiWeapon weapon)
        {
            var definition = ship.Definition;
            switch (definition.Devices[weapon.DeviceIndex])
            {
                case CannonDefinition cannon:
                    return Create(ship, definition.Barrels[cannon.BarrelIndex], cannon.Bullet.Speed,
                        cannon.Bullet.ParentVelocityEffect, cannon.EnergyCost, FireRateOf(cannon.Cooldown));
                case BeamDefinition beam:
                    // Legacy WeaponInfo.EnergyCost: per second for a continuous beam, per shot for a shot beam.
                    return Create(ship, definition.Barrels[beam.BarrelIndex], 0f, 0f,
                        beam.IsShot ? beam.ActivationCost : beam.EnergyPerSecond, FireRateOf(beam.Cooldown));
                default:
                    throw new InvalidOperationException($"AI weapon at device {weapon.DeviceIndex} is not a cannon or a beam");
            }
        }

        // Legacy IWeaponPlatform.OptimalShipCourse: the ship heading that points the barrel at the target.
        public float OptimalShipCourse(Vec2 target) => LegacyMath.Angle(target - Position) - MountAngle;

        private static WeaponMount Create(in ShipState ship, BarrelDefinition barrel, float bulletSpeed,
            float relativeVelocityEffect, float energyCost, float fireRate) =>
            new WeaponMount(ship.Position + Rot.FromAngle(ship.Angle).Rotate(barrel.Position), barrel.Angle, bulletSpeed,
                relativeVelocityEffect, energyCost, fireRate);

        private static float FireRateOf(float cooldown) => cooldown > 0f ? 1f / cooldown : 0f;
    }

    // Legacy AimAndAttackHandler with the WeaponWrapper strategies (WeaponWrapperExtensions) and TargetingHelpers.
    // Positions are unwrapped around the gun, so the torus offset counts. Auto-aiming barrels and turret targets wait
    // for D9: AutoAimingAngle is 0 and no platform has an active target.
    internal static class AimAndAttack
    {
        [Flags]
        public enum State
        {
            Failed = 0,
            Aiming = 1,
            Attacking = 2,
        }

        private enum AimingStrategy
        {
            NoAiming,
            AimDirectly,
            AimWithPrediction,
        }

        private enum ActivationStrategy
        {
            Always,
            WhenInRange,
            WhenAimedRoughly,
            WhenAimedPrecisely,
        }

        private struct AimingData
        {
            public bool InRange;
            public bool ReadyToFire;
            public float Distance;
            public float? Angle;
            public Vec2 Position;
        }

        private const float AimingAngleErrorMargin = 5 * LegacyMath.Degree;

        // Legacy AttackWhileStandingStill: fires the weapons that are aimed; the course is left alone.
        public static State AttackWhileStandingStill(AiContext context, EntityId enemyId, AiWeaponList weapons)
        {
            if (!context.TryGetShip(enemyId, out var enemy))
                return State.Failed;

            var activated = 0;
            foreach (var weapon in weapons.List)
            {
                var aiming = new AimingData();
                if (IsReadyToFire(context, weapon) && IsValidTarget(weapon, enemy))
                {
                    var mount = WeaponMount.Of(context.Ship, weapon);
                    Aim(context, weapon, mount, enemy, ref aiming);
                    Attack(context, weapon, mount, enemy, ref aiming);
                }

                if (!aiming.ReadyToFire) continue;
                if (aiming.InRange) activated++;
                Activate(context.Controls, weapon);
            }

            return activated > 0 ? State.Attacking : State.Failed;
        }

        // Legacy AttackWithAllWeapons: fires the weapons that are aimed and turns to the aimed weapon whose course is
        // nearest to the heading; a later weapon wins a tie.
        public static State AttackWithAllWeapons(AiContext context, EntityId enemyId, AiWeaponList weapons)
        {
            if (!context.TryGetShip(enemyId, out var enemy))
                return State.Failed;

            var targetAngle = 0f;
            var targetDeltaAngle = float.MaxValue;
            var setCourse = false;
            var aimingCount = 0;
            var activated = 0;
            var shipRotation = context.Ship.Angle;

            foreach (var weapon in weapons.List)
            {
                if (!IsReadyToFire(context, weapon)) continue;
                if (!IsValidTarget(weapon, enemy)) continue;

                var mount = WeaponMount.Of(context.Ship, weapon);
                var aiming = new AimingData();
                Aim(context, weapon, mount, enemy, ref aiming);
                Attack(context, weapon, mount, enemy, ref aiming);

                if (aiming.ReadyToFire)
                {
                    if (aiming.InRange) activated++;
                    Activate(context.Controls, weapon);
                }

                if (aiming.Angle.HasValue)
                {
                    aimingCount++;
                    var delta = Math.Abs(LegacyMath.DeltaAngle(aiming.Angle.Value, shipRotation));
                    if (delta <= targetDeltaAngle)
                    {
                        targetAngle = aiming.Angle.Value;
                        targetDeltaAngle = delta;
                        setCourse = true;
                    }
                }
            }

            if (setCourse)
                context.Controls.Course = targetAngle;

            return activated > 0 ? State.Attacking : aimingCount > 0 ? State.Aiming : State.Failed;
        }

        // Legacy AttackHelpers.TryGetTarget (SustainAim): an ally is always visible, and the projectile range allows a
        // tenth of both model scales.
        public static bool TryGetSustainTarget(AiContext context, AiWeapon weapon, in WeaponMount mount, in ShipState enemy,
            out Vec2 target)
        {
            target = Vec2.Zero;
            var ship = context.Ship;
            if (context.IsEnemy(enemy) && IsHidden(context, enemy))
                return false;

            var enemyPosition = mount.Position + context.Delta(mount.Position, enemy.Position);
            if (weapon.BulletBehavior != AiBulletBehavior.Projectile)
            {
                target = enemyPosition;
                return weapon.Range >= (enemyPosition - mount.Position).Length - context.ProfileOf(enemy).ModelScale * 0.4f;
            }

            var velocity = enemy.LinearVelocity - ship.LinearVelocity * mount.RelativeVelocityEffect;
            if (!LegacyMath.TryIntercept(enemyPosition, velocity, mount.Position, mount.BulletSpeed, out target, out var time))
                return false;

            return weapon.Range + 0.1f * (context.Profile.ModelScale + context.ProfileOf(enemy).ModelScale) >= time * mount.BulletSpeed;
        }

        // Legacy TargetingHelpers.CantDetectTarget, also AttackHelpers.CantDetectTarget for an enemy: a ship without
        // target priority is seen within 5 units plus its model scale.
        public static bool IsHidden(AiContext context, in ShipState enemy)
        {
            var profile = context.ProfileOf(enemy);
            if (profile.TargetPriority != AiTargetPriority.None)
                return false;

            return context.Distance(context.Ship, enemy) > 5 + profile.ModelScale;
        }

        // Legacy WeaponWrapperExtensions.IsReadyToFire on the device state (step 3b). Charging weapons wait for phase 5
        // (no charge in the snapshot); harpoons are inert in phase 4, so no harpoon has a bullet out.
        public static bool IsReadyToFire(AiContext context, AiWeapon weapon)
        {
            if (!context.DeviceOf(weapon.DeviceIndex).Ready) return false;
            if (weapon.WeaponClass == AiWeaponClass.RequiredCharging) return false;
            return true;
        }

        // Legacy WeaponWrapperExtensions.Activate: a charging weapon fires when its bit is released.
        public static void Activate(AiControls controls, AiWeapon weapon) =>
            controls.ActivateSystem(weapon.DeviceIndex, weapon.WeaponClass != AiWeaponClass.RequiredCharging);

        // Legacy WeaponWrapperExtensions.IsValidTarget: a drone catcher aims at drones only.
        private static bool IsValidTarget(AiWeapon weapon, in ShipState target) =>
            (weapon.Capability & AiWeaponCapability.CaptureDrone) != AiWeaponCapability.CaptureDrone || target.IsDrone;

        private static void Aim(AiContext context, AiWeapon weapon, in WeaponMount mount, in ShipState enemy, ref AimingData aiming)
        {
            aiming.Angle = null;

            switch (AimingOf(context, weapon))
            {
                case AimingStrategy.AimDirectly:
                    aiming.InRange = TryGetDirectTarget(context, weapon, mount, enemy, out aiming.Position, out aiming.Distance);
                    break;
                case AimingStrategy.AimWithPrediction:
                    aiming.InRange = TryGetProjectileTarget(context, weapon, mount, enemy, out aiming.Position, out aiming.Distance);
                    break;
                default:
                    aiming.InRange = IsInAttackRange(context, weapon, enemy);
                    return;
            }

            if (aiming.InRange)
                aiming.Angle = mount.OptimalShipCourse(aiming.Position);
        }

        private static void Attack(AiContext context, AiWeapon weapon, in WeaponMount mount, in ShipState enemy, ref AimingData aiming)
        {
            var activation = ActivationOf(weapon, mount);
            if (activation == ActivationStrategy.Always)
                aiming.ReadyToFire = true;
            else if (activation == ActivationStrategy.WhenInRange)
                aiming.ReadyToFire = aiming.InRange;
            else if (!aiming.Angle.HasValue)
                aiming.ReadyToFire = false;
            else
            {
                var delta = Math.Abs(LegacyMath.DeltaAngle(aiming.Angle.Value, context.Ship.Angle));
                var spread = CalculateSpread(aiming.Distance, context.ProfileOf(enemy).ModelScale,
                    AimingAngleErrorMargin + weapon.Spread / 2);

                if (delta <= spread)
                    aiming.ReadyToFire = true;
            }
        }

        // Legacy WeaponWrapper.DetermineAimingStrategy: below Hard, projectiles aim without prediction.
        private static AimingStrategy AimingOf(AiContext context, AiWeapon weapon)
        {
            switch (weapon.BulletBehavior)
            {
                case AiBulletBehavior.Homing:
                case AiBulletBehavior.AreaOfEffect:
                case AiBulletBehavior.Trap:
                    return AimingStrategy.NoAiming;
                case AiBulletBehavior.Projectile:
                case AiBulletBehavior.Harpoon:
                    return context.Settings.AiLevel < AiDifficultyLevel.Hard ? AimingStrategy.AimDirectly : AimingStrategy.AimWithPrediction;
                default:
                    return AimingStrategy.AimDirectly;
            }
        }

        // Legacy WeaponWrapper.DetermineActivationStrategy; no barrel auto-aims yet (D9).
        private static ActivationStrategy ActivationOf(AiWeapon weapon, in WeaponMount mount)
        {
            if (weapon.WeaponClass == AiWeaponClass.Continuous && !(mount.EnergyCost > 0))
                return ActivationStrategy.Always;

            switch (weapon.BulletBehavior)
            {
                case AiBulletBehavior.Trap:
                    return ActivationStrategy.Always;
                case AiBulletBehavior.Homing:
                case AiBulletBehavior.AreaOfEffect:
                    return ActivationStrategy.WhenInRange;
                case AiBulletBehavior.Projectile:
                    return mount.FireRate > 1.0f ? ActivationStrategy.WhenAimedRoughly : ActivationStrategy.WhenAimedPrecisely;
                case AiBulletBehavior.Harpoon:
                    return ActivationStrategy.WhenAimedPrecisely;
                default:
                    return ActivationStrategy.WhenAimedRoughly;
            }
        }

        // Legacy TargetingHelpers.TryGetProjectileTarget.
        private static bool TryGetProjectileTarget(AiContext context, AiWeapon weapon, in WeaponMount mount, in ShipState enemy,
            out Vec2 target, out float distance)
        {
            target = Vec2.Zero;
            distance = 0;
            if (IsHidden(context, enemy))
                return false;

            var enemyScale = context.ProfileOf(enemy).ModelScale;
            var enemyPosition = mount.Position + context.Delta(mount.Position, enemy.Position);
            var velocity = enemy.LinearVelocity - context.Ship.LinearVelocity * mount.RelativeVelocityEffect;
            if (!LegacyMath.TryIntercept(enemyPosition, velocity, mount.Position, mount.BulletSpeed, enemyScale * 0.4f,
                    out target, out var timeInterval))
                return false;

            distance = mount.BulletSpeed * timeInterval;
            return weapon.Range >= distance;
        }

        // Legacy TargetingHelpers.TryGetDirectTarget.
        private static bool TryGetDirectTarget(AiContext context, AiWeapon weapon, in WeaponMount mount, in ShipState enemy,
            out Vec2 target, out float distance)
        {
            target = Vec2.Zero;
            distance = 0;
            if (IsHidden(context, enemy))
                return false;

            target = mount.Position + context.Delta(mount.Position, enemy.Position);
            distance = (target - mount.Position).Length - context.ProfileOf(enemy).ModelScale * 0.4f;
            return weapon.Range >= distance;
        }

        // Legacy WeaponWrapperExtensions.IsInAttackRange, sign included: the target's half scale is added, not subtracted.
        private static bool IsInAttackRange(AiContext context, AiWeapon weapon, in ShipState target)
        {
            var distance = context.Delta(context.Ship.Position, target.Position).Length - 0.5f * context.Profile.ModelScale +
                0.5f * context.ProfileOf(target).ModelScale;
            return distance <= weapon.Range;
        }

        // Legacy WeaponWrapperExtensions.CalculateSpread: half of the weapon tolerance plus the target's angular size.
        private static float CalculateSpread(float distance, float enemySize, float weaponSpread)
        {
            const float epsilon = 0.1f;
            const float maxSpread = 0.5f * (float)Math.PI;

            if (distance <= epsilon) return maxSpread + weaponSpread;
            var delta = (float)Math.Atan2(enemySize, distance);
            return (weaponSpread + delta) * 0.5f;
        }
    }
}
