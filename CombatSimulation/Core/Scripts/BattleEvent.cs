namespace CombatSimulation.Core
{
    public enum BattleEventKind : byte
    {
        Spawn = 1,
        Removal = 2,
        RosterTransition = 3,
        Result = 4,
    }

    public enum RemovalReason : byte
    {
        None,
        Death,
        Retreat,
        Expiry,
        Disposal,
    }

    // The same set as the application's roster change reasons (phase 3a).
    public enum RosterTransitionReason : byte
    {
        None,
        Deployed,
        Retreated,
        Killed,

        // Surrender or kill-all destroyed the slot; a ready reserve has no entity.
        ForcedDestruction,
    }

    // One committed event of a tick. A flat tagged form, so the batch never allocates.
    // Identity: battle ID, tick and sequence. An entity event comes before the roster transition of the same operation.
    public readonly struct BattleEvent
    {
        public readonly BattleId BattleId;
        public readonly long Tick;

        // Position in the tick's batch.
        public readonly int Sequence;

        public readonly BattleEventKind Kind;

        // Spawn, removal, and a roster transition that involves an entity.
        public readonly EntityId EntityId;

        // Spawn and removal of a roster ship, and roster transitions.
        public readonly RosterId RosterId;
        public readonly int Side;
        public readonly int Owner;

        // Spawn: the view factory builds the view from it.
        public readonly ShipDefinition Definition;

        public readonly RemovalReason RemovalReason;

        public readonly RosterStatus PreviousStatus;
        public readonly RosterStatus Status;
        public readonly RosterTransitionReason TransitionReason;
        public readonly int Revision;

        // Roster transition: the deficits the slot retains after it (B4).
        public readonly float ArmorDeficit;
        public readonly float ShieldDeficit;

        public readonly BattleResult Result;

        private BattleEvent(BattleId battleId, long tick, int sequence, BattleEventKind kind, EntityId entityId,
            RosterId rosterId, int side, int owner, ShipDefinition definition, RemovalReason removalReason,
            RosterStatus previousStatus, RosterStatus status, RosterTransitionReason transitionReason, int revision,
            float armorDeficit, float shieldDeficit, BattleResult result)
        {
            BattleId = battleId;
            Tick = tick;
            Sequence = sequence;
            Kind = kind;
            EntityId = entityId;
            RosterId = rosterId;
            Side = side;
            Owner = owner;
            Definition = definition;
            RemovalReason = removalReason;
            PreviousStatus = previousStatus;
            Status = status;
            TransitionReason = transitionReason;
            Revision = revision;
            ArmorDeficit = armorDeficit;
            ShieldDeficit = shieldDeficit;
            Result = result;
        }

        internal static BattleEvent Spawn(BattleId battleId, long tick, int sequence, ShipEntity ship) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Spawn, ship.Id, ship.Slot.RosterId, ship.Side,
                ship.Owner, ship.Definition, RemovalReason.None, default, default, RosterTransitionReason.None, 0, 0f, 0f,
                default);

        internal static BattleEvent Removal(BattleId battleId, long tick, int sequence, ShipEntity ship, RemovalReason reason) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Removal, ship.Id, ship.Slot.RosterId, ship.Side,
                ship.Owner, ship.Definition, reason, default, default, RosterTransitionReason.None, 0, 0f, 0f, default);

        internal static BattleEvent Transition(BattleId battleId, long tick, int sequence, RosterSlot slot,
            RosterStatus previousStatus, RosterTransitionReason reason, EntityId entityId) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.RosterTransition, entityId, slot.RosterId, slot.Side,
                slot.Owner, null, RemovalReason.None, previousStatus, slot.Status, reason, slot.Revision,
                slot.ArmorDeficit, slot.ShieldDeficit, default);

        internal static BattleEvent ResultEvent(BattleId battleId, long tick, int sequence, BattleResult result) =>
            new BattleEvent(battleId, tick, sequence, BattleEventKind.Result, default, default, 0, 0,
                null, RemovalReason.None, default, default, RosterTransitionReason.None, 0, 0f, 0f, result);

        public override string ToString()
        {
            switch (Kind)
            {
                case BattleEventKind.Spawn:
                    return $"{Tick}.{Sequence} Spawn {EntityId} (roster {RosterId}, side {Side})";
                case BattleEventKind.Removal:
                    return $"{Tick}.{Sequence} Removal {EntityId} ({RemovalReason})";
                case BattleEventKind.RosterTransition:
                    return $"{Tick}.{Sequence} Roster {RosterId}: {PreviousStatus} -> {Status} ({TransitionReason}, revision {Revision}, entity {EntityId})";
                case BattleEventKind.Result:
                    return $"{Tick}.{Sequence} {Result}";
                default:
                    return $"{Tick}.{Sequence} {Kind}";
            }
        }
    }
}
