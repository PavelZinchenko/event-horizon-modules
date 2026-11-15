namespace CombatSimulation.Core
{
    public enum ShipCommandKind : byte
    {
        // Leaves the battle without a death (D12): no explosion and no kill credit. A roster ship retreats at once.
        Vanish,

        // Sets the allegiance of the ship and of its bullets and beams (D13).
        ChangeAllegiance,
    }

    // A one-shot pilot command for a ship of the local owner (phase 4, step 7e). Validation is at stage 1; a vanish
    // applies at stage 12, an allegiance change at stage 1.
    public readonly struct ShipCommand
    {
        public readonly BattleId BattleId;
        public readonly ulong CommandId;
        public readonly long TargetTick;
        public readonly EntityId EntityId;
        public readonly ShipCommandKind Kind;

        // ChangeAllegiance only.
        public readonly Allegiance Allegiance;

        private ShipCommand(BattleId battleId, ulong commandId, long targetTick, EntityId entityId, ShipCommandKind kind,
            Allegiance allegiance)
        {
            BattleId = battleId;
            CommandId = commandId;
            TargetTick = targetTick;
            EntityId = entityId;
            Kind = kind;
            Allegiance = allegiance;
        }

        public static ShipCommand Vanish(BattleId battleId, ulong commandId, long targetTick, EntityId entityId) =>
            new ShipCommand(battleId, commandId, targetTick, entityId, ShipCommandKind.Vanish, default);

        public static ShipCommand ChangeAllegiance(BattleId battleId, ulong commandId, long targetTick, EntityId entityId,
            Allegiance allegiance) =>
            new ShipCommand(battleId, commandId, targetTick, entityId, ShipCommandKind.ChangeAllegiance, allegiance);

        public override string ToString() => Kind == ShipCommandKind.ChangeAllegiance
            ? $"{Kind} #{CommandId} at tick {TargetTick} (entity {EntityId}, {Allegiance})"
            : $"{Kind} #{CommandId} at tick {TargetTick} (entity {EntityId})";
    }
}
