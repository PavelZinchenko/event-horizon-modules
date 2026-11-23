using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // The published state and event batch of one completed tick. A reader must finish with a snapshot
    // before the next Step returns: the battle reuses two buffers, so a snapshot stays intact only while
    // the following tick runs. A reader on another thread takes a copy (CopyTo).
    public sealed class BattleSnapshot
    {
        internal BattleSnapshot(BattleId battleId, int rosterSize)
        {
            BattleId = battleId;
            _roster = new UnitState[rosterSize];
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

        // The ceasefire after this tick's battle commands (battle continuity, D12).
        public bool Ceasefire { get; private set; }

        // Active roster ships in roster order, then drones and decoys in entity ID order.
        public ReadOnlySpan<ShipState> Ships => new ReadOnlySpan<ShipState>(_ships, 0, _shipCount);

        // Live bullets in storage order; the order has no meaning.
        public ReadOnlySpan<BulletState> Bullets => new ReadOnlySpan<BulletState>(_bullets, 0, _bulletCount);

        // Live worm tail segments in storage order; the order has no meaning (phase 5, step 7d).
        public ReadOnlySpan<RepairBotState> RepairBots => new ReadOnlySpan<RepairBotState>(_repairBots, 0, _repairBotCount);

        // The shield devices of the ships, in ship and device order (phase 5, step 10d).
        public ReadOnlySpan<ShieldState> Shields => new ReadOnlySpan<ShieldState>(_shields, 0, _shieldCount);
        public ReadOnlySpan<TailSegmentState> Segments => new ReadOnlySpan<TailSegmentState>(_segments, 0, _segmentCount);

        // Live asteroids in setup order (phase 5, step 14).
        public ReadOnlySpan<AsteroidState> Asteroids => new ReadOnlySpan<AsteroidState>(_asteroids, 0, _asteroidCount);

        // Live pickups in storage order; the order has no meaning (loot plan, L7).
        public ReadOnlySpan<PickupState> Pickups => new ReadOnlySpan<PickupState>(_pickups, 0, _pickupCount);

        // Every unit in table order: the setup's units, then the ones a mode added.
        public ReadOnlySpan<UnitState> Units => new ReadOnlySpan<UnitState>(_roster, 0, _unitCount);

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

        public ReadOnlySpan<BarrelState> GetBarrels(in ShipState ship) =>
            new ReadOnlySpan<BarrelState>(_barrels, ship.BarrelStart, ship.Definition.Barrels.Count);

        // In Definition.Satellites order (phase 5, step 12c).
        public ReadOnlySpan<SatelliteState> GetSatellites(in ShipState ship) =>
            new ReadOnlySpan<SatelliteState>(_satellites, ship.SatelliteStart, ship.Definition.Satellites.Count);

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

        // A copy for a reader on another thread (phase 7, step 5): the battle never writes it. Pass a copy of the same
        // battle to reuse its arrays; it allocates only to grow them. The state structs hold only immutable definitions.
        public BattleSnapshot CopyTo(BattleSnapshot target = null)
        {
            target ??= new BattleSnapshot(BattleId, _unitCount);
            if (target == this) throw new ArgumentException("A snapshot cannot copy itself.", nameof(target));
            if (target.BattleId != BattleId)
                throw new ArgumentException("The copy belongs to another battle.", nameof(target));
            target.Tick = Tick;
            target.TimerElapsedTicks = TimerElapsedTicks;
            target.Result = Result;
            target.CandidateResult = CandidateResult;
            target.Ceasefire = Ceasefire;
            target._unitCount = Copy(_roster, _unitCount, ref target._roster);
            target._shipCount = Copy(_ships, _shipCount, ref target._ships);
            target._deviceCount = Copy(_devices, _deviceCount, ref target._devices);
            target._barrelCount = Copy(_barrels, _barrelCount, ref target._barrels);
            target._satelliteCount = Copy(_satellites, _satelliteCount, ref target._satellites);
            target._bulletCount = Copy(_bullets, _bulletCount, ref target._bullets);
            target._repairBotCount = Copy(_repairBots, _repairBotCount, ref target._repairBots);
            target._shieldCount = Copy(_shields, _shieldCount, ref target._shields);
            target._segmentCount = Copy(_segments, _segmentCount, ref target._segments);
            target._asteroidCount = Copy(_asteroids, _asteroidCount, ref target._asteroids);
            target._pickupCount = Copy(_pickups, _pickupCount, ref target._pickups);
            target._eventCount = Copy(_events, _eventCount, ref target._events);
            target._hitCount = Copy(_outgoingHits, _hitCount, ref target._outgoingHits);
            target._operationCount = Copy(_outgoingOperations, _operationCount, ref target._outgoingOperations);
            target._appliedOperationCount = Copy(_appliedOperations, _appliedOperationCount, ref target._appliedOperations);
            return target;
        }

        private static int Copy<T>(T[] source, int count, ref T[] target)
        {
            if (target.Length < count) target = new T[Math.Max(count, target.Length * 2)];
            Array.Copy(source, target, count);
            return count;
        }

        internal void Write(Battle battle, long tick, PhysicsWorld world, UnitTable units, BattleEventBuffer events)
        {
            Tick = tick;
            TimerElapsedTicks = battle.TimerElapsedTicks;
            Result = battle.Result;
            CandidateResult = battle.CandidateResult;
            Ceasefire = battle.Ceasefire;

            // A mode can add units (ModeContext.AddUnit): the array grows as the other arrays do.
            var slots = units.All;
            if (_roster.Length < slots.Count) Array.Resize(ref _roster, Math.Max(slots.Count, _roster.Length * 2));
            _unitCount = slots.Count;
            _shipCount = 0;
            for (var i = 0; i < slots.Count; ++i)
            {
                var slot = slots[i];
                _roster[i] = new UnitState(slot);
            }

            if (_ships.Length < battle.ShipEntryCount)
                Array.Resize(ref _ships, Math.Max(battle.ShipEntryCount, _ships.Length * 2));
            var deviceCount = 0;
            var barrelCount = 0;
            var satelliteCount = 0;
            _shieldCount = 0;
            for (var i = 0; i < battle.ShipEntryCount; ++i)
            {
                var ship = battle.GetShipEntry(i);
                if (ship == null) continue;

                var devices = ship.DeviceCount;
                if (_devices.Length < deviceCount + devices)
                    Array.Resize(ref _devices, Math.Max(deviceCount + devices, _devices.Length * 2));
                for (var d = 0; d < devices; ++d)
                {
                    var device = ship.GetDevice(d);
                    _devices[deviceCount + d] = device.GetState(battle.DeviceContext, ship, tick);
                    if (!(device is ShieldDevice shield)) continue;
                    if (_shieldCount == _shields.Length) Array.Resize(ref _shields, _shields.Length * 2);
                    _shields[_shieldCount++] = new ShieldState(ship, shield, tick);
                }
                var barrels = ship.Definition.Barrels.Count;
                if (_barrels.Length < barrelCount + barrels)
                    Array.Resize(ref _barrels, Math.Max(barrelCount + barrels, _barrels.Length * 2));
                for (var b = 0; b < barrels; ++b) _barrels[barrelCount + b] = new BarrelState(ship.GetBarrel(b));
                var satellites = ship.Definition.Satellites.Count;
                if (_satellites.Length < satelliteCount + satellites)
                    Array.Resize(ref _satellites, Math.Max(satelliteCount + satellites, _satellites.Length * 2));
                for (var s = 0; s < satellites; ++s)
                    _satellites[satelliteCount + s] = new SatelliteState(world, ship.GetSatellite(s));
                _ships[_shipCount++] = new ShipState(ship, world, deviceCount, barrelCount, satelliteCount, tick);
                barrelCount += barrels;
                satelliteCount += satellites;
                deviceCount += devices;
            }
            _deviceCount = deviceCount;
            _barrelCount = barrelCount;
            _satelliteCount = satelliteCount;

            var bullets = battle.Bullets;
            if (_bullets.Length < bullets.AliveCount)
                _bullets = new BulletState[Math.Max(bullets.AliveCount, _bullets.Length * 2)];
            _bulletCount = 0;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i)) _bullets[_bulletCount++] = new BulletState(bullets[i], world);

            var bots = battle.RepairBots.Items;
            if (_repairBots.Length < bots.Count) Array.Resize(ref _repairBots, Math.Max(bots.Count, _repairBots.Length * 2));
            _repairBotCount = 0;
            foreach (var bot in bots)
                if (bot != null) _repairBots[_repairBotCount++] = new RepairBotState(bot, world);

            var segments = battle.Segments;
            if (_segments.Length < segments.AliveCount)
                _segments = new TailSegmentState[Math.Max(segments.AliveCount, _segments.Length * 2)];
            _segmentCount = 0;
            for (var i = 0; i < segments.Capacity; ++i)
                if (segments[i].IsAlive) _segments[_segmentCount++] = new TailSegmentState(segments[i], world);

            var asteroids = battle.Asteroids.Items;
            if (_asteroids.Length < asteroids.Count) Array.Resize(ref _asteroids, asteroids.Count);
            _asteroidCount = 0;
            for (var i = 0; i < asteroids.Count; ++i)
                if (asteroids[i] != null) _asteroids[_asteroidCount++] = new AsteroidState(asteroids[i], world);

            var pickups = battle.Pickups;
            if (_pickups.Length < pickups.AliveCount)
                _pickups = new PickupState[Math.Max(pickups.AliveCount, _pickups.Length * 2)];
            _pickupCount = 0;
            for (var i = 0; i < pickups.Items.Count; ++i)
                if (pickups.Items[i].IsAlive) _pickups[_pickupCount++] = new PickupState(pickups.Items[i], tick);

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

        private UnitState[] _roster;
        private int _unitCount;
        private ShipState[] _ships;
        private int _shipCount;
        private BarrelState[] _barrels = new BarrelState[16];
        private SatelliteState[] _satellites = Array.Empty<SatelliteState>();
        private DeviceState[] _devices = new DeviceState[16];
        private int _deviceCount, _barrelCount, _satelliteCount;
        private BulletState[] _bullets = new BulletState[16];
        private int _bulletCount;
        private RepairBotState[] _repairBots = new RepairBotState[4];
        private ShieldState[] _shields = new ShieldState[4];
        private int _shieldCount;
        private int _repairBotCount;
        private TailSegmentState[] _segments = new TailSegmentState[16];
        private int _segmentCount;
        private AsteroidState[] _asteroids = Array.Empty<AsteroidState>();
        private int _asteroidCount;
        private PickupState[] _pickups = Array.Empty<PickupState>();
        private int _pickupCount;
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

        public ReadOnlySpan<BattleEvent> AsSpan() => new ReadOnlySpan<BattleEvent>(_items, 0, Count);

        public void Clear()
        {
            Array.Clear(_items, 0, Count);
            Count = 0;
        }

        private BattleEvent[] _items = new BattleEvent[16];
    }
}
