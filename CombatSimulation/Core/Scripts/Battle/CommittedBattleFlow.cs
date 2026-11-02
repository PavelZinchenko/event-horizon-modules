using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Only roster commits enter this timeline. Physics and presentation never rewind.
    internal sealed class CommittedBattleFlow
    {
        internal CommittedBattleFlow(BattleSetup setup)
        {
            _timeLimit = setup.TimeLimitTicks;
            _mode = setup.TimerMode;
            _playerSide = setup.PlayerSide;
            _onlyOneShip = setup.OnlyOneShip;
            foreach (var slot in setup.Roster)
                if (slot.Side == 0) ++_confirmed.Alive0; else ++_confirmed.Alive1;
            _confirmed.Tick = -1;
            _confirmed.LossDeadline = long.MaxValue;
        }

        internal void Add(long tick, int side, RosterStatus before, RosterStatus after)
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
                var gapEnd = Math.Min(Math.Min(end, next - 1), state.LossDeadline);
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
                if (state.Tick == state.LossDeadline) return ShipLost(state.Tick);
                if (state.Tick == end) break;
                state.Tick = next;
                var resets = Resets.None;
                while (index < _pending.Count && _pending[index].Tick == next)
                {
                    var change = _pending[index++];
                    var alive = (change.After != RosterStatus.Destroyed ? 1 : 0) -
                        (change.Before != RosterStatus.Destroyed ? 1 : 0);
                    var active = (change.After == RosterStatus.Active ? 1 : 0) -
                        (change.Before == RosterStatus.Active ? 1 : 0);
                    if (change.Side == 0) { state.Alive0 += alive; state.Active0 += active; }
                    else { state.Alive1 += alive; state.Active1 += active; }
                    resets |= Classify(change);
                    if (_onlyOneShip && change.Side == _playerSide && change.Before == RosterStatus.Active &&
                        change.After == RosterStatus.Destroyed && state.LossDeadline == long.MaxValue)
                        state.LossDeadline = next + Battle.ShipLostTicks;
                }
                if (ShouldReset(resets, state)) state.Elapsed = 0;
                if (state.Alive0 == 0 || state.Alive1 == 0)
                    return new BattleResult(state.Alive0 == state.Alive1 ? BattleOutcome.Draw : BattleOutcome.Victory,
                        state.Alive0 == state.Alive1 || state.Alive0 > 0 ? 0 : 1, BattleEndReason.Elimination, next);
                if (state.Active0 > 0 && state.Active1 > 0)
                {
                    ++state.Elapsed;
                    var limit = DrawLimit(state);
                    if (limit > 0 && state.Elapsed >= limit)
                        return new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.Timeout, next);
                }
                if (next == state.LossDeadline) return ShipLost(next);
            }
            return default;
        }

        // OnlyOneShip: the enemy side wins; the player reserves stay ready (legacy IsPlayerDefeated).
        private BattleResult ShipLost(long tick) =>
            new BattleResult(BattleOutcome.Victory, 1 - _playerSide, BattleEndReason.ShipLost, tick);

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
            if (change.After == RosterStatus.Active && change.Before != RosterStatus.Active)
                return player ? Resets.PlayerDeployed : Resets.EnemyDeployed;
            if (change.Before == RosterStatus.Active && change.After != RosterStatus.Active)
                return player ? Resets.PlayerRemoved : Resets.EnemyRemoved;
            return Resets.None;
        }

        // Legacy CombatTimer, read after every transition of the tick. Draw: any deployment or removal resets.
        private bool ShouldReset(Resets resets, in State state)
        {
            if (resets == Resets.None) return false;
            if (_mode == TimerMode.Draw) return true;
            if ((resets & Resets.EnemyDeployed) != 0) return true;
            if ((resets & Resets.PlayerRemoved) != 0 && _mode == TimerMode.DrainPlayerHp) return true;
            if ((resets & Resets.EnemyRemoved) != 0)
                return _mode != TimerMode.CallNextEnemyOrDraw || state.Ready(1 - _playerSide) > 0;
            return false;
        }

        private struct State
        {
            internal long Tick, Elapsed;
            // OnlyOneShip: the tick that ends the battle after a player death; long.MaxValue while none.
            internal long LossDeadline;
            internal int Alive0, Alive1, Active0, Active1;
            internal readonly int Ready(int side) => side == 0 ? Alive0 - Active0 : Alive1 - Active1;
        }
        private readonly struct Transition
        {
            internal readonly long Tick;
            internal readonly int Side;
            internal readonly RosterStatus Before, After;
            internal Transition(long tick, int side, RosterStatus before, RosterStatus after)
            { Tick = tick; Side = side; Before = before; After = after; }
        }
        private State _confirmed;
        private readonly long _timeLimit;
        private readonly TimerMode _mode;
        private readonly int _playerSide;
        private readonly bool _onlyOneShip;
        private readonly List<Transition> _pending = new List<Transition>();
    }
}
