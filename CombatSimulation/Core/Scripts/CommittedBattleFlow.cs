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
            foreach (var slot in setup.Roster)
                if (slot.Side == 0) ++_confirmed.Alive0; else ++_confirmed.Alive1;
            _confirmed.Tick = -1;
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
                var gapEnd = Math.Min(end, next - 1);
                if (gapEnd > state.Tick)
                {
                    var count = gapEnd - state.Tick;
                    if (state.Active0 > 0 && state.Active1 > 0)
                    {
                        if (_timeLimit > 0 && _timeLimit - state.Elapsed <= count)
                        {
                            state.Tick += _timeLimit - state.Elapsed;
                            state.Elapsed = _timeLimit;
                            return new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.Timeout, state.Tick);
                        }
                        state.Elapsed += count;
                    }
                    state.Tick = gapEnd;
                }
                if (state.Tick == end) break;
                state.Tick = next;
                while (index < _pending.Count && _pending[index].Tick == next)
                {
                    var change = _pending[index++];
                    var alive = (change.After != RosterStatus.Destroyed ? 1 : 0) -
                        (change.Before != RosterStatus.Destroyed ? 1 : 0);
                    var active = (change.After == RosterStatus.Active ? 1 : 0) -
                        (change.Before == RosterStatus.Active ? 1 : 0);
                    if (change.Side == 0) { state.Alive0 += alive; state.Active0 += active; }
                    else { state.Alive1 += alive; state.Active1 += active; }
                    if (change.Before == RosterStatus.Active || change.After == RosterStatus.Active) state.Elapsed = 0;
                }
                if (state.Alive0 == 0 || state.Alive1 == 0)
                    return new BattleResult(state.Alive0 == state.Alive1 ? BattleOutcome.Draw : BattleOutcome.Victory,
                        state.Alive0 == state.Alive1 || state.Alive0 > 0 ? 0 : 1, BattleEndReason.Elimination, next);
                if (state.Active0 > 0 && state.Active1 > 0)
                {
                    ++state.Elapsed;
                    if (_timeLimit > 0 && state.Elapsed >= _timeLimit)
                        return new BattleResult(BattleOutcome.Draw, 0, BattleEndReason.Timeout, next);
                }
            }
            return default;
        }

        private struct State { internal long Tick, Elapsed; internal int Alive0, Alive1, Active0, Active1; }
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
        private readonly List<Transition> _pending = new List<Transition>();
    }
}
