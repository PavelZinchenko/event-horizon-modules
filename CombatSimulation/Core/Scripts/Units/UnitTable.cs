using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // The core-owned units (fleet slots). Units keep setup order; simulation stages visit drones after unit ships.
    internal sealed class UnitTable
    {
        private readonly Unit[] _slots;
        private readonly Dictionary<UnitId, Unit> _byId;

        public UnitTable(BattleSetup setup)
        {
            _slots = new Unit[setup.Units.Count];
            _byId = new Dictionary<UnitId, Unit>(_slots.Length);
            for (var i = 0; i < _slots.Length; ++i)
            {
                _slots[i] = new Unit(setup.Units[i]);
                _byId.Add(_slots[i].UnitId, _slots[i]);
            }
        }

        public IReadOnlyList<Unit> All => _slots;

        public Unit Find(UnitId id) => _byId.TryGetValue(id, out var slot) ? slot : null;

        public int CountActive(int side)
        {
            var count = 0;
            foreach (var slot in _slots)
                if (slot.Side == side && slot.Status == UnitStatus.Active)
                    ++count;
            return count;
        }

        public bool HasDestroyed(int side)
        {
            foreach (var slot in _slots)
                if (slot.Side == side && slot.Status == UnitStatus.Destroyed)
                    return true;
            return false;
        }

        // A side is alive while any slot is not destroyed; a ready reserve counts.
        public bool HasAlive(int side)
        {
            foreach (var slot in _slots)
                if (slot.Side == side && slot.Status != UnitStatus.Destroyed)
                    return true;
            return false;
        }
    }
}
