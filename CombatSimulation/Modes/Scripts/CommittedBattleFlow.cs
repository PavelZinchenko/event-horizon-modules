using System;
using System.Collections.Generic;
using CombatSimulation.Core;

namespace CombatSimulation.Modes
{
    // RosterMode's result and timer. Only roster commits enter this timeline. Physics and presentation never rewind.
    internal sealed class CommittedBattleFlow
    {
        internal CommittedBattleFlow(BattleSetup setup, RosterRules rules)
        {
            _timeLimit = rules.TimeLimitTicks;
            _mode = rules.TimerMode;
            _playerSide = rules.PlayerSide;
            _onlyOneShip = rules.OnlyOneShip;
            _eliminationDelay = rules.EliminationDelayTicks;
            _remoteOwners = setup.HasRemoteOwners;
            foreach (var slot in setup.Units)
                if (slot.Side == 0) ++_confirmed.Alive0; else ++_confirmed.Alive1;
            _confirmed.Tick = -1;
            _confirmed.LossDeadline = long.MaxValue;
            _confirmed.EliminationDeadline = long.MaxValue;
        }

        internal void Add(long tick, int side, UnitStatus before, UnitStatus after)
        {
            if (tick <= _confirmed.Tick)
                throw new InvalidOperationException("A roster commit precedes the confirmed result frontier.");
            var index = _pending.Count;
            while (index > 0 && _pending[index - 1].Tick > tick) --index;
            _pending.Insert(index, new Transition(tick, side, before, after));
        }

        internal BattleResult Evaluate(long tick, long confirmedThrough, out BattleResult candidate, out long elapsed)
        {
            confirmedThrough = Math.Min(tick, confirmedThrough);
            var used = 0;
            var result = Advance(ref _confirmed, confirmedThrough, ref used);
            if (used != 0) _pending.RemoveRange(0, used);
            var provisional = _confirmed;
            used = 0;
            candidate = result.IsCompleted ? result : Advance(ref provisional, tick, ref used);
            elapsed = provisional.Elapsed;
            return result;
        }

