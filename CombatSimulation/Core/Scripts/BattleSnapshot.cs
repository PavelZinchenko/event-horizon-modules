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
        public BattleResult CandidateResult { get; private set; }
        public bool IsCompleted => Result.IsCompleted;

        // Active roster ships in roster order, then drones in entity ID order.
        public ReadOnlySpan<ShipState> Ships => new ReadOnlySpan<ShipState>(_ships, 0, _shipCount);

        // Live bullets in storage order; the order has no meaning.
        public ReadOnlySpan<BulletState> Bullets => new ReadOnlySpan<BulletState>(_bullets, 0, _bulletCount);

        // Every slot in setup order.
        public ReadOnlySpan<RosterSlotState> Roster => new ReadOnlySpan<RosterSlotState>(_roster, 0, _roster.Length);

        // Presentation events of this tick, in order. Authority distinguishes predictions from commits.
        public ReadOnlySpan<BattleEvent> Events => new ReadOnlySpan<BattleEvent>(_events, 0, _eventCount);

        // Owner-decided hits for remote ships and drones; empty in single player.
        public ReadOnlySpan<ResolvedHit> OutgoingHits => new ReadOnlySpan<ResolvedHit>(_outgoingHits, 0, _hitCount);
        // Atomic owner commits, captured before removed entities leave the stores. Empty in single player.
        public ReadOnlySpan<OwnerOperation> OutgoingOperations => new ReadOnlySpan<OwnerOperation>(_outgoingOperations, 0, _operationCount);
        // Received commits applied this step, retaining original owner ticks for Runtime's later result agreement.
        public ReadOnlySpan<OwnerOperation> AppliedOperations => new ReadOnlySpan<OwnerOperation>(_appliedOperations, 0, _appliedOperationCount);

        // The ship's devices in control-bit order. Valid only for a ShipState read from this snapshot.
        public ReadOnlySpan<DeviceState> GetDevices(in ShipState ship) =>
            new ReadOnlySpan<DeviceState>(_devices, ship.DeviceStart, ship.Definition.Devices.Count);

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
            CandidateResult = battle.CandidateResult;

            var slots = roster.Slots;
            _shipCount = 0;
            for (var i = 0; i < slots.Count; ++i)
            {
                var slot = slots[i];
                _roster[i] = new RosterSlotState(slot);
            }

            if (_ships.Length < battle.ShipEntryCount)
                Array.Resize(ref _ships, Math.Max(battle.ShipEntryCount, _ships.Length * 2));
            var deviceCount = 0;
            for (var i = 0; i < battle.ShipEntryCount; ++i)
            {
                var ship = battle.GetShipEntry(i);
                if (ship == null) continue;

                var devices = ship.DeviceCount;
                if (_devices.Length < deviceCount + devices)
                    Array.Resize(ref _devices, Math.Max(deviceCount + devices, _devices.Length * 2));
                for (var d = 0; d < devices; ++d)
                    _devices[deviceCount + d] = ship.GetDevice(d).GetState(battle, ship, tick);
                _ships[_shipCount++] = new ShipState(ship, world, deviceCount);
                deviceCount += devices;
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
            var hits = battle.OutgoingHits;
            if (_outgoingHits.Length < hits.Count)
                Array.Resize(ref _outgoingHits, Math.Max(hits.Count, _outgoingHits.Length * 2));
            hits.CopyTo(_outgoingHits);
            _hitCount = hits.Count;
            var operations = battle.OutgoingOperations;
            if (_outgoingOperations.Length < operations.Count)
                Array.Resize(ref _outgoingOperations, Math.Max(operations.Count, _outgoingOperations.Length * 2));
            operations.CopyTo(_outgoingOperations);
            _operationCount = operations.Count;
            var applied = battle.AppliedOperations;
            if (_appliedOperations.Length < applied.Count)
                Array.Resize(ref _appliedOperations, Math.Max(applied.Count, _appliedOperations.Length * 2));
            applied.CopyTo(_appliedOperations);
            _appliedOperationCount = applied.Count;
        }

        private readonly RosterSlotState[] _roster;
        private ShipState[] _ships;
        private int _shipCount;
        private DeviceState[] _devices = new DeviceState[16];
        private BulletState[] _bullets = new BulletState[16];
        private int _bulletCount;
        private BattleEvent[] _events = new BattleEvent[16];
        private int _eventCount;
        private ResolvedHit[] _outgoingHits = new ResolvedHit[16];
        private int _hitCount;
        private OwnerOperation[] _outgoingOperations = new OwnerOperation[16];
        private int _operationCount;
        private OwnerOperation[] _appliedOperations = new OwnerOperation[16];
        private int _appliedOperationCount;
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
