namespace CombatSimulation.Core
{
    // A one-shot pilot command. Validation is at stage 1; death is marked at stage 11.
    public readonly struct SelfDestructCommand
    {
        public readonly BattleId BattleId;
        public readonly ulong CommandId;
        public readonly long TargetTick;
        public readonly EntityId EntityId;

        public SelfDestructCommand(BattleId battleId, ulong commandId, long targetTick, EntityId entityId)
        {
            BattleId = battleId;
            CommandId = commandId;
            TargetTick = targetTick;
            EntityId = entityId;
        }
    }
}
