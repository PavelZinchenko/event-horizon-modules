using System;

namespace CombatSimulation.Core
{
    // The game rules of a battle (battle modes, phase 2): immutable data that BattleSetup holds. Each Battle creates its
    // own mode from them. SetupHash walks the rules by reflection, so the duel peers compare them too. A setup without
    // rules runs an open battle: no deployment limit and no result.
    public interface IBattleRules
    {
        // The BattleSetup constructor calls it last. It throws when the setup does not fit the rules.
        void Validate(BattleSetup setup);

        // Each Battle calls it once, in its constructor.
        IBattleMode CreateMode(BattleSetup setup);
    }

    // Game rule code inside Battle.Step: at stage 1 before the queued commands, and at stage 13. It is deterministic: it
    // reads no Unity state, no wall time and no Dictionary enumeration order. It changes the battle only through the
    // ModeContext actions, so a replay of the commands gives the same battle.
    public interface IBattleMode
    {
        // Stage 1, before the queued commands. context.Snapshot is the last tick. The inputs are the ModeCommands of the
        // tick in command ID order. The ModeContext actions run only here.
        void BeginTick(ModeContext context, ReadOnlySpan<ModeCommand> inputs);

        // Stage 1, for each queued Deploy command, after the core's own checks. False rejects it with the reason.
        bool AllowDeploy(ModeContext context, in UnitState unit, out string reason);

        // Each committed unit transition, by its original tick: a local one at once, a received one when it applies.
        void OnTransition(in UnitChange change);

        // Stage 13. confirmedThrough is the result frontier: the tick itself without remote owners. A completed result
        // ends the battle; the candidate is the provisional result that includes unconfirmed transitions.
        BattleResult EndTick(ModeContext context, long confirmedThrough, out BattleResult candidate);

        // Published in BattleSnapshot.TimerElapsedTicks. 0 for a mode without a timer.
        long TimerElapsedTicks { get; }
    }

    // One committed unit transition, as the UnitTransition event gives it.
    public readonly struct UnitChange
    {
        public readonly long Tick;
        public readonly UnitId UnitId;
        public readonly int Side;
        public readonly UnitStatus Before;
        public readonly UnitStatus After;
        public readonly UnitTransitionReason Reason;

        public UnitChange(long tick, UnitId unitId, int side, UnitStatus before, UnitStatus after,
            UnitTransitionReason reason)
        {
            Tick = tick;
            UnitId = unitId;
            Side = side;
            Before = before;
            After = after;
            Reason = reason;
        }
    }

    // The mode of a setup without rules: every deployment is allowed, and the battle has no result.
    internal sealed class OpenMode : IBattleMode
    {
        public static readonly OpenMode Instance = new OpenMode();

        public void BeginTick(ModeContext context, ReadOnlySpan<ModeCommand> inputs) { }

        public bool AllowDeploy(ModeContext context, in UnitState unit, out string reason)
        {
            reason = null;
            return true;
        }

        public void OnTransition(in UnitChange change) { }

        public BattleResult EndTick(ModeContext context, long confirmedThrough, out BattleResult candidate)
        {
            candidate = default;
            return default;
        }

        public long TimerElapsedTicks => 0;
    }
}
