using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // The core-owned fleet roster. Slots keep setup order, which is the processing order of every stage.
    internal sealed class Roster
    {
        private readonly RosterSlot[] _slots;
        private readonly Dictionary<RosterId, RosterSlot> _byId;

        public Roster(BattleSetup setup)
        {
            _slots = new RosterSlot[setup.Roster.Count];
            _byId = new Dictionary<RosterId, RosterSlot>(_slots.Length);
            for (var i = 0; i < _slots.Length; ++i)
            {
                _slots[i] = new RosterSlot(setup.Roster[i]);
                _byId.Add(_slots[i].RosterId, _slots[i]);
            }
        }

        public IReadOnlyList<RosterSlot> Slots => _slots;

        public RosterSlot Find(RosterId id) => _byId.TryGetValue(id, out var slot) ? slot : null;

        public int CountActive(int side)
        {
            var count = 0;
            foreach (var slot in _slots)
                if (slot.Side == side && slot.Status == RosterStatus.Active)
                    ++count;
            return count;
        }

        // A side is alive while any slot is not destroyed; a ready reserve counts.
        public bool HasAlive(int side)
        {
            foreach (var slot in _slots)
                if (slot.Side == side && slot.Status != RosterStatus.Destroyed)
                    return true;
            return false;
        }
    }
}
