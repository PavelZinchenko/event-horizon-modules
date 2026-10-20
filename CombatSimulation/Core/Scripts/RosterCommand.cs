namespace CombatSimulation.Core
{
    public enum RosterCommandKind : byte
    {
        Deploy = 1,
        Retreat = 2,
        Surrender = 3,
        KillAll = 4,
    }

    // A roster request for a target tick. Submitting it does not mean that it succeeds:
    // the core validates it again against committed state at stage 1 of its tick.
    public readonly struct RosterCommand
    {
        public readonly BattleId BattleId;

        // Unique among pending commands. Commands of one tick apply in command ID order.
        public readonly ulong CommandId;

        public readonly long TargetTick;
        public readonly RosterCommandKind Kind;

        // Deploy only.
        public readonly RosterId RosterId;

        // Retreat only.
        public readonly EntityId EntityId;

        // Surrender: the side that gives up. Kill-all: the requesting side; the other side is destroyed.
        public readonly int Side;

        private RosterCommand(BattleId battleId, ulong commandId, long targetTick, RosterCommandKind kind,
            RosterId rosterId, EntityId entityId, int side)
        {
            BattleId = battleId;
            CommandId = commandId;
            TargetTick = targetTick;
            Kind = kind;
            RosterId = rosterId;
            EntityId = entityId;
            Side = side;
        }

        public static RosterCommand Deploy(BattleId battleId, ulong commandId, long targetTick, RosterId rosterId) =>
            new RosterCommand(battleId, commandId, targetTick, RosterCommandKind.Deploy, rosterId, default, 0);

        public static RosterCommand Retreat(BattleId battleId, ulong commandId, long targetTick, EntityId entityId) =>
            new RosterCommand(battleId, commandId, targetTick, RosterCommandKind.Retreat, default, entityId, 0);

        public static RosterCommand Surrender(BattleId battleId, ulong commandId, long targetTick, int side) =>
            new RosterCommand(battleId, commandId, targetTick, RosterCommandKind.Surrender, default, default, side);

        // A test action (B1). It has no duel entry point.
        public static RosterCommand KillAll(BattleId battleId, ulong commandId, long targetTick, int requestingSide) =>
            new RosterCommand(battleId, commandId, targetTick, RosterCommandKind.KillAll, default, default, requestingSide);

        public override string ToString() =>
            $"{Kind} #{CommandId} at tick {TargetTick} (roster {RosterId}, entity {EntityId}, side {Side})";
    }
}
