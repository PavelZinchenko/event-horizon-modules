using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // One damage effect of a bullet: an amount of one damage type (D4). Falloff: legacy DamageWithFalloff (E6).
    public readonly struct DamageAmount
    {
        public readonly int DamageType;
        public readonly float Amount;
        public readonly bool Falloff;

        public DamageAmount(int damageType, float amount, bool falloff = false)
        {
            DamageType = damageType;
            Amount = amount;
            Falloff = falloff;
        }
    }

    // Legacy BulletImpactType (phase 5, step 3). FirstTarget stops at the first valid hit. AllTargets hits each target
    // unit once when it enters the bullet (legacy OnTriggerEnter). OverTime deals its damage per second to every target
    // it touches, once per target entity per step (D16).
    public enum BulletImpact : byte { FirstTarget, AllTargets, OverTime }

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
            IReadOnlyList<DamageAmount> extraDamage = null, IReadOnlyList<BulletTriggerDefinition> triggers = null,
            bool detonateWhenDestroyed = true, bool canBeDisarmed = true, BulletImpact impact = BulletImpact.FirstTarget,
            bool damageFalloff = false, float radialPush = 0f, float radialPushWithFalloff = 0f, float lifetime = 0f,
            float scale = 0f, bool attachedToParent = false, bool rocket = false, float stickyLifetime = 0f,
            bool magnetic = false, bool hasDirection = true, float beamLength = 0f, bool piercing = false, bool aura = false,
            ImpactEffects effects = default)
            : this(radius, speed, range, damage, weight, parentVelocityEffect, hitPoints, homing, startingVelocityMultiplier,
                damageType, recoil, push, extraDamage, triggers, detonateWhenDestroyed, canBeDisarmed, impact, damageFalloff,
                radialPush, radialPushWithFalloff, lifetime, scale, attachedToParent, rocket, stickyLifetime, magnetic,
                hasDirection, beamLength, piercing, aura, effects, null, 1f)
        {
        }

        // A charged copy (phase 5, step 4f) names its uncharged definition and its charge.
        private BulletDefinition(float radius, float speed, float range, float damage, float weight,
            float parentVelocityEffect, float hitPoints, bool homing, float startingVelocityMultiplier, int damageType,
            float recoil, float push, IReadOnlyList<DamageAmount> extraDamage, IReadOnlyList<BulletTriggerDefinition> triggers,
            bool detonateWhenDestroyed, bool canBeDisarmed, BulletImpact impact, bool damageFalloff, float radialPush,
            float radialPushWithFalloff, float lifetime, float scale, bool attachedToParent, bool rocket,
            float stickyLifetime, bool magnetic, bool hasDirection, float beamLength, bool piercing, bool aura,
            ImpactEffects effects, BulletDefinition uncharged, float charge)
        {
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be finite and positive.");
            if (!(speed >= 0f) || float.IsInfinity(speed))
                throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be finite and not negative.");
            if (!(range >= 0f) || float.IsInfinity(range))
                throw new ArgumentOutOfRangeException(nameof(range), "Range must be finite and not negative.");
            if (!(lifetime >= 0f) || float.IsInfinity(lifetime))
                throw new ArgumentOutOfRangeException(nameof(lifetime), "Lifetime must be finite and not negative.");
            // Legacy RangeMultiplier is 0 at a charge of 0.1 or less: such a bullet expires after its first step.
            if (!(lifetime > 0f) && !(speed > 0f && (range > 0f || uncharged != null)))
                throw new ArgumentException("A bullet needs a lifetime, or a positive speed and range.", nameof(lifetime));
            if (!(scale >= 0f) || float.IsInfinity(scale))
                throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be finite and not negative.");
            if (impact < BulletImpact.FirstTarget || impact > BulletImpact.OverTime)
                throw new ArgumentOutOfRangeException(nameof(impact), "Unknown impact.");
            if (float.IsNaN(radialPush) || float.IsInfinity(radialPush) ||
                float.IsNaN(radialPushWithFalloff) || float.IsInfinity(radialPushWithFalloff))
                throw new ArgumentOutOfRangeException(nameof(radialPush), "Radial push must be finite.");
            if (attachedToParent && homing)
                throw new ArgumentException("An attached bullet cannot home.", nameof(attachedToParent));
            if (magnetic && !homing)
                throw new ArgumentException("A magnetic bullet is a homing bullet.", nameof(magnetic));
            if (rocket && (homing || attachedToParent))
                throw new ArgumentException("A rocket neither homes nor rides on its parent.", nameof(rocket));
            if (!(stickyLifetime >= 0f) || float.IsInfinity(stickyLifetime))
                throw new ArgumentOutOfRangeException(nameof(stickyLifetime), "Sticky lifetime must be finite and not negative.");
            if (stickyLifetime > 0f && (homing || rocket || attachedToParent))
                throw new ArgumentException("A sticky bullet neither steers nor rides on its parent.", nameof(stickyLifetime));
            if (!(beamLength >= 0f) || float.IsInfinity(beamLength))
                throw new ArgumentOutOfRangeException(nameof(beamLength), "Beam length must be finite and not negative.");
            if (beamLength > 0f && (!(speed > 0f) || homing || rocket || stickyLifetime > 0f || attachedToParent || hitPoints > 0f))
                throw new ArgumentException("A beam bolt has a speed and neither steers, rides on its parent nor has hit points.",
                    nameof(beamLength));
            if (piercing && !(beamLength > 0f))
                throw new ArgumentException("Only a beam bolt pierces.", nameof(piercing));
            if (aura && (!(lifetime > 0f) || homing || rocket || stickyLifetime > 0f || beamLength > 0f))
                throw new ArgumentException("An aura has a body lifetime and no other controller.", nameof(aura));
            // Its weapon renews an aura's lifetime, and its size follows that lifetime (Auras).
            if (aura && (effects.Restore > 0f || effects.LifetimeLoss > 0f))
                throw new ArgumentException("An aura does not restore its lifetime.", nameof(effects));
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
            var falloff = damageFalloff || radialPushWithFalloff != 0f;
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
                    falloff |= item.Falloff;
                }
            }

            // A damage-over-time contact travels as one interval with a fixed rate (network plan, decision 2). A push or
            // a falloff would change per tick, and the interval key has no damage type.
            if (impact == BulletImpact.OverTime && (extra.Length > 0 || falloff || push != 0f || radialPush != 0f))
                throw new ArgumentException("Damage over time takes one damage type, without push or falloff.",
                    nameof(impact));

            var triggerList = Array.Empty<BulletTriggerDefinition>();
            var conditions = BulletTriggerConditions.None;
            var spawns = false;
            if (triggers != null && triggers.Count > 0)
            {
                triggerList = new BulletTriggerDefinition[triggers.Count];
                for (var i = 0; i < triggerList.Length; ++i)
                {
                    triggerList[i] = triggers[i] ?? throw new ArgumentNullException(nameof(triggers));
                    conditions |= triggerList[i].Conditions;
                    spawns |= !triggerList[i].IsDetonate;
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
            IsMagnetic = magnetic;
            HasDirection = hasDirection;
            IsRocket = rocket;
            StartingVelocityMultiplier = startingVelocityMultiplier;
            Impact = impact;
            DamageFalloff = damageFalloff;
            RadialPush = radialPush;
            RadialPushWithFalloff = radialPushWithFalloff;
            HasFalloff = falloff;
            Scale = scale > 0f ? scale : 2f * radius;
            AttachedToParent = attachedToParent;
            // The progressive ramp is a beam effect only (phase 5, step 5c).
            if (effects.Ramp > 0f) throw new ArgumentOutOfRangeException(nameof(effects), "A bullet has no progressive ramp.");
            Effects = effects;
            BeamLength = beamLength;
            Piercing = piercing;
            IsAura = aura;
            AuraGrowthTicks = aura ? TickTime.AtLeast(lifetime) : 0;
            BodyLifetime = lifetime;
            Lifetime = lifetime > 0f ? lifetime : range / speed;
            LifetimeTicks = SpawnLifetimeTicks(Lifetime, 1f);
            Triggers = triggerList;
            TriggerConditions = conditions;
            HasSpawnTriggers = spawns;
            DetonateWhenDestroyed = detonateWhenDestroyed;
            CanBeDisarmed = canBeDisarmed;
            StickyLifetime = stickyLifetime;
            StickyTicks = stickyLifetime > 0f ? TickTime.AtLeast(stickyLifetime) : 0;
            Uncharged = uncharged ?? this;
            Charge = charge;

            // Legacy BulletFactory.CreateController: both scale with 1 / (0.2 + 2 x weight); a rocket accelerates at
            // speed / (0.1 + weight), a magnetic bullet at twice the homing thrust.
            var agility = 1f / (0.2f + weight * 2f);
            TurnRate = 120f * agility * (float)(Math.PI / 180.0);
            Acceleration = rocket ? speed / (0.1f + weight) : magnetic ? speed * agility : 0.5f * speed * agility;
        }

        // Collision circle at the bullet origin. Phase 4 takes the shape from the bullet prefab.
        public float Radius { get; }

        // Muzzle speed along the shot, in units per second. A homing bullet accelerates up to it.
        public float Speed { get; }

        public float Range { get; }

        // Damage per hit, of DamageType (D4); per second for OverTime. A missile target takes it without resistance.
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

        // Legacy MagneticController (homing with IgnoreRotation, phase 5, step 4c): a homing bullet that thrusts toward
        // the velocity leading to its target instead of turning toward it. Every homing rule of the network applies.
        public bool IsMagnetic { get; }

        // Legacy BulletShape.HasDirection, read only by a magnetic bullet: with a direction it searches in the 30 degree
        // cone and turns toward its velocity; without one (Spark, Mine) it searches all around and never turns.
        public bool HasDirection { get; }

        // Legacy RocketController (phase 5, step 4a): thrust along the heading up to Speed, which also removes the
        // sideways speed. It never turns.
        public bool IsRocket { get; }

        // Legacy StickyController (phase 5, step 4b): above zero, the bullet sticks to the first ship, drone or tail
        // segment it touches and detonates this many seconds later (StickyMine).
        public float StickyLifetime { get; }
        public bool IsSticky => StickyLifetime > 0f;

        // Legacy cooldown -= dt, detonation at <= 0: the ticks from the stick tick to the detonation tick, both counted.
        public int StickyTicks { get; }

        // The share of Speed the bullet starts with (legacy StartingVelocityMultiplier).
        public float StartingVelocityMultiplier { get; }

        // Homing: the turn rate in radians per second. Homing, magnetic and rocket: the thrust in units per second
        // squared.
        public float TurnRate { get; }
        public float Acceleration { get; }

        // Legacy BulletStats.GetBulletLifetime, in seconds: Body.Lifetime when positive, else range / speed. Homing
        // bullets too (Q2a).
        public float Lifetime { get; }

        // Phase 5, step 3. A bullet that is neither a missile nor a FirstTarget bolt is a legacy area of effect
        // (UnitClass.AreaOfEffect): it hits like an energy bolt, but point defense ignores it and nothing damages it.
        public BulletImpact Impact { get; }
        public bool IsArea => !IsMissile && Impact != BulletImpact.FirstTarget;

        // Falloff (E6, legacy ApplyExplosionDamageAction.GetFalloff): clamp01((R + target scale - distance) / R), with
        // R = Scale / 2 and the distance between the centres. DamageFalloff applies it to Damage; each extra damage
        // has its own flag.
        public bool DamageFalloff { get; }
        public bool HasFalloff { get; }

        public float Falloff(float distance, float targetScale)
        {
            var radius = Scale / 2f;
            var value = (radius + targetScale - distance) / radius;
            return value <= 0f ? 0f : value >= 1f ? 1f : value;
        }

        // Legacy RadialPushAction (PushFromCenter, PullToCenter, PushFromCenterWithFalloff): an impulse away from the
        // bullet centre on a ship or drone target; negative pulls. The second one scales by the falloff.
        public float RadialPush { get; }
        public float RadialPushWithFalloff { get; }

        // Legacy Body.WorldScale of the bullet (BulletStats.BodySize): the falloff diameter of an area, and the target
        // size when an area hits this bullet. 2 x Radius unless set.
        public float Scale { get; }

        // Legacy Body.AttachedToParent at the barrel: the bullet rides on its ship at the barrel pose, without spread or
        // velocity of its own, and leaves with the ship without triggers (owner, 2026-11-02).
        public bool AttachedToParent { get; }

        // Physics steps the bullet flies, from its spawn tick. Legacy expires a bullet when its age exceeds the lifetime
        // (Lifetime.IsExpired). A beam bolt adds its growth: its lifetime does not run while it grows. A restoring bullet
        // starts with its lifetime loss spent (phase 5, step 5d).
        public int LifetimeTicks { get; }

        // The flight ticks of a shot with this lifetime (legacy GetBulletLifetime with the random factor).
        internal int SpawnLifetimeTicks(float lifetime, float speedScale) =>
            TickTime.After(lifetime * Math.Max(0f, 1f - Effects.LifetimeLoss)) + GrowthTicks(speedScale);

        // Only the owner knows when an aura's weapon stopped renewing it (step 4g), or how long a restoring bullet
        // touched ships (step 5d). A copy leaves with the owner's removal record.
        public bool HasOwnerLifetime => IsAura || Effects.Restore > 0f;

        // Legacy MovingBeamController (phase 5, step 4e): above zero, the bullet is a beam bolt, a ray of this length
        // from its origin along its heading. It hits through the ray only; nothing hits its body, and point defense does
        // not see it (the legacy prefab has no physics collider). It starts with its parent's velocity share only and
        // grows by its speed per second; when it reaches this length it takes its speed along its heading, and only
        // then its lifetime starts. In its last ticks the ray shrinks to the remaining lifetime x speed.
        public float BeamLength { get; }
        public bool IsBeamBolt => BeamLength > 0f;

        // Legacy PiercingLaser prefab (RayCastCollider._passThrough): the ray reports every body along it, not the first.
        public bool Piercing { get; }

        // Legacy AuraController (phase 5, step 4g): the circle starts at size 0 and grows to Radius in AuraGrowthTicks
        // (the body lifetime). Its size never exceeds the share of its lifetime that is left, so it shrinks when its
        // weapon stops renewing it (Auras).
        public bool IsAura { get; }
        public int AuraGrowthTicks { get; }

        // Legacy MovingBeamController grows the ray until speed x elapsed time reaches the length: the ticks from the
        // spawn tick to the tick the bolt starts to fly. 0 for other bullets.
        public int GrowthTicks(float speedScale) =>
            IsBeamBolt ? TickTime.AtLeast(BeamLength / (Speed * speedScale)) : 0;

        // Legacy BulletStats.Recoil (Q1, revised by phase 4 E1): the impulse on the ship per shot is Recoil x the bullet
        // velocity relative to the ship. It comes from Body.Recoil only, never from the weight.
        public float Recoil { get; }

        // Legacy PushAction (phase 4 E4): the impulse on a ship or drone target is Push x the bullet velocity, at the
        // target's origin. Negative pulls. Missiles are never pushed (legacy MissileDamageHandler drops impulses).
        public float Push { get; }

        // Drains and siphon on a ship or drone target (phase 5, step 5a), the restore of its own lifetime (5d).
        // OverTime: per second.
        public ImpactEffects Effects { get; }

        // Gameplay triggers in legacy order (phase 5, step 2). The index is the spawn source of a child's identity.
        public IReadOnlyList<BulletTriggerDefinition> Triggers { get; }

        // Every condition of any trigger, and whether any trigger spawns a bullet.
        public BulletTriggerConditions TriggerConditions { get; }
        public bool HasSpawnTriggers { get; }

        // Legacy Body.DetonateWhenDestroyed: a bullet that stops at a hit or is shot down detonates (Detonated triggers
        // after Destroyed ones); otherwise only its Destroyed triggers run.
        public bool DetonateWhenDestroyed { get; }

        // Legacy Body.CanBeDisarmed. Otherwise point defense still zaps the bullet (energy, cooldown, Hit triggers), and
        // the bullet flies on (legacy Bullet.OnCollision ignores the Disarm effect).
        public bool CanBeDisarmed { get; }

        // Phase 5, step 4f (H7): the definition a charging weapon scales, and the charge of this copy (legacy
        // PowerLevel). An uncharged definition is its own, with charge 1.
        public BulletDefinition Uncharged { get; }
        public float Charge { get; }

        // Legacy BulletStats with PowerLevel c: damage and effect powers x c; range x c, or 0 at c <= 0.1; body size,
        // weight, recoil and beam length x (0.5 + 0.5c). A body lifetime stays (G9 reaches beams only); a lifetime of
        // range / speed follows the range. Each child takes the charge of its parent (behaviour change "Charged child
        // bullets"; legacy keeps the first shot's charge). A full charge is the uncharged definition.
        internal BulletDefinition WithCharge(float charge) =>
            charge >= 1f ? Uncharged : Uncharged.Charged(charge, new Dictionary<BulletDefinition, BulletDefinition>());

        private BulletDefinition Charged(float charge, Dictionary<BulletDefinition, BulletDefinition> scaled)
        {
            if (scaled.TryGetValue(this, out var result)) return result;

            var size = 0.5f + 0.5f * charge;
            var rangeScale = charge > 0.1f ? charge : 0f;
            var extra = new DamageAmount[ExtraDamage.Count];
            for (var i = 0; i < extra.Length; ++i)
            {
                var item = ExtraDamage[i];
                extra[i] = new DamageAmount(item.DamageType, item.Amount * charge, item.Falloff);
            }

            var triggers = new BulletTriggerDefinition[Triggers.Count];
            for (var i = 0; i < triggers.Length; ++i)
            {
                var trigger = Triggers[i];
                triggers[i] = trigger.IsDetonate ? trigger
                    : BulletTriggerDefinition.Spawn(trigger.Conditions, trigger.Bullet.Charged(charge, scaled), trigger.Pattern,
                        trigger.RandomFactor);
            }

            result = new BulletDefinition(Radius * size, Speed, Range * rangeScale, Damage * charge, Weight * size,
                ParentVelocityEffect, HitPoints, IsHoming, StartingVelocityMultiplier, DamageType, Recoil * size,
                Push * charge, extra, triggers, DetonateWhenDestroyed, CanBeDisarmed, Impact, DamageFalloff,
                RadialPush * charge, RadialPushWithFalloff * charge, BodyLifetime, Scale * size, AttachedToParent, IsRocket,
                StickyLifetime, IsMagnetic, HasDirection, BeamLength * size, Piercing, IsAura, Effects.WithCharge(charge), this,
                charge);
            scaled.Add(this, result);
            return result;
        }

        // Legacy Body.Lifetime: 0 when the lifetime is range / speed.
        private float BodyLifetime { get; }
    }
}
