using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Pooled ship entities with a LIFO free list. Allocation order never affects public IDs.
    internal sealed class ShipStore
    {
        private readonly List<ShipEntity> _items = new List<ShipEntity>();
        private readonly List<int> _free = new List<int>();

        public int AliveCount { get; private set; }

        public ShipEntity Allocate()
        {
            ShipEntity ship;
            if (_free.Count > 0)
            {
                var last = _free.Count - 1;
                ship = _items[_free[last]];
                _free.RemoveAt(last);
            }
            else
            {
                ship = new ShipEntity(_items.Count);
                _items.Add(ship);
                // Removal must not allocate when a warmed pool first becomes empty.
                if (_free.Capacity < _items.Count) _free.Capacity = _items.Capacity;
            }

            ship.Allocate();
            ++AliveCount;
            return ship;
        }

        public void Release(ShipEntity ship)
        {
            ship.Release();
            _free.Add(ship.Index);
            --AliveCount;
        }

        // Returns null for a stale or foreign handle.
        public ShipEntity Get(EntityHandle handle)
        {
            if (handle.Kind != EntityKind.Ship || handle.Index < 0 || handle.Index >= _items.Count)
                return null;

            var ship = _items[handle.Index];
            return ship.IsAlive && ship.Generation == handle.Generation ? ship : null;
        }
    }
}
