using System;

namespace CombatSimulation.Core
{
    // Immutable parameters of a projectile (new ammunition model, projectile or homing controller, one damage effect).
    // Phase 2 builds them from code fixtures; phase 4 from the database.
    public sealed class BulletDefinition
    {
        // Legacy HomingController: a target search every 2 s, within 1.3 x range; full turn rate unless the target is
        // within 5 degrees.
        public const float SearchInterval = 2f;
        public const float SearchRangeFactor = 1.3f;
        public const float TurnDeadZone = (float)(5.0 * Math.PI / 180.0);

        // GetEnemyForMissile: an enemy within this angle of the heading is preferred.
        public const float SearchCone = (float)(30.0 * Math.PI / 180.0);

        public BulletDefinition(float radius, float speed, float range, float damage, float weight = 0f,
            float parentVelocityEffect = 1f, float hitPoints = 0f, bool homing = false,
            float startingVelocityMultiplier = 1f)
        {
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be finite and positive.");
            if (!(speed > 0f) || float.IsInfinity(speed))
                throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be finite and positive.");
            if (!(range > 0f) || float.IsInfinity(range))
                throw new ArgumentOutOfRangeException(nameof(range), "Range must be finite and positive.");
            if (!(damage >= 0f) || float.IsInfinity(damage))
                throw new ArgumentOutOfRangeException(nameof(damage), "Damage must be finite and not negative.");
            if (!(weight >= 0f) || float.IsInfinity(weight))
                throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be finite and not negative.");
            if (float.IsNaN(parentVelocityEffect) || float.IsInfinity(parentVelocityEffect))
                throw new ArgumentOutOfRangeException(nameof(parentVelocityEffect), "Parent velocity effect must be finite.");
            if (!(hitPoints >= 0f) || float.IsInfinity(hitPoints))
                throw new ArgumentOutOfRangeException(nameof(hitPoints), "Hit points must be finite and not negative.");
            if (!(startingVelocityMultiplier >= 0f) || float.IsInfinity(startingVelocityMultiplier))
                throw new ArgumentOutOfRangeException(nameof(startingVelocityMultiplier),
                    "Starting velocity multiplier must be finite and not negative.");

            Radius = radius;
            Speed = speed;
            Range = range;
            Damage = damage;
            Weight = weight;
            ParentVelocityEffect = parentVelocityEffect;
            HitPoints = hitPoints;
            IsHoming = homing;
            StartingVelocityMultiplier = startingVelocityMultiplier;
            LifetimeTicks = TickTime.After(Lifetime);

            // Legacy BulletFactory.CreateController: both scale with 1 / (0.2 + 2 x weight).
            var agility = 1f / (0.2f + weight * 2f);
            TurnRate = 120f * agility * (float)(Math.PI / 180.0);
            Acceleration = 0.5f * speed * agility;
        }

        // Collision circle at the bullet origin. Phase 4 takes the shape from the bullet prefab.
        public float Radius { get; }

        // Muzzle speed along the shot, in units per second. A homing bullet accelerates up to it.
        public float Speed { get; }

        public float Range { get; }

        // Impact damage to the first target (HitFirstTarget). Phase 2 has no damage types or resistances.
        public float Damage { get; }

        public float Weight { get; }

        // The share of the ship velocity the bullet inherits (legacy BulletBody.ParentVelocityEffect).
        public float ParentVelocityEffect { get; }

        // Above zero: a missile (legacy UnitClass.Missile). Energy bolts and missiles damage it; it detonates at its
        // first valid hit on a ship or a missile and flies through energy bolts.
        public float HitPoints { get; }
        public bool IsMissile => HitPoints > 0f;

        // Legacy HomingController: searches a target and turns toward it.
        public bool IsHoming { get; }

        // The share of Speed the bullet starts with (legacy StartingVelocityMultiplier).
        public float StartingVelocityMultiplier { get; }

        // Homing: the turn rate in radians per second and the thrust in units per second squared.
        public float TurnRate { get; }
        public float Acceleration { get; }

        // Legacy BulletStats.GetBulletLifetime: range / speed, in seconds. Homing bullets too (Q2a).
        public float Lifetime => Range / Speed;

        // Physics steps the bullet flies. Legacy expires a bullet when its age exceeds the lifetime (Lifetime.IsExpired).
        public int LifetimeTicks { get; }

        // Legacy BulletStats.Recoil: the impulse on the ship per shot (Q1). Applied by the core (owner, 2026-10-20).
        public float Recoil => Weight * Speed * StartingVelocityMultiplier * ParentVelocityEffect;
    }
}
