using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public enum HitContactState : byte { Start, Stay, End }
    // Disarm and Stop name a bullet target only (TargetIsBullet).
    public enum HitImpactKind : byte { Impact, Energy, DamageOverTime, Disarm, Stop }

    // An owner-decided contact against a remote ship or drone. No live object is needed to apply it.
    // Start is inclusive; End is exclusive. Amount is damage, or damage per second for damage over time.
    // Sequence numbers the records of one attacker owner from 1; the target owner acknowledges it in corrections.
    // TargetIsBullet (network plan, 9d; owner, 2026-10-26): a contact on a remote bullet that the sender's device saw.
    // The attacker is the sender's bullet or point-defense ship. Impact and Energy carry the hit's damage, DamageOverTime
    // one tick of ray damage, Disarm a point-defense zap, and Stop a target bullet that stopped at its own hit on the
    // attacker. The contact state is always Start and the shape 0.
    // DamageType (D4) indexes the battle's damage type table; a bullet target ignores it.
    // Impulse (phase 4 E4, owner 2026-10-31): the push on a ship target, applied at its origin by the target owner when
    // the record arrives. Impact and Energy records on a ship only; zero otherwise. A bullet with several damage effects
    // sends one record per effect, and only the first carries the impulse.
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

        public ResolvedHit(BattleId battleId, long tick, EntityId attackerId, int attackerOwner, int deviceIndex,
            EntityId targetId, int targetOwner, int targetShape, HitContactState contactState,
            HitImpactKind impactKind, float amount, ulong sequence = 0, bool targetIsBullet = false, byte damageType = 0,
            Vec2 impulse = default)
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
        }

        internal ResolvedHit EndAt(long tick) => new ResolvedHit(BattleId, tick, AttackerId, AttackerOwner,
            DeviceIndex, TargetId, TargetOwner, TargetShape, HitContactState.End, ImpactKind, Amount, 0, TargetIsBullet, DamageType, Impulse);

        internal ResolvedHit WithSequence(ulong sequence) => new ResolvedHit(BattleId, Tick, AttackerId, AttackerOwner,
            DeviceIndex, TargetId, TargetOwner, TargetShape, ContactState, ImpactKind, Amount, sequence, TargetIsBullet, DamageType, Impulse);

        internal bool SameContact(in ResolvedHit other) => AttackerId == other.AttackerId &&
            DeviceIndex == other.DeviceIndex && TargetId == other.TargetId && TargetShape == other.TargetShape;
    }
}
