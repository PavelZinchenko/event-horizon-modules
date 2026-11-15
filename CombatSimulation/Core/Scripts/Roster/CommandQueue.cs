using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Commands and control frames that wait for their target tick; each group is in target tick, then command ID order.
    // The queue checks a submission. The battle counts and logs a rejection, and applies the due items at stage 1.
    internal sealed class CommandQueue
    {
        public CommandQueue(BattleId battleId)
        {
            _battleId = battleId;
        }

        // Each Add returns null when it accepts the item, otherwise the reason for the rejection.
        public string Add(in RosterCommand command, long tick)
        {
            if (command.BattleId != _battleId)
                return "wrong battle";
            if (command.TargetTick < tick)
                return "past tick";
            if (!IsValidKind(command.Kind))
                return "unknown kind";
            if ((command.Kind == RosterCommandKind.Surrender || command.Kind == RosterCommandKind.KillAll) &&
                (command.Side < 0 || command.Side >= BattleSetup.SideCount))
                return "invalid side";
            if (!_pendingCommandIds.Add(command.CommandId))
                return "duplicate command ID";

            _rosterCommands.Insert(FindInsertIndex(command), command);
            return null;
        }

        // The entity is checked at stage 1 of the target tick: a frame may name a ship that deploys in that tick.
        public string Add(in ControlFrame frame, long tick)
        {
            if (frame.BattleId != _battleId)
                return "wrong battle";
            if (frame.TargetTick < tick)
                return "past tick";
            if (frame.EntityId.IsNull)
                return "no entity";
            if (!(frame.Throttle >= 0f && frame.Throttle <= 1f))
                return "throttle out of range";
            if (frame.HasCourse && (float.IsNaN(frame.Course) || float.IsInfinity(frame.Course)))
                return "invalid course";
            if (_pendingCommandIds.Contains(frame.CommandId))
                return "duplicate command ID";
            if (!_pendingFrameKeys.Add(new FrameKey(frame.EntityId, frame.TargetTick)))
                return "second frame for the entity and tick";

            _pendingCommandIds.Add(frame.CommandId);
            _frames.Insert(FindInsertIndex(frame), frame);
            return null;
        }

        public string Add(in SelfDestructCommand command, long tick)
        {
            if (command.BattleId != _battleId) return "wrong battle";
            if (command.TargetTick < tick) return "past tick";
            if (command.EntityId.IsNull) return "no entity";
            if (!_pendingCommandIds.Add(command.CommandId)) return "duplicate command ID";

            var index = 0;
            while (index < _selfDestructs.Count &&
                (_selfDestructs[index].TargetTick < command.TargetTick ||
                 (_selfDestructs[index].TargetTick == command.TargetTick && _selfDestructs[index].CommandId < command.CommandId)))
                ++index;
            _selfDestructs.Insert(index, command);
            return null;
        }

        public string Add(in ShipCommand command, long tick)
        {
            if (command.BattleId != _battleId) return "wrong battle";
            if (command.TargetTick < tick) return "past tick";
            if (command.EntityId.IsNull) return "no entity";
            if (command.Kind != ShipCommandKind.Vanish && command.Kind != ShipCommandKind.ChangeAllegiance)
                return "unknown kind";
            if (command.Kind == ShipCommandKind.ChangeAllegiance && !Allegiances.IsValid(command.Allegiance))
                return "invalid allegiance";
            if (!_pendingCommandIds.Add(command.CommandId)) return "duplicate command ID";

            var index = 0;
            while (index < _shipCommands.Count &&
                (_shipCommands[index].TargetTick < command.TargetTick ||
                 (_shipCommands[index].TargetTick == command.TargetTick && _shipCommands[index].CommandId < command.CommandId)))
                ++index;
            _shipCommands.Insert(index, command);
            return null;
        }

        // Each TakeDue clears the list, then moves the items for the tick into it, in queue order.
        public void TakeDue(long tick, List<RosterCommand> due)
        {
            due.Clear();
            var count = 0;
            while (count < _rosterCommands.Count && _rosterCommands[count].TargetTick == tick)
            {
                var command = _rosterCommands[count++];
                _pendingCommandIds.Remove(command.CommandId);
                due.Add(command);
            }
            if (count > 0) _rosterCommands.RemoveRange(0, count);
        }

        public void TakeDue(long tick, List<SelfDestructCommand> due)
        {
            due.Clear();
            var count = 0;
            while (count < _selfDestructs.Count && _selfDestructs[count].TargetTick == tick)
            {
                var command = _selfDestructs[count++];
                _pendingCommandIds.Remove(command.CommandId);
                due.Add(command);
            }
            if (count > 0) _selfDestructs.RemoveRange(0, count);
        }

        public void TakeDue(long tick, List<ShipCommand> due)
        {
            due.Clear();
            var count = 0;
            while (count < _shipCommands.Count && _shipCommands[count].TargetTick == tick)
            {
                var command = _shipCommands[count++];
                _pendingCommandIds.Remove(command.CommandId);
                due.Add(command);
            }
            if (count > 0) _shipCommands.RemoveRange(0, count);
        }

        public void TakeDue(long tick, List<ControlFrame> due)
        {
            due.Clear();
            var count = 0;
            while (count < _frames.Count && _frames[count].TargetTick == tick)
            {
                var frame = _frames[count++];
                _pendingCommandIds.Remove(frame.CommandId);
                _pendingFrameKeys.Remove(new FrameKey(frame.EntityId, frame.TargetTick));
                due.Add(frame);
            }
            if (count > 0) _frames.RemoveRange(0, count);
        }

        private int FindInsertIndex(in RosterCommand command)
        {
            var low = 0;
            var high = _rosterCommands.Count;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                var other = _rosterCommands[middle];
                var before = other.TargetTick < command.TargetTick ||
                             (other.TargetTick == command.TargetTick && other.CommandId < command.CommandId);
                if (before) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private int FindInsertIndex(in ControlFrame frame)
        {
            var low = 0;
            var high = _frames.Count;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                var other = _frames[middle];
                var before = other.TargetTick < frame.TargetTick ||
                             (other.TargetTick == frame.TargetTick && other.CommandId < frame.CommandId);
                if (before) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static bool IsValidKind(RosterCommandKind kind) =>
            kind == RosterCommandKind.Deploy || kind == RosterCommandKind.Retreat ||
            kind == RosterCommandKind.Surrender || kind == RosterCommandKind.KillAll;

        private readonly BattleId _battleId;
        private readonly List<RosterCommand> _rosterCommands = new List<RosterCommand>();
        private readonly List<SelfDestructCommand> _selfDestructs = new List<SelfDestructCommand>();
        private readonly List<ShipCommand> _shipCommands = new List<ShipCommand>();
        private readonly List<ControlFrame> _frames = new List<ControlFrame>();
        private readonly HashSet<FrameKey> _pendingFrameKeys = new HashSet<FrameKey>();
        private readonly HashSet<ulong> _pendingCommandIds = new HashSet<ulong>();

        // One control frame per entity and tick (commands, rule 2).
        private readonly struct FrameKey : IEquatable<FrameKey>
        {
            private readonly EntityId _entityId;
            private readonly long _tick;

            public FrameKey(EntityId entityId, long tick)
            {
                _entityId = entityId;
                _tick = tick;
            }

            public bool Equals(FrameKey other) => _entityId == other._entityId && _tick == other._tick;
            public override bool Equals(object obj) => obj is FrameKey other && Equals(other);
            public override int GetHashCode() => (_entityId.GetHashCode() * 397) ^ _tick.GetHashCode();
        }
    }
}
