using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Battle continuity (D12): one local battle lives for a whole level visit. Battle commands set the ceasefire and
    // replace the rules at a tick boundary, so a replay of the commands gives the same battle.
    public sealed partial class Battle
    {
        // The rules of the running mode: Setup.Rules until a ChangeRules command applies.
        public IBattleRules Rules { get; private set; }

        // ChangeRules validates the rules against the setup here; the validation throws as at setup.
        public bool Submit(in BattleCommand command)
        {
            if (IsStopped) throw new InvalidOperationException("The battle has a result; it accepts no commands.");
            if (Setup.HasRemoteOwners)
                throw new InvalidOperationException("A battle with remote owners has no battle commands (D12).");
            if (command.Kind == BattleCommandKind.ChangeRules) command.Rules?.Validate(Setup);
            var reason = _commands.Add(command, Tick);
            return reason == null || Reject(command, reason);
        }

        // Stage 1, first: in command ID order, before the mode's BeginTick.
        private void ApplyBattleCommands()
        {
            _commands.TakeDue(Tick, _dueBattleCommands);
            foreach (var command in _dueBattleCommands)
            {
                if (command.Kind == BattleCommandKind.Ceasefire) Ceasefire = command.Enabled;
                else ChangeRules(command.Rules);
            }
        }

        // The new mode starts from the current units and ships. The latched result and the ceasefire clear, and each
        // unit's performance becomes zero: the application reads it for the reward before it submits the command.
        // The setup (the world size, the asteroids and the zones) stays.
        private void ChangeRules(IBattleRules rules)
        {
            _mode = rules == null ? OpenMode.Instance
                : rules.CreateMode(Setup) ?? throw new InvalidOperationException("The rules created no mode.");
            Rules = rules;
            Result = default;
            CandidateResult = default;
            TimerElapsedTicks = 0;
            Ceasefire = false;
            foreach (var unit in Units.All) unit.Performance = default;
        }

        private bool Reject(in BattleCommand command, string reason)
        {
            ++RejectedCommandCount;
            GameDiagnostics.Trace.LogWarning($"Battle {BattleId}, tick {Tick}: rejected {command}: {reason}.");
            return false;
        }

        private readonly List<BattleCommand> _dueBattleCommands = new List<BattleCommand>();
    }
}
