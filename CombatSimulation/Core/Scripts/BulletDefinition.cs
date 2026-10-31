using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // One damage effect of a bullet: an amount of one damage type (D4).
    public readonly struct DamageAmount
    {
        public readonly int DamageType;
        public readonly float Amount;

        public DamageAmount(int damageType, float amount)
        {
            DamageType = damageType;
            Amount = amount;
        }
    }

    // Immutable parameters of a projectile (new ammunition model, projectile or homing controller, damage and push
    // effects). Phase 2 builds them from code fixtures; phase 4 from the database.
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
            float startingVelocityMultiplier = 1f, int damageType = 0, float recoil = 0f, float push = 0f,
            IReadOnlyList<DamageAmount> extraDamage = null)
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
            DamageTypeTable.ValidateIndex(damageType, nameof(damageType));
            if (!(recoil >= 0f) || float.IsInfinity(recoil))
                throw new ArgumentOutOfRangeException(nameof(recoil), "Recoil must be finite and not negative.");
            if (float.IsNaN(push) || float.IsInfinity(push))
                throw new ArgumentOutOfRangeException(nameof(push), "Push must be finite.");

            var total = damage;
            var extra = Array.Empty<DamageAmount>();
            if (extraDamage != null && extraDamage.Count > 0)
            {
                extra = new DamageAmount[extraDamage.Count];
                for (var i = 0; i < extra.Length; ++i)
                {
                    var item = extraDamage[i];
                    if (!(item.Amount >= 0f) || float.IsInfinity(item.Amount))
                        throw new ArgumentOutOfRangeException(nameof(extraDamage), "Damage must be finite and not negative.");
                    DamageTypeTable.ValidateIndex(item.DamageType, nameof(extraDamage));
                    extra[i] = item;
                    total += item.Amount;
                }
            }

            Radius = radius;
            Speed = speed;
            Range = range;
            Damage = damage;
            DamageType = damageType;
            ExtraDamage = extra;
            TotalDamage = total;
            Weight = weight;
            Recoil = recoil;
            Push = push;
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

        // Damage to the first target (HitFirstTarget), of DamageType (D4). A missile target takes it without resistance.
        public float Damage { get; }

        // An index into the battle's damage type table.
        public int DamageType { get; }

        // Further damage effects of other types (legacy: several Damage effects, phase 4 E4). A ship target takes each
        // of them with its own resistance.
        public IReadOnlyList<DamageAmount> ExtraDamage { get; }

        // Damage plus every extra damage. A missile target takes it without resistance (legacy GetTotalDamage).
        public float TotalDamage { get; }

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

        // Legacy BulletStats.Recoil (Q1, revised by phase 4 E1): the impulse on the ship per shot is Recoil x the bullet
        // velocity relative to the ship. It comes from Body.Recoil only, never from the weight.
        public float Recoil { get; }

        // Legacy PushAction (phase 4 E4): the impulse on a ship or drone target is Push x the bullet velocity, at the
        // target's origin. Negative pulls. Missiles are never pushed (legacy MissileDamageHandler drops impulses).
        public float Push { get; }
    }
}
