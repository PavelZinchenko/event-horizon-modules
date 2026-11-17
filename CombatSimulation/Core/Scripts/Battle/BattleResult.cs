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

        // OnlyOneShip: a player roster ship died, and its reserves may not deploy (phase 4, step 9). With remote
        // owners either side can lose; first deaths of both sides in one tick are a draw (phase 7, Q1).
        ShipLost,

        // A battle mode ended the battle; BattleResult.ModeReason says why.
        Mode,
    }

    public readonly struct BattleResult
    {
        public readonly BattleOutcome Outcome;

        // Valid only for a victory.
        public readonly int WinningSide;

        public readonly BattleEndReason Reason;
        public readonly long Tick;

        // BattleEndReason.Mode only: defined by the mode (Frontier: level complete, starbase lost, out of lives, exit).
        public readonly ushort ModeReason;

        public BattleResult(BattleOutcome outcome, int winningSide, BattleEndReason reason, long tick,
            ushort modeReason = 0)
        {
            Outcome = outcome;
            WinningSide = winningSide;
            Reason = reason;
            Tick = tick;
            ModeReason = modeReason;
        }

        public bool IsCompleted => Outcome != BattleOutcome.None;

        public override string ToString() =>
            Outcome == BattleOutcome.Victory
                ? $"Victory of side {WinningSide} ({ReasonText}) at tick {Tick}"
                : $"{Outcome} ({ReasonText}) at tick {Tick}";

        private string ReasonText => Reason == BattleEndReason.Mode ? $"Mode {ModeReason}" : Reason.ToString();
    }
}
