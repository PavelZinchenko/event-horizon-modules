using System;

namespace CombatSimulation.Core
{
    // Q3: an attached ray. Costs and damage are absolute, without database modifiers. A continuous beam (legacy
    // ContinuousCannon) lives while its device holds it. Phase 5, step 4d: a shot beam (legacy CommonCannon with a beam
    // controller) lives for its lifetime, and the device does not hold it; both forms take the per-tick spread and the
    // tracking of the legacy beam controllers.
    public sealed class BeamDefinition : DeviceDefinition
    {
        public BeamDefinition(int barrelIndex, float cooldown, float activationCost, float energyPerSecond,
            float range, float damagePerSecond, int damageType = 0, float spread = 0f, float trackingAngle = 0f)
            : this(barrelIndex, cooldown, activationCost, energyPerSecond, range, damagePerSecond, damageType, spread,
                trackingAngle, 0f)
        {
        }

        private BeamDefinition(int barrelIndex, float cooldown, float activationCost, float energyPerSecond,
            float range, float damagePerSecond, int damageType, float spread, float trackingAngle, float lifetime)
        {
            if (barrelIndex < 0) throw new ArgumentOutOfRangeException(nameof(barrelIndex));
            ValidateAmount(cooldown, nameof(cooldown));
            ValidateAmount(activationCost, nameof(activationCost));
            ValidateAmount(energyPerSecond, nameof(energyPerSecond));
            ValidateAmount(damagePerSecond, nameof(damagePerSecond));
            ValidateAmount(spread, nameof(spread));
            ValidateAmount(lifetime, nameof(lifetime));
            if (!(range > 0f) || float.IsInfinity(range)) throw new ArgumentOutOfRangeException(nameof(range));
            if (!(trackingAngle >= 0f) || trackingAngle > (float)Math.PI)
                throw new ArgumentOutOfRangeException(nameof(trackingAngle));
            DamageTypeTable.ValidateIndex(damageType, nameof(damageType));

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
            Lifetime = lifetime;
            LifetimeTicks = lifetime > 0f ? TickTime.After(lifetime) : 0;
        }

        // Legacy CommonCannon with a beam controller: one beam per shot for the energy cost; it lives Lifetime seconds.
        public static BeamDefinition Shot(int barrelIndex, float cooldown, float energyCost, float range,
            float damagePerSecond, float lifetime, int damageType = 0, float spread = 0f, float trackingAngle = 0f)
        {
            if (!(lifetime > 0f)) throw new ArgumentOutOfRangeException(nameof(lifetime));
            return new BeamDefinition(barrelIndex, cooldown, energyCost, 0f, range, damagePerSecond, damageType, spread,
                trackingAngle, lifetime);
        }

        public int BarrelIndex { get; }
        public float Cooldown { get; }
        public int CooldownTicks { get; }

        // A continuous beam pays it at the start; a shot beam per shot.
        public float ActivationCost { get; }

        // A continuous beam only.
        public float EnergyPerSecond { get; }
        public float Range { get; }
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

        // A shot beam's lifetime in seconds; 0 for a continuous beam.
        public float Lifetime { get; }
        public int LifetimeTicks { get; }
        public bool IsShot => LifetimeTicks > 0;

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
