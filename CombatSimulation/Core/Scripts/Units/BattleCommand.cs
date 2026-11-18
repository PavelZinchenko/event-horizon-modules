namespace CombatSimulation.Core
{
    public enum BattleCommandKind : byte
    {
        // Sets or clears the ceasefire (battle continuity, D12): every ship's control bits and turret requests are masked.
        Ceasefire,

        // Replaces the battle mode (battle continuity, D12): the result, the ceasefire and the unit performance reset.
        ChangeRules,
    }

    // A command for the whole battle, from the application. Local battles only: a battle with remote owners rejects it.
    // It applies at stage 1 before the mode's BeginTick, so a new mode runs from its target tick.
    public readonly struct BattleCommand
    {
        public readonly BattleId BattleId;
        public readonly ulong CommandId;
        public readonly long TargetTick;
        public readonly BattleCommandKind Kind;

        // Ceasefire only.
        public readonly bool Enabled;

        // ChangeRules only. Validated against the setup at submission.
        public readonly IBattleRules Rules;

        private BattleCommand(BattleId battleId, ulong commandId, long targetTick, BattleCommandKind kind, bool enabled,
            IBattleRules rules)
        {
            BattleId = battleId;
            CommandId = commandId;
            TargetTick = targetTick;
            Kind = kind;
            Enabled = enabled;
            Rules = rules;
        }

        public static BattleCommand Ceasefire(BattleId battleId, ulong commandId, long targetTick, bool enabled) =>
            new BattleCommand(battleId, commandId, targetTick, BattleCommandKind.Ceasefire, enabled, null);

        public static BattleCommand ChangeRules(BattleId battleId, ulong commandId, long targetTick, IBattleRules rules) =>
            new BattleCommand(battleId, commandId, targetTick, BattleCommandKind.ChangeRules, false, rules);

        public override string ToString() => Kind == BattleCommandKind.Ceasefire
            ? $"{Kind} {(Enabled ? "on" : "off")} #{CommandId} at tick {TargetTick}"
            : $"{Kind} #{CommandId} at tick {TargetTick} ({Rules?.GetType().Name ?? "open"})";
    }
}
