using System;
using CombatSimulation.Core;

namespace CombatSimulation.Modes
{
    // Main's roster battle: Main's adapter deploys and retreats the units with commands; this mode only applies the
    // rules (RosterRules). It uses no ModeContext action except the drain, so it also runs with remote owners (D10).
    public sealed class RosterMode : IBattleMode
    {
        // Legacy CombatManager: the 3 s cooldown before IsPlayerDefeated ends an OnlyOneShip battle.
        public const long ShipLostTicks = 150;

        private const long DrainTicksPerSecond = 50;

        public RosterMode(BattleSetup setup, RosterRules rules)
        {
            _setup = setup ?? throw new ArgumentNullException(nameof(setup));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _flow = new CommittedBattleFlow(setup, rules);
        }

        public RosterRules Rules { get; }

        // Ticks counted toward the time limit since the last reset.
        public long TimerElapsedTicks { get; private set; }

        // Legacy CombatTimer, DrainPlayerHp (TM3): after the limit, each active player roster ship loses k % of its
        // maximum armor and of its maximum shield per second, k being the whole overtime seconds already passed.
        // No resistances; both restart their delay. It reads the timer of the previous tick. Only the owner drains;
        // a remote copy follows through corrections. Drones are not drained. With remote owners both sides drain.
        public void BeginTick(ModeContext context, ReadOnlySpan<ModeCommand> inputs)
        {
            if (Rules.TimerMode != TimerMode.DrainPlayerHp || Rules.TimeLimitTicks <= 0) return;
            var overtime = TimerElapsedTicks - Rules.TimeLimitTicks;
            if (overtime < DrainTicksPerSecond) return;

            var fraction = overtime / DrainTicksPerSecond * 0.01f * Battle.TickDuration;
            for (var i = 0; i < context.UnitCount; ++i)
            {
                var unit = context.GetUnit(i);
                if (Rules.HasPlayerRules(_setup, unit.Side)) context.Drain(unit.UnitId, fraction, fraction);
            }
        }

        // The side limit, then OnlyOneShip: after the first death of a player ship its reserves stay ready.
        public bool AllowDeploy(ModeContext context, in UnitState unit, out string reason)
        {
            reason = null;
            if (context.CountUnits(unit.Side, UnitStatus.Active) >= Rules.MaxActiveShips(unit.Side))
                reason = "side limit";
            else if (Rules.OnlyOneShip && Rules.HasPlayerRules(_setup, unit.Side) &&
                     context.CountUnits(unit.Side, UnitStatus.Destroyed) > 0)
                reason = "only one ship";
            return reason == null;
        }

        public void OnTransition(in UnitChange change) => _flow.Add(change.Tick, change.Side, change.Before, change.After);

        // Elimination first, then the timer.
        public BattleResult EndTick(ModeContext context, long confirmedThrough, out BattleResult candidate)
        {
            var result = _flow.Evaluate(context.Tick, confirmedThrough, out candidate, out var elapsed);
            TimerElapsedTicks = elapsed;
            return result;
        }

        private readonly BattleSetup _setup;
        private readonly CommittedBattleFlow _flow;
    }
}
