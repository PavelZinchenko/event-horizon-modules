using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Fire confirmations (network plan, step 4). The owner sends a BulletSpawn record for each projectile and beam on
    // the reliable ordered stream, then a watermark. The receiver confirms its matching copies, adds the missing ones,
    // and removes remote bullets that the watermark covers but no record confirms.
    public sealed partial class Battle
    {
        // Call after the operations the watermark covers. It takes effect once every operation sequence up to
        // OperationSequence has been submitted; older or repeated watermarks are ignored.
        public bool Submit(int senderOwner, in OwnerWatermark watermark)
        {
            RequireRunning();
            if (watermark.BattleId != BattleId || watermark.Owner != senderOwner || !IsRemoteOwner(senderOwner) ||
                watermark.Tick < 0) return false;
            if (!_watermarks.TryGetValue(senderOwner, out var state))
                _watermarks.Add(senderOwner, state = new WatermarkState());
            if (watermark.Tick <= state.PendingTick || watermark.OperationSequence < state.PendingSequence) return false;

            // A pending watermark whose operations have all arrived takes effect before a newer one replaces it.
            state.Promote(OperationsThrough(senderOwner));
            state.PendingTick = watermark.Tick;
            state.PendingSequence = watermark.OperationSequence;
            return true;
        }

        // The last spawn tick the owner's records have settled on this device: min(watermark, present). -1 before any.
        internal long ConfirmedThrough(int owner) => _watermarks.TryGetValue(owner, out var state) ? state.Limit : -1;
        internal int PredictedRemovalCount => _predictedRemovals.Count;

        private ulong OperationsThrough(int owner) =>
            _operationSequences.TryGetValue(owner, out var window) ? window.Through : 0;

        private SpawnSample ReadSpawnSample(ShipEntity ship) => new SpawnSample(Tick, World.GetPosition(ship.Body),
            World.GetRotation(ship.Body), World.GetAngle(ship.Body), World.GetLinearVelocity(ship.Body));

        // Stage 5, before any spawn. Only remote ships need the muzzle state of past ticks.
        private void RecordSpawnSamples()
        {
            if (!Setup.HasRemoteOwners) return;
            for (var i = 0; i < ShipEntryCount; ++i)
            {
                var ship = GetShipEntry(i);
                if (ship != null && !Setup.IsLocalOwner(ship.Owner)) ship.RecordSpawnSample(ReadSpawnSample(ship));
            }
        }

        private void CaptureBulletSpawn(in BulletEntity bullet, int barrelIndex, uint sequence)
        {
            if (ShouldCapture(bullet.Owner))
                _outgoingOperations.Add(OwnerOperation.BulletFired(BattleId, bullet.Owner, NextOperationSequence(bullet.Owner),
                    Tick, bullet.Id, bullet.ParentId, bullet.DeviceIndex, barrelIndex, sequence));
        }

        // Confirms the local copy, or adds the missing bullet at its spawn tick. A bullet this device already removed
        // by prediction, or a projectile that has expired, stays gone. The confirmed shot re-syncs the shooter's
        // cannon and barrel timers (G1): an overdue fire input shifts a held weapon's cooldown phase for the whole
        // burst. Energy and recoil are left to corrections.
        private bool ApplyRemoteBulletSpawn(in OwnerOperation operation)
        {
            var ship = FindShip(operation.ParentId);
            if (ship == null || ship.Owner != operation.Owner || operation.DeviceIndex >= ship.DeviceCount) return false;
            var device = ship.GetDevice(operation.DeviceIndex);
            var cannon = device as CannonDevice;
            var beamDevice = device as BeamDevice;
            if (cannon != null ? cannon.Definition.BarrelIndex != operation.BarrelIndex :
                beamDevice == null || beamDevice.Definition.BarrelIndex != operation.BarrelIndex) return false;
            cannon?.ConfirmShot(operation.Tick);
            ship.RecordBarrelShot(operation.BarrelIndex, Math.Max(ship.GetBarrelShotTick(operation.BarrelIndex), operation.Tick));

            if (_entities.TryGet(operation.EntityId, out var handle))
            {
                if (!_bullets.TryGet(handle, out var existing) || _bullets[existing].Owner != operation.Owner) return false;
                _bullets[existing].Confirmed = true;
                return true;
            }

            if (_predictedRemovalIds.Contains(operation.EntityId)) return true;
            if (cannon != null && operation.Tick + cannon.Definition.Bullet.LifetimeTicks - 1 < Tick) return true;
            if (HasDueRemoval(operation.EntityId)) return true;

            int index;
            if (cannon != null)
            {
                if (!ship.TryGetSpawnSample(operation.Tick, out var sample)) sample = ReadSpawnSample(ship);
                index = CreateBullet(ship, operation.DeviceIndex, operation.BarrelIndex, operation.SpawnSequence,
                    cannon.Definition.Bullet, cannon.Definition.Spread, operation.Tick, sample, out _, out _);
            }
            else
            {
                // The owner stopped any earlier beam before this one, so a beam still attached here is a prediction.
                if (beamDevice.TryGetBeam(out var current))
                {
                    if (_bullets.TryGet(current, out var currentIndex))
                        RemoveBulletNow(currentIndex, _bullets[currentIndex].Confirmed ? RemovalReason.Disposal : RemovalReason.Unconfirmed);
                    else beamDevice.Detach(current, Tick);
                }
                index = CreateBeam(ship, beamDevice, operation.SpawnSequence, operation.Tick);
            }

            if (index < 0) return false;
            _bullets[index].Confirmed = true;
            _events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _events.Count, _bullets[index]));
            return true;
        }

        // A bullet that spawned and died between two deliveries: its owner removal is in the same batch.
        private bool HasDueRemoval(EntityId id)
        {
            for (var i = 0; i < _remoteOperations.Count; ++i)
            {
                var operation = _remoteOperations[i];
                if (operation.Tick > Tick) break;
                if (operation.Kind == OwnerOperationKind.Removal && operation.EntityId == id) return true;
            }
            return false;
        }

        // End of stage 5. Every record up to the owner's settled tick has applied, so an unconfirmed copy there was
        // fired on this device only.
        private void RemoveUnconfirmedBullets()
        {
            if (_watermarks.Count == 0) return;
            var settled = false;
            foreach (var pair in _watermarks)
            {
                pair.Value.Promote(OperationsThrough(pair.Key));
                pair.Value.Limit = Math.Min(pair.Value.Through, Tick);
                settled |= pair.Value.Limit >= 0;
            }
            if (!settled) return;

            for (var i = 0; i < _bullets.Capacity; ++i)
            {
                if (!_bullets.IsAlive(i)) continue;
                ref readonly var bullet = ref _bullets[i];
                if (bullet.Confirmed || !_watermarks.TryGetValue(bullet.Owner, out var state) || bullet.SpawnTick > state.Limit)
                    continue;
                RemoveBulletNow(i, RemovalReason.Unconfirmed);
            }

            // A settled entry can meet no further record: its confirmation, if any, has applied.
            var kept = 0;
            for (var i = 0; i < _predictedRemovals.Count; ++i)
            {
                var entry = _predictedRemovals[i];
                if (_watermarks.TryGetValue(entry.Owner, out var state) && entry.SpawnTick <= state.Limit)
                    _predictedRemovalIds.Remove(entry.Id);
                else _predictedRemovals[kept++] = entry;
            }
            _predictedRemovals.RemoveRange(kept, _predictedRemovals.Count - kept);
        }

        // A predicted removal outside stage 12: the bullet leaves at once, before this tick's queries.
        private void RemoveBulletNow(int index, RemovalReason reason)
        {
            ref var bullet = ref _bullets[index];
            bullet.PendingRemoval = reason;
            if (bullet.IsBeam) DetachBeam(bullet, _bullets.GetHandle(index));
            _events.Add(BattleEvent.BulletRemoval(BattleId, Tick, _events.Count, bullet, BattleEventAuthority.Predicted));
            ReleaseBullet(index);
        }

        private void DetachBeam(in BulletEntity beam, EntityHandle handle) => DetachBeam(beam, handle, Tick);

        private void DetachBeam(in BulletEntity beam, EntityHandle handle, long stopTick)
        {
            var ship = FindShip(beam.ParentId);
            if (ship != null && beam.DeviceIndex < ship.DeviceCount && ship.GetDevice(beam.DeviceIndex) is BeamDevice device)
                device.Detach(handle, stopTick);
        }

        // A remote bullet that this device removed before its owner confirmed it (N2, expiry, beam stop).
        private void RecordPredictedRemoval(in BulletEntity bullet)
        {
            if (_predictedRemovalIds.Add(bullet.Id))
                _predictedRemovals.Add(new PredictedRemoval(bullet.Id, bullet.Owner, bullet.SpawnTick));
        }

        private sealed class WatermarkState
        {
            public long Through = -1;
            public long PendingTick = -1;
            public ulong PendingSequence;
            public long Limit = -1;

            public void Promote(ulong operationsThrough)
            {
                if (PendingTick > Through && operationsThrough >= PendingSequence) Through = PendingTick;
            }
        }

        private readonly struct PredictedRemoval
        {
            public readonly EntityId Id;
            public readonly int Owner;
            public readonly long SpawnTick;

            public PredictedRemoval(EntityId id, int owner, long spawnTick)
            {
                Id = id;
                Owner = owner;
                SpawnTick = spawnTick;
            }
        }

        private readonly Dictionary<int, WatermarkState> _watermarks = new Dictionary<int, WatermarkState>();
        private readonly List<PredictedRemoval> _predictedRemovals = new List<PredictedRemoval>();
        private readonly HashSet<EntityId> _predictedRemovalIds = new HashSet<EntityId>();
    }
}
