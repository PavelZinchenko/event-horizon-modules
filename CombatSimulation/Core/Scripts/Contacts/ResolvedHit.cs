using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public enum HitContactState : byte { Start, Stay, End }
    // Disarm and Stop name a bullet target only (TargetIsBullet).
    public enum HitImpactKind : byte { Impact, Energy, DamageOverTime, Disarm, Stop }
    // What the amount of a record on a ship does (phase 5, step 5a). A bullet target takes Damage only.
    public enum HitEffect : byte
    {
        Damage, EnergyDrain, ShieldDrain, SlowDown, Roll, ProgressiveDamage, Repair, EnergyRecharge, ShieldRecharge,
        DamageIgnoringShield, ProgressiveDamageIgnoringShield
    }

    // An owner-decided contact against a remote ship or drone. No live object is needed to apply it.
    // Start is inclusive; End is exclusive. Amount is damage, or damage per second for damage over time.
    // Sequence numbers the records of one attacker owner from 1; the target owner acknowledges it in corrections.
    // TargetIsBullet (network plan, 9d; owner, 2026-10-26): a contact on a remote bullet that the sender's device saw.
    // The attacker is the sender's bullet or point-defense ship. Impact and Energy carry the hit's damage, DamageOverTime
    // one tick of ray damage, Disarm a point-defense zap, and Stop a target bullet that stopped at its own hit on the
    // attacker. The contact state is always Start and the shape 0. A worm tail segment (phase 5, step 7d) is a contact
    // target too: Impact, Energy and DamageOverTime carry the hit's total damage of all types; device index -1 marks a
    // ram, which counts once per tick as DamageOverTime does.
    // DamageType (D4) indexes the battle's damage type table; a bullet target ignores it.
    // Impulse (phase 4 E4, owner 2026-10-31): the push on a ship target, applied at its origin by the target owner when
    // the record arrives. Impact and Energy records on a ship only; zero otherwise. A bullet with several damage effects
    // sends one record per effect, and only the first carries the impulse.
    // Effect (phase 5, step 5a, SessionCodec version 11): a drain on a ship target travels as its own record with the
    // drain as amount, beside the damage records of the same contact; over time, as its own interval with the rate.
    // DamageType is 0 for a drain.
    // SlowDown carries power (per second for an interval), before the damage record carrying push. Roll carries a
    // positive change in degrees/s, once on contact entry; the receiver chooses the sign from its current spin.
    // ProgressiveDamage (step 5c): a beam's ramp as its own interval on shape 0, with the full ramp per second as
    // amount, the beam's damage type and RampTime; the receiver counts the ramp from the interval's first tick.
    // DamageIgnoringShield (step 6a, version 12) keeps the damage type and projectile push, but skips shield absorption.
    // ProgressiveDamageIgnoringShield does the same for a ramp. Bullet and segment targets still use Damage.
    public readonly struct ResolvedHit
    {
        public readonly BattleId BattleId;
        public readonly long Tick;
        public readonly EntityId AttackerId;
        public readonly int AttackerOwner;
        public readonly int DeviceIndex;
        public readonly EntityId TargetId;
        public readonly int TargetOwner;
        // Stable shape order within the target definition, never a physics storage index.
        public readonly int TargetShape;
        public readonly HitContactState ContactState;
        public readonly HitImpactKind ImpactKind;
        public readonly float Amount;
        public readonly ulong Sequence;
        public readonly bool TargetIsBullet;
        public readonly byte DamageType;
        public readonly Vec2 Impulse;
        public readonly HitEffect Effect;
        // Seconds to the full ramp; ProgressiveDamage only, 0 otherwise.
        public readonly float RampTime;

        public ResolvedHit(BattleId battleId, long tick, EntityId attackerId, int attackerOwner, int deviceIndex,
            EntityId targetId, int targetOwner, int targetShape, HitContactState contactState,
            HitImpactKind impactKind, float amount, ulong sequence = 0, bool targetIsBullet = false, byte damageType = 0,
            Vec2 impulse = default, HitEffect effect = HitEffect.Damage, float rampTime = 0f)
        {
            BattleId = battleId;
            Tick = tick;
            AttackerId = attackerId;
            AttackerOwner = attackerOwner;
            DeviceIndex = deviceIndex;
            TargetId = targetId;
            TargetOwner = targetOwner;
            TargetShape = targetShape;
            ContactState = contactState;
            ImpactKind = impactKind;
            Amount = amount;
            Sequence = sequence;
            TargetIsBullet = targetIsBullet;
            DamageType = damageType;
            Impulse = impulse;
            Effect = effect;
            RampTime = rampTime;
        }

        internal ResolvedHit EndAt(long tick) => new ResolvedHit(BattleId, tick, AttackerId, AttackerOwner,
            DeviceIndex, TargetId, TargetOwner, TargetShape, HitContactState.End, ImpactKind, Amount, 0, TargetIsBullet,
            DamageType, Impulse, Effect, RampTime);

        internal ResolvedHit WithSequence(ulong sequence) => new ResolvedHit(BattleId, Tick, AttackerId, AttackerOwner,
            DeviceIndex, TargetId, TargetOwner, TargetShape, ContactState, ImpactKind, Amount, sequence, TargetIsBullet,
            DamageType, Impulse, Effect, RampTime);

        // One interval per effect of a contact.
        internal bool SameContact(in ResolvedHit other) => AttackerId == other.AttackerId &&
            DeviceIndex == other.DeviceIndex && TargetId == other.TargetId && TargetShape == other.TargetShape &&
            Effect == other.Effect && DamageType == other.DamageType;
    }
}
