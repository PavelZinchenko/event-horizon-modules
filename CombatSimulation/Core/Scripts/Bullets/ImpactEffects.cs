using System;

namespace CombatSimulation.Core
{
    // Effects of a bullet or beam on a ship or drone target besides damage and push (phase 5, step 5). A hit applies
    // each once, at full power (legacy actions have no falloff); damage over time and a beam apply each per second,
    // except Roll, which runs on contact entry only.
    // Missiles and tail segments take only the progressive ramp and repair's hostile branch, which are damage.
    // The restore (step 5d) acts on the bullet itself.
    public readonly struct ImpactEffects
    {
        public ImpactEffects(float energyDrain = 0f, float shieldDrain = 0f, float siphon = 0f,
            float slowDown = 0f, float roll = 0f, float ramp = 0f, float rampTime = 0f, float restore = 0f,
            float lifetimeLoss = 0f, float repair = 0f, float repairDamage = 0f, int repairDamageType = 0,
            float energyRecharge = 0f, float shieldRecharge = 0f, float enemyEnergyDrain = 0f, float enemyShieldDrain = 0f)
        {
            Validate(energyDrain, nameof(energyDrain));
            Validate(shieldDrain, nameof(shieldDrain));
            Validate(siphon, nameof(siphon));
            Validate(slowDown, nameof(slowDown));
            Validate(roll, nameof(roll));
            Validate(ramp, nameof(ramp));
            Validate(rampTime, nameof(rampTime));
            Validate(restore, nameof(restore));
            Validate(lifetimeLoss, nameof(lifetimeLoss));
            Validate(repair, nameof(repair));
            Validate(repairDamage, nameof(repairDamage));
            Validate(energyRecharge, nameof(energyRecharge));
            Validate(shieldRecharge, nameof(shieldRecharge));
            Validate(enemyEnergyDrain, nameof(enemyEnergyDrain));
            Validate(enemyShieldDrain, nameof(enemyShieldDrain));
            DamageTypeTable.ValidateIndex(repairDamageType, nameof(repairDamageType));
            EnergyDrain = energyDrain;
            ShieldDrain = shieldDrain;
            Siphon = siphon;
            SlowDown = slowDown;
            Roll = roll;
            Ramp = ramp;
            RampTime = rampTime;
            Restore = restore;
            LifetimeLoss = lifetimeLoss;
            Repair = repair;
            RepairDamage = repairDamage;
            RepairDamageType = repairDamageType;
            EnergyRecharge = energyRecharge;
            ShieldRecharge = shieldRecharge;
            EnemyEnergyDrain = enemyEnergyDrain;
            EnemyShieldDrain = enemyShieldDrain;
        }

        // Legacy DrainEnergyAction: the target's energy falls by it, clamped at 0, and its recharge delay restarts.
        public float EnergyDrain { get; }

        // Legacy DamageShieldAction: the target's shield falls by it before the shield absorbs damage.
        public float ShieldDrain { get; }

        // Legacy SiphonHitPointsAction Factor, on the first damage effect: the attacker's ship regains that damage x
        // (1 - the target's armor resistance of its type) x Siphon, also while the target's shield absorbs it
        // (finding 7a). Ship and drone targets only.
        public float Siphon { get; }

        // Legacy SlowDownAction: scale velocity and spin by 1 - clamp01(power / sqrt(weight)).
        public float SlowDown { get; }

        // Legacy RollAction: add power / sqrt(weight) x 180 degrees/s in the target's spin direction.
        public float Roll { get; }

        // Legacy ProgressiveDamageAction on a beam (phase 5, step 5c): damage per second of the beam's type, x
        // min(1, t / RampTime), t = the time since the contact with the first body on the ray started. The first contact
        // tick deals none; any target takes it.
        public float Ramp { get; }
        public float RampTime { get; }

        // Legacy RestoreLifetime on a bullet (phase 5, step 5d): it starts with LifetimeLoss x its lifetime spent
        // (Lifetime.Take(Max x Factor)), and each contact with a ship or drone gives back Restore seconds of it (over
        // time: per second), up to its full lifetime (Lifetime.Restore).
        public float Restore { get; }
        public float LifetimeLoss { get; }

        public bool HasDrain => EnergyDrain > 0f || ShieldDrain > 0f;

        // Repair and recharge choose their branch from the firing ship's allegiance, not the collision filter.
        public float Repair { get; }
        public float RepairDamage { get; }
        public int RepairDamageType { get; }
        public float EnergyRecharge { get; }
        public float ShieldRecharge { get; }
        public float EnemyEnergyDrain { get; }
        public float EnemyShieldDrain { get; }

        internal float DamageAgainst(Allegiance source, Allegiance target) =>
            Allegiances.IsHostile(source, target) ? RepairDamage : 0f;

        // Legacy BulletStats with PowerLevel c: effect powers x c. The siphon factor, the ramp time and the restore stay
        // (legacy BulletFactory reads the raw RestoreLifetime power).
        internal ImpactEffects WithCharge(float charge) =>
            new ImpactEffects(EnergyDrain * charge, ShieldDrain * charge, Siphon, SlowDown * charge, Roll * charge,
                Ramp * charge, RampTime, Restore, LifetimeLoss, Repair * charge, RepairDamage * charge, RepairDamageType,
                EnergyRecharge * charge, ShieldRecharge * charge, EnemyEnergyDrain * charge, EnemyShieldDrain * charge);

        // The ramp damage of the contact ticks [from, to), counted from the contact's first tick (0). Tick k deals
        // ramp x min(1, k x TickDuration / rampTime) x TickDuration; tick 0 deals none (legacy IsNew). The owner adds
        // one tick at a time; the target owner and the correction replay add ranges, so the sum has a closed form.
        internal static double RampDamage(float ramp, float rampTime, long from, long to)
        {
            from = Math.Max(from, 1L);
            if (to <= from || !(ramp > 0f)) return 0.0;
            var full = rampTime / (double)Battle.TickDuration;
            // Ticks below the first full one grow linearly: k / full.
            var firstFull = Math.Max(from, Math.Min(to, (long)Math.Ceiling(full)));
            var growing = 0.0;
            if (firstFull > from) growing = ((double)from + firstFull - 1) * (firstFull - from) / 2.0 / full;
            return ramp * Battle.TickDuration * (growing + (to - firstFull));
        }

        internal static float RampTick(float ramp, float rampTime, long tick) =>
            tick < 1 || !(ramp > 0f) ? 0f :
            rampTime > 0f ? ramp * Math.Min(1f, tick * Battle.TickDuration / rampTime) * Battle.TickDuration :
            ramp * Battle.TickDuration;

        private static void Validate(float value, string name)
        {
            if (!(value >= 0f) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "An effect must be finite and not negative.");
        }
    }
}
