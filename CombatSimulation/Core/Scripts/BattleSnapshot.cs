using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // The published state and event batch of one completed tick. A reader must finish with a snapshot
    // before the next Step returns: the battle reuses two buffers, so a snapshot stays intact only while
    // the following tick runs.
    public sealed class BattleSnapshot
    {
        internal BattleSnapshot(BattleId battleId, int rosterSize)
        {
            BattleId = battleId;
            _roster = new RosterSlotState[rosterSize];
            _ships = new ShipState[rosterSize];
        }

        public BattleId BattleId { get; }

        // The completed tick. -1 before the first step.
        public long Tick { get; private set; }

        // Ticks counted toward the time limit since the last reset.
        public long TimerElapsedTicks { get; private set; }

        public BattleResult Result { get; private set; }
        public bool IsCompleted => Result.IsCompleted;

        // Active roster ships in roster order, then drones in entity ID order.
        public ReadOnlySpan<ShipState> Ships => new ReadOnlySpan<ShipState>(_ships, 0, _shipCount);

        // Live bullets in storage order; the order has no meaning.
        public ReadOnlySpan<BulletState> Bullets => new ReadOnlySpan<BulletState>(_bullets, 0, _bulletCount);

        // Every slot in setup order.
        public ReadOnlySpan<RosterSlotState> Roster => new ReadOnlySpan<RosterSlotState>(_roster, 0, _roster.Length);

        // The committed events of this tick, in order.
        public ReadOnlySpan<BattleEvent> Events => new ReadOnlySpan<BattleEvent>(_events, 0, _eventCount);

        public bool TryGetShip(EntityId id, out ShipState ship)
        {
            for (var i = 0; i < _shipCount; ++i)
            {
                if (_ships[i].Id != id) continue;
                ship = _ships[i];
                return true;
            }

            ship = default;
            return false;
        }

        internal void Write(Battle battle, long tick, PhysicsWorld world, Roster roster, BattleEventBuffer events)
        {
            Tick = tick;
            TimerElapsedTicks = battle.TimerElapsedTicks;
            Result = battle.Result;

            var slots = roster.Slots;
            _shipCount = 0;
            for (var i = 0; i < slots.Count; ++i)
            {
                var slot = slots[i];
                _roster[i] = new RosterSlotState(slot);
            }

            if (_ships.Length < battle.ShipEntryCount)
                Array.Resize(ref _ships, Math.Max(battle.ShipEntryCount, _ships.Length * 2));
            for (var i = 0; i < battle.ShipEntryCount; ++i)
            {
                var ship = battle.GetShipEntry(i);
                if (ship != null) _ships[_shipCount++] = new ShipState(ship, world);
            }

            var bullets = battle.Bullets;
            if (_bullets.Length < bullets.AliveCount)
                _bullets = new BulletState[Math.Max(bullets.AliveCount, _bullets.Length * 2)];
            _bulletCount = 0;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i)) _bullets[_bulletCount++] = new BulletState(bullets[i], world);

            if (_events.Length < events.Count)
                _events = new BattleEvent[Math.Max(events.Count, _events.Length * 2)];
            events.CopyTo(_events);
            _eventCount = events.Count;
        }

        private readonly RosterSlotState[] _roster;
        private ShipState[] _ships;
        private int _shipCount;
        private BulletState[] _bullets = new BulletState[16];
        private int _bulletCount;
        private BattleEvent[] _events = new BattleEvent[16];
        private int _eventCount;
    }

    // The events of the tick being processed. Stage 14 copies them into the snapshot.
    internal sealed class BattleEventBuffer
    {
        public int Count { get; private set; }

        public void Add(in BattleEvent item)
        {
            if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
            _items[Count++] = item;
        }

        public void CopyTo(BattleEvent[] target) => Array.Copy(_items, target, Count);

        public void Clear()
        {
            Array.Clear(_items, 0, Count);
            Count = 0;
        }

        private BattleEvent[] _items = new BattleEvent[16];
    }
}
