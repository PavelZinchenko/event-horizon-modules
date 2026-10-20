using System;

namespace CombatSimulation.Core
{
    // Immutable parameters of a projectile (new ammunition model, projectile controller, one damage effect).
    // Phase 2 builds them from code fixtures; phase 4 from the database.
    public sealed class BulletDefinition
    {
        public BulletDefinition(float radius, float speed, float range, float damage, float weight = 0f,
            float parentVelocityEffect = 1f)
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

            Radius = radius;
            Speed = speed;
            Range = range;
            Damage = damage;
            Weight = weight;
            ParentVelocityEffect = parentVelocityEffect;
            LifetimeTicks = TickTime.After(Lifetime);
        }

        // Collision circle at the bullet origin. Phase 4 takes the shape from the bullet prefab.
        public float Radius { get; }

        // Muzzle speed along the shot, in units per second.
        public float Speed { get; }

        public float Range { get; }

        // Impact damage to the first target (HitFirstTarget). Phase 2 has no damage types or resistances.
        public float Damage { get; }

        public float Weight { get; }

        // The share of the ship velocity the bullet inherits (legacy BulletBody.ParentVelocityEffect).
        public float ParentVelocityEffect { get; }

        // Legacy BulletStats.GetBulletLifetime: range / speed, in seconds.
        public float Lifetime => Range / Speed;

        // Physics steps the bullet flies. Legacy expires a bullet when its age exceeds the lifetime (Lifetime.IsExpired).
        public int LifetimeTicks { get; }

        // Legacy BulletStats.Recoil: the impulse on the ship per shot (Q1). Applied by the core (owner, 2026-10-20).
        public float Recoil => Weight * Speed * ParentVelocityEffect;
    }
}
