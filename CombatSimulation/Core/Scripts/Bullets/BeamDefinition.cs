using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Q3: an attached ray. Costs and damage are absolute, without database modifiers. A continuous beam (legacy
    // ContinuousCannon) lives while its device holds it. Phase 5, step 4d: a shot beam (legacy CommonCannon with a beam
    // controller) lives for its lifetime, and the device does not hold it; both forms take the per-tick spread and the
    // tracking of the legacy beam controllers. Step 4f: a charged beam (legacy ChargeableCannon with a beam controller)
    // is a shot beam that fires its charge.
    public sealed class BeamDefinition : DeviceDefinition
    {
        public BeamDefinition(int barrelIndex, float cooldown, float activationCost, float energyPerSecond,
            float range, float damagePerSecond, int damageType = 0, float spread = 0f, float trackingAngle = 0f,
            ImpactEffects effects = default, bool friendlyFire = false)
            : this(barrelIndex, cooldown, activationCost, energyPerSecond, range, damagePerSecond, damageType, spread,
                trackingAngle, 0f, 0f, effects, null, 1f, friendlyFire, null, BulletImpact.OverTime)
        {
        }

        private BeamDefinition(int barrelIndex, float cooldown, float activationCost, float energyPerSecond,
            float range, float damagePerSecond, int damageType, float spread, float trackingAngle, float lifetime,
            float chargeTime, ImpactEffects effects, BeamDefinition uncharged, float charge, bool friendlyFire,
            IReadOnlyList<BulletTriggerDefinition> triggers, BulletImpact impact)
        {
            if (barrelIndex < 0) throw new ArgumentOutOfRangeException(nameof(barrelIndex));
            ValidateAmount(cooldown, nameof(cooldown));
            ValidateAmount(activationCost, nameof(activationCost));
            ValidateAmount(energyPerSecond, nameof(energyPerSecond));
            ValidateAmount(damagePerSecond, nameof(damagePerSecond));
            ValidateAmount(spread, nameof(spread));
            ValidateAmount(lifetime, nameof(lifetime));
            ValidateAmount(chargeTime, nameof(chargeTime));
            // Legacy RangeMultiplier is 0 at a charge of 0.1 or less: the beam hits nothing.
            if (!(range > 0f || range == 0f && uncharged != null) || float.IsInfinity(range))
                throw new ArgumentOutOfRangeException(nameof(range));
            if (!(trackingAngle >= 0f) || trackingAngle > (float)Math.PI)
                throw new ArgumentOutOfRangeException(nameof(trackingAngle));
            DamageTypeTable.ValidateIndex(damageType, nameof(damageType));
            if (effects.Restore > 0f || effects.LifetimeLoss > 0f)
                throw new ArgumentOutOfRangeException(nameof(effects), "A beam does not restore its lifetime.");

            if (impact != BulletImpact.OverTime && (impact != BulletImpact.AllTargets || lifetime <= 0f))
                throw new ArgumentOutOfRangeException(nameof(impact));
            var copied = new List<BulletTriggerDefinition>();
            if (triggers != null)
                foreach (var trigger in triggers)
                {
                    if (trigger == null || trigger.IsDetonate || trigger.Conditions != BulletTriggerConditions.Hit)
                        throw new ArgumentException("Beams support Hit spawn triggers only.", nameof(triggers));
                    copied.Add(trigger);
                }
            Triggers = copied.AsReadOnly();
            Impact = impact;
            BarrelIndex = barrelIndex;
            Cooldown = cooldown;
            CooldownTicks = TickTime.AtLeast(cooldown);
            ActivationCost = activationCost;
            EnergyPerSecond = energyPerSecond;
            Range = range;
            DamagePerSecond = damagePerSecond;
            DamageType = damageType;
            Spread = spread;
            TrackingAngle = trackingAngle;
            Effects = effects;
            FriendlyFire = friendlyFire;
            Lifetime = lifetime;
            LifetimeTicks = lifetime > 0f ? TickTime.After(lifetime) : 0;
            ChargeTime = chargeTime;
            Uncharged = uncharged ?? this;
            Charge = charge;
        }

        // Legacy CommonCannon with a beam controller: one beam per shot for the energy cost; it lives Lifetime seconds.
        public static BeamDefinition Shot(int barrelIndex, float cooldown, float energyCost, float range,
            float damagePerSecond, float lifetime, int damageType = 0, float spread = 0f, float trackingAngle = 0f,
            ImpactEffects effects = default, bool friendlyFire = false, IReadOnlyList<BulletTriggerDefinition> triggers = null,
            BulletImpact impact = BulletImpact.OverTime)
        {
            if (!(lifetime > 0f)) throw new ArgumentOutOfRangeException(nameof(lifetime));
            return new BeamDefinition(barrelIndex, cooldown, energyCost, 0f, range, damagePerSecond, damageType, spread,
                trackingAngle, lifetime, 0f, effects, null, 1f, friendlyFire, triggers, impact);
        }

        // Legacy ChargeableCannon with a beam controller (phase 5, step 4f): a full charge takes chargeTime seconds
        // (1 / FireRate) and energyCost energy. The beam lives Lifetime seconds at a full charge.
        public static BeamDefinition Charged(int barrelIndex, float chargeTime, float energyCost, float range,
            float damagePerSecond, float lifetime, int damageType = 0, float spread = 0f, float trackingAngle = 0f,
            ImpactEffects effects = default, bool friendlyFire = false)
        {
            if (!(chargeTime > 0f) || float.IsInfinity(chargeTime)) throw new ArgumentOutOfRangeException(nameof(chargeTime));
            if (!(lifetime > 0f)) throw new ArgumentOutOfRangeException(nameof(lifetime));
            return new BeamDefinition(barrelIndex, 0f, energyCost, 0f, range, damagePerSecond, damageType, spread,
                trackingAngle, lifetime, chargeTime, effects, null, 1f, friendlyFire, null, BulletImpact.OverTime);
        }

        public int BarrelIndex { get; }
        public float Cooldown { get; }
        public int CooldownTicks { get; }

        // A continuous beam pays it at the start; a shot beam per shot.
        public float ActivationCost { get; }

        // A continuous beam only.
        public float EnergyPerSecond { get; }
        public float Range { get; }
        // Per second for OverTime; per new target for AllTargets.
        public float DamagePerSecond { get; }

        // An index into the battle's damage type table (D4).
        public int DamageType { get; }

        // Legacy weapon Spread in radians: the full angle. Each tick the ray turns from the barrel by a random angle within
        // half of it either way (legacy BeamController), or within all of it either way while a tracking beam touches
        // nothing (legacy LookAtTargetController).
        public float Spread { get; }

        // Legacy BulletController_Beam.TrackingAngle in radians; 0 does not track. A tracking beam points at the target
        // it touched in the previous tick, at most this angle from the barrel.
        public float TrackingAngle { get; }

        // Drains and siphon per second on a ship or drone target (phase 5, step 5a); the siphon heals from
        // DamagePerSecond.
        public ImpactEffects Effects { get; }
        public bool FriendlyFire { get; }
        public IReadOnlyList<BulletTriggerDefinition> Triggers { get; }
        public BulletImpact Impact { get; }

        // A shot beam's lifetime in seconds; 0 for a continuous beam.
        public float Lifetime { get; }
        public int LifetimeTicks { get; }
        public bool IsShot => LifetimeTicks > 0;

        // A charged beam: the seconds of a full charge; 0 otherwise. A charged beam is a shot beam without a weapon
        // cooldown; ActivationCost is the energy of a full charge.
        public float ChargeTime { get; }
        public bool IsCharged => ChargeTime > 0f;

        // The definition a charge scales, and the charge of this copy (legacy PowerLevel); 1 when uncharged.
        public BeamDefinition Uncharged { get; }
        public float Charge { get; }

        // Legacy BulletStats with PowerLevel c: damage and effect powers x c, range x c (0 at c <= 0.1), and G9 (owner, 2026-11-03): the
        // lifetime x (0.5 + 0.5c), as legacy scales the flash of a continuous controller. A full charge is the
        // uncharged definition.
        internal BeamDefinition WithCharge(float charge)
        {
            var beam = Uncharged;
            if (charge >= 1f) return beam;
            return new BeamDefinition(beam.BarrelIndex, beam.Cooldown, beam.ActivationCost, beam.EnergyPerSecond,
                beam.Range * (charge > 0.1f ? charge : 0f), beam.DamagePerSecond * charge, beam.DamageType, beam.Spread,
                beam.TrackingAngle, beam.Lifetime * (0.5f + 0.5f * charge), beam.ChargeTime, beam.Effects.WithCharge(charge),
                beam, charge, beam.FriendlyFire, beam.Triggers, beam.Impact);
        }

        internal override void Validate(int barrelCount)
        {
            if (BarrelIndex >= barrelCount)
                throw new ArgumentOutOfRangeException(nameof(BarrelIndex), $"Beam barrel {BarrelIndex} does not exist.");
        }

        internal override ShipDevice CreateDevice(int index) => new BeamDevice(this, index);

        private static void ValidateAmount(float value, string name)
        {
            if (!(value >= 0f) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(name);
        }
    }
}
