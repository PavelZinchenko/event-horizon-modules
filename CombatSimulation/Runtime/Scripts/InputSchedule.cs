using System.Collections.Generic;
using CombatSimulation.Core;

namespace CombatSimulation.Runtime
{
    internal sealed class InputSchedule
    {
        internal bool Add(EntityId entity, long originalTick, in PilotControls controls)
        {
            if (!_entities.TryGetValue(entity, out var state)) _entities.Add(entity, state = new State());
            if (originalTick <= state.AppliedOriginal) return false;
            for (var i = 0; i < state.Frames.Count; ++i)
                if (state.Frames[i].Tick == originalTick) return false;
            state.Frames.Add(new Frame(originalTick, controls));
            return true;
        }

        internal void Apply(BattleRunner runner, int owner, int delay)
        {
            _expired.Clear();
            foreach (var pair in _entities)
            {
                var state = pair.Value;
                var selected = -1;
                for (var i = 0; i < state.Frames.Count; ++i)
                {
                    var frame = state.Frames[i];
                    if (frame.Tick + delay > runner.Tick) continue;
                    if (selected < 0 || frame.Tick > state.Frames[selected].Tick) selected = i;
                }
                if (selected < 0) continue;
                var chosen = state.Frames[selected];
                // Input never controls another owner's ship. A missing spawn will acquire controls on a later frame.
                if (runner.Battle.AcceptsInputFrom(pair.Key, owner) &&
                    runner.ApplyControls(pair.Key, runner.Tick, chosen.Controls))
                {
                    state.AppliedOriginal = chosen.Tick;
                    if (chosen.Controls.SelfDestruct && runner.Battle.Setup.IsLocalOwner(owner))
                        runner.SelfDestruct(pair.Key, runner.Tick);
                }
                for (var i = state.Frames.Count - 1; i >= 0; --i)
                    if (state.Frames[i].Tick <= chosen.Tick) state.Frames.RemoveAt(i);
                if (state.Frames.Count == 0 && !runner.Battle.AcceptsInputFrom(pair.Key, owner))
                    _expired.Add(pair.Key);
            }
            foreach (var entity in _expired) _entities.Remove(entity);
        }

        internal void Forget(EntityId entity) => _entities.Remove(entity);
        private sealed class State
        {
            internal long AppliedOriginal = -1;
            internal readonly List<Frame> Frames = new List<Frame>();
        }
        private readonly struct Frame
        {
            internal readonly long Tick;
            internal readonly PilotControls Controls;
            internal Frame(long tick, in PilotControls controls) { Tick = tick; Controls = controls; }
        }
        private readonly Dictionary<EntityId, State> _entities = new Dictionary<EntityId, State>();
        private readonly List<EntityId> _expired = new List<EntityId>();
    }
}
