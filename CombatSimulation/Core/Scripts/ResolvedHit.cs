namespace CombatSimulation.Core
{
    public enum HitContactState : byte { Start, Stay, End }
    public enum HitImpactKind : byte { Impact, Energy, DamageOverTime }

    // An owner-decided contact against a remote ship or drone. No live object is needed to apply it.
    // Start is inclusive; End is exclusive. Amount is damage, or damage per second for damage over time.
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

        public ResolvedHit(BattleId battleId, long tick, EntityId attackerId, int attackerOwner, int deviceIndex,
            EntityId targetId, int targetOwner, int targetShape, HitContactState contactState,
            HitImpactKind impactKind, float amount)
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
        }

        internal ResolvedHit EndAt(long tick) => new ResolvedHit(BattleId, tick, AttackerId, AttackerOwner,
            DeviceIndex, TargetId, TargetOwner, TargetShape, HitContactState.End, ImpactKind, Amount);

        internal bool SameContact(in ResolvedHit other) => AttackerId == other.AttackerId &&
            DeviceIndex == other.DeviceIndex && TargetId == other.TargetId && TargetShape == other.TargetShape;
    }
}
