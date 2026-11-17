using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // The core-owned units (fleet slots). Units keep setup order; simulation stages visit drones after unit ships.
    // A mode can add units during the battle (ModeContext.AddUnit); units are never removed.
    internal sealed class UnitTable
    {
        private readonly List<Unit> _slots;
        private readonly Dictionary<UnitId, Unit> _byId;

        public UnitTable(BattleSetup setup)
        {
            _slots = new List<Unit>(setup.Units.Count);
            _byId = new Dictionary<UnitId, Unit>(setup.Units.Count);
            foreach (var unit in setup.Units) Add(unit);
        }

        public IReadOnlyList<Unit> All => _slots;

        public Unit Find(UnitId id) => _byId.TryGetValue(id, out var slot) ? slot : null;

        public Unit Add(UnitSetup setup)
        {
            var slot = new Unit(setup);
            _byId.Add(slot.UnitId, slot);
            _slots.Add(slot);
            return slot;
        }

        public int Count(int side, UnitStatus status)
        {
            var count = 0;
            foreach (var slot in _slots)
                if (slot.Side == side && slot.Status == status)
                    ++count;
            return count;
        }

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
