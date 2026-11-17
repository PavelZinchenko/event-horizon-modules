namespace CombatSimulation.Core
{
    public enum UnitCommandKind : byte
    {
        Deploy = 1,
        Retreat = 2,
        Surrender = 3,
        KillAll = 4,
    }

    // A roster request for a target tick. Submitting it does not mean that it succeeds:
    // the core validates it again against committed state at stage 1 of its tick.
    public readonly struct UnitCommand
    {
        public readonly BattleId BattleId;

        // Unique among pending commands. Commands of one tick apply in command ID order.
        public readonly ulong CommandId;

        public readonly long TargetTick;
        public readonly UnitCommandKind Kind;

        // Deploy only.
        public readonly UnitId UnitId;

        // Retreat only.
        public readonly EntityId EntityId;

        // Surrender: the side that gives up. Kill-all: the requesting side; the other side is destroyed.
        public readonly int Side;

        private UnitCommand(BattleId battleId, ulong commandId, long targetTick, UnitCommandKind kind,
            UnitId rosterId, EntityId entityId, int side)
        {
            BattleId = battleId;
            CommandId = commandId;
            TargetTick = targetTick;
            Kind = kind;
            UnitId = rosterId;
            EntityId = entityId;
            Side = side;
        }

        public static UnitCommand Deploy(BattleId battleId, ulong commandId, long targetTick, UnitId rosterId) =>
            new UnitCommand(battleId, commandId, targetTick, UnitCommandKind.Deploy, rosterId, default, 0);

        public static UnitCommand Retreat(BattleId battleId, ulong commandId, long targetTick, EntityId entityId) =>
            new UnitCommand(battleId, commandId, targetTick, UnitCommandKind.Retreat, default, entityId, 0);

        public static UnitCommand Surrender(BattleId battleId, ulong commandId, long targetTick, int side) =>
            new UnitCommand(battleId, commandId, targetTick, UnitCommandKind.Surrender, default, default, side);

        // A test action (B1). It has no duel entry point.
        public static UnitCommand KillAll(BattleId battleId, ulong commandId, long targetTick, int requestingSide) =>
            new UnitCommand(battleId, commandId, targetTick, UnitCommandKind.KillAll, default, default, requestingSide);

        public override string ToString() =>
            $"{Kind} #{CommandId} at tick {TargetTick} (roster {UnitId}, entity {EntityId}, side {Side})";
    }
}
