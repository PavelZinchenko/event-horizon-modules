namespace CombatSimulation.Core
{
    public enum BattleOutcome : byte
    {
        None,
        Victory,
        Draw,
    }

    public enum BattleEndReason : byte
    {
        None,

        // A side lost its last slot: death, surrender or kill-all.
        Elimination,

        // The time limit ran out.
        Timeout,
    }

    public readonly struct BattleResult
    {
        public readonly BattleOutcome Outcome;

        // Valid only for a victory.
        public readonly int WinningSide;

        public readonly BattleEndReason Reason;
        public readonly long Tick;

        public BattleResult(BattleOutcome outcome, int winningSide, BattleEndReason reason, long tick)
        {
            Outcome = outcome;
            WinningSide = winningSide;
            Reason = reason;
            Tick = tick;
        }

        public bool IsCompleted => Outcome != BattleOutcome.None;

        public override string ToString() =>
            Outcome == BattleOutcome.Victory
                ? $"Victory of side {WinningSide} ({Reason}) at tick {Tick}"
                : $"{Outcome} ({Reason}) at tick {Tick}";
    }
}
