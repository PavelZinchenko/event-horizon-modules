namespace CombatSimulation.Core
{
    // An application input for the battle mode: the player buys a life, a cheat button, an exit. The core does not
    // interpret it. It goes through the command queue, so it shares the command ID namespace and the "past tick" check
    // with the other commands, and a replay of the commands gives it again.
    public readonly struct ModeCommand
    {
        public readonly BattleId BattleId;

        // Unique among pending commands. The mode commands of one tick reach the mode in command ID order.
        public readonly ulong CommandId;

        public readonly long TargetTick;

        // Defined by the mode.
        public readonly int Kind;
        public readonly long Argument0;
        public readonly long Argument1;

        public ModeCommand(BattleId battleId, ulong commandId, long targetTick, int kind, long argument0 = 0,
            long argument1 = 0)
        {
            BattleId = battleId;
            CommandId = commandId;
            TargetTick = targetTick;
            Kind = kind;
            Argument0 = argument0;
            Argument1 = argument1;
        }

        public override string ToString() =>
            $"Mode command {Kind} #{CommandId} at tick {TargetTick} ({Argument0}, {Argument1})";
    }

    // What a mode publishes for the application (ModeContext.Publish): lives changed, a wave arrived, a decision is
    // needed. The code and the values are defined by the mode.
    public readonly struct ModeEvent
    {
        public readonly int Code;
        public readonly long Value0;
        public readonly long Value1;

        public ModeEvent(int code, long value0 = 0, long value1 = 0)
        {
            Code = code;
            Value0 = value0;
            Value1 = value1;
        }
    }
}