        private BattleResult Advance(ref State state, long end, ref int index)
        {
            while (state.Tick < end)
            {
                var next = index < _pending.Count ? _pending[index].Tick : long.MaxValue;
                var deadline = Deadline(state);
                var gapEnd = Math.Min(Math.Min(end, next - 1), deadline);
                if (gapEnd > state.Tick)
                {
                    var count = gapEnd - state.Tick;
                    if (state.Active0 > 0 && state.Active1 > 0)
                    {
                        var limit = DrawLimit(state);
                        if (limit > 0 && limit - state.Elapsed <= count)
                        {
                            state.Tick += limit - state.Elapsed;
                            state.Elapsed = limit;
                            return new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.Timeout, state.Tick);
                        }
                        state.Elapsed += count;
                    }
                    state.Tick = gapEnd;
                }
                if (state.Tick == deadline) return AtDeadline(state);
                if (state.Tick == end) break;
                state.Tick = next;
                var resets = Resets.None;
                while (index < _pending.Count && _pending[index].Tick == next)
                {
                    var change = _pending[index++];
                    var alive = (change.After != UnitStatus.Destroyed ? 1 : 0) -
                        (change.Before != UnitStatus.Destroyed ? 1 : 0);
                    var active = (change.After == UnitStatus.Active ? 1 : 0) -
                        (change.Before == UnitStatus.Active ? 1 : 0);
                    if (change.Side == 0) { state.Alive0 += alive; state.Active0 += active; }
                    else { state.Alive1 += alive; state.Active1 += active; }
                    resets |= Classify(change);
                    if (_onlyOneShip && (_remoteOwners || change.Side == _playerSide) &&
                        change.Before == UnitStatus.Active && change.After == UnitStatus.Destroyed)
                        MarkLoss(ref state, next, change.Side);
                }
                if (ShouldReset(resets, state)) state.Elapsed = 0;
                if (state.Alive0 == 0 || state.Alive1 == 0)
                {
                    if (_eliminationDelay == 0) return Elimination(state, next);
                    if (state.EliminationDeadline == long.MaxValue) state.EliminationDeadline = next + _eliminationDelay;
                }
                if (state.Active0 > 0 && state.Active1 > 0)
                {
                    ++state.Elapsed;
                    var limit = DrawLimit(state);
                    if (limit > 0 && state.Elapsed >= limit)
                        return new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.Timeout, next);
                }
                if (next == Deadline(state)) return AtDeadline(state);
            }
            return default;
        }

        // A pending elimination replaces the OnlyOneShip deadline: without the delay it would have ended the battle
        // first. The timer needs no hold: the eliminated side has no active ship.
        private static long Deadline(in State state) =>
            state.EliminationDeadline != long.MaxValue ? state.EliminationDeadline : state.LossDeadline;

        private static BattleResult AtDeadline(in State state) => state.EliminationDeadline != long.MaxValue
            ? Elimination(state, state.EliminationDeadline)
            : ShipLost(state);

        // The first eliminated side loses; both sides without a ship: a draw.
        private static BattleResult Elimination(in State state, long tick) =>
            new BattleResult(state.Alive0 == state.Alive1 ? BattleOutcome.Draw : BattleOutcome.Victory,
                state.Alive0 == state.Alive1 || state.Alive0 > 0 ? 0 : 1, BattleEndReason.Elimination, tick);

        // OnlyOneShip: the first death sets the deadline. With remote owners either side can lose, and first deaths
        // of both sides in one tick are a draw (phase 7, Q1).
        private static void MarkLoss(ref State state, long tick, int side)
        {
            if (state.LossDeadline == long.MaxValue)
            {
                state.LossDeadline = tick + RosterMode.ShipLostTicks;
                state.LosingSide = side;
            }
            else if (state.LossDeadline == tick + RosterMode.ShipLostTicks && state.LosingSide != side)
                state.LosingSide = -1;
        }

        // OnlyOneShip: the other side wins; the reserves stay ready (legacy IsPlayerDefeated).
        private static BattleResult ShipLost(in State state) => state.LosingSide < 0
            ? new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.ShipLost, state.LossDeadline)
            : new BattleResult(BattleOutcome.Victory, 1 - state.LosingSide, BattleEndReason.ShipLost, state.LossDeadline);

        // The elapsed ticks that end the battle in a draw; 0: the timer never draws (it holds, or drains).
        private long DrawLimit(in State state)
        {
            if (_timeLimit <= 0) return 0;
            switch (_mode)
            {
                case TimerMode.Draw: return _timeLimit;
                case TimerMode.CallNextEnemyOrDraw: return state.Ready(1 - _playerSide) == 0 ? 3 * _timeLimit : 0;
                default: return 0;
            }
        }

        [Flags]
        private enum Resets : byte { None = 0, PlayerDeployed = 1, PlayerRemoved = 2, EnemyDeployed = 4, EnemyRemoved = 8 }

        // A deployment enters Active; a removal leaves it (death, retreat, vanish). A reserve destroyed without an
        // entity is neither.
        private Resets Classify(in Transition change)
        {
            var player = change.Side == _playerSide;
            if (change.After == UnitStatus.Active && change.Before != UnitStatus.Active)
                return player ? Resets.PlayerDeployed : Resets.EnemyDeployed;
            if (change.Before == UnitStatus.Active && change.After != UnitStatus.Active)
                return player ? Resets.PlayerRemoved : Resets.EnemyRemoved;
            return Resets.None;
        }

        // Legacy CombatTimer, read after every transition of the tick. Draw: any deployment or removal resets.
        // With remote owners a removal on either side resets (phase 7, Q1).
        private bool ShouldReset(Resets resets, in State state)
        {
            if (resets == Resets.None) return false;
            if (_mode == TimerMode.Draw) return true;
            if (_remoteOwners && (resets & (Resets.PlayerRemoved | Resets.EnemyRemoved)) != 0) return true;
            if ((resets & Resets.EnemyDeployed) != 0) return true;
            if ((resets & Resets.PlayerRemoved) != 0 && _mode == TimerMode.DrainPlayerHp) return true;
            if ((resets & Resets.EnemyRemoved) != 0)
                return _mode != TimerMode.CallNextEnemyOrDraw || state.Ready(1 - _playerSide) > 0;
            return false;
        }

        private struct State
        {
            internal long Tick, Elapsed;
            // OnlyOneShip: the tick that ends the battle after the first death; long.MaxValue while none.
            internal long LossDeadline;
            // RosterRules.EliminationDelayTicks: the tick of the Elimination result after a side lost its last ship;
            // long.MaxValue while none.
            internal long EliminationDeadline;
            // OnlyOneShip: the side whose death set LossDeadline; -1 when both sides lost a ship in that tick.
            internal int LosingSide;
            internal int Alive0, Alive1, Active0, Active1;
            internal readonly int Ready(int side) => side == 0 ? Alive0 - Active0 : Alive1 - Active1;
        }
        private readonly struct Transition
        {
            internal readonly long Tick;
            internal readonly int Side;
            internal readonly UnitStatus Before, After;
            internal Transition(long tick, int side, UnitStatus before, UnitStatus after)
            { Tick = tick; Side = side; Before = before; After = after; }
        }
        private State _confirmed;
        private readonly long _timeLimit;
        private readonly TimerMode _mode;
        private readonly int _playerSide;
        private readonly bool _onlyOneShip;
        private readonly long _eliminationDelay;
        private readonly bool _remoteOwners;
        private readonly List<Transition> _pending = new List<Transition>();
    }
}
