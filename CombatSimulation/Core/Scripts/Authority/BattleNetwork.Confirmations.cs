using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Fire confirmations (network plan, step 4). The owner sends a BulletSpawn record for each projectile and beam on
    // the reliable ordered stream, then a watermark. The receiver confirms its matching copies, adds the missing ones,
    // and removes remote bullets that the watermark covers but no record confirms.
    internal sealed partial class BattleNetwork
    {
        internal bool Submit(int senderOwner, in OwnerWatermark watermark)
        {
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

        // Stage 5, before any spawn. Only remote ships need the muzzle state of past ticks.
        internal void RecordSpawnSamples()
        {
            if (!Setup.HasRemoteOwners) return;
            for (var i = 0; i < _world.ShipEntryCount; ++i)
            {
                var ship = _world.GetShipEntry(i);
                if (ship != null && !Setup.IsLocalOwner(ship.Owner)) ship.RecordSpawnSample(_world.ReadSpawnSample(ship));
            }
        }

        // A plain projectile sends its launch (P1): the receiver's copy of the shooter aims a little differently, and
        // the error grows with flight time.
        // The source is the device index, or the trigger index for a child bullet; a child always sends its launch.
        // A charging weapon's shot sends its charge ticks (phase 5, step 4f, H7); a child takes its parent's charge.
        internal void CaptureBulletSpawn(in BulletEntity bullet, int source, int barrelIndex, uint sequence, long spawnTick,
            bool sendLaunch = false, Vec2 muzzle = default, float angle = 0f, int chargeTicks = 0)
        {
            if (!ShouldCapture(bullet.Owner)) return;
            var operationSequence = NextOperationSequence(bullet.Owner);
            _outgoingOperations.Add(sendLaunch
                ? OwnerOperation.BulletFired(BattleId, bullet.Owner, operationSequence, spawnTick, bullet.Id,
                    bullet.ParentId, source, barrelIndex, sequence, muzzle, angle, chargeTicks)
                : OwnerOperation.BulletFired(BattleId, bullet.Owner, operationSequence, spawnTick, bullet.Id,
                    bullet.ParentId, source, barrelIndex, sequence, chargeTicks));
        }

        // Confirms the local copy, adopts a copy fired at another tick (P2), or adds the missing bullet at its spawn
        // tick. A bullet this device already removed by prediction, or a projectile that has expired, stays gone. The
        // confirmed shot re-syncs the shooter's cannon and barrel timers (G1): an overdue fire input shifts a held
        // weapon's cooldown phase for the whole burst. Energy and recoil are left to corrections.
        // A record whose parent is not a ship names a child bullet (phase 5, step 2). A charging weapon's record names
        // the charge (step 4f): the copy builds the bullet or beam of the owner's charge, or gives it to its own copy.
        private bool ApplyRemoteBulletSpawn(in OwnerOperation operation)
        {
            var ship = _world.FindShip(operation.ParentId);
            if (ship == null) return _triggers.ApplyRemoteChildSpawn(operation);
            if (ship.Owner != operation.Owner || operation.DeviceIndex >= ship.DeviceCount) return false;
            var device = ship.GetDevice(operation.DeviceIndex);
            if (device is DetonatorDevice detonator) return ApplyRemoteExplosion(ship, detonator, operation);
            if (device is WormTailDevice tail) return _tails.ApplyRemoteSegment(ship, tail, operation);
            var cannon = device as CannonDevice;
            var beamDevice = device as BeamDevice;
            if (cannon != null ? cannon.Definition.BarrelIndex != operation.BarrelIndex :
                beamDevice == null || beamDevice.Definition.BarrelIndex != operation.BarrelIndex) return false;
            cannon?.ConfirmShot(operation.Tick);
            beamDevice?.ConfirmShot(operation.Tick);
            var bulletDefinition = cannon?.BulletFor(operation.ChargeTicks);
            var beamDefinition = beamDevice?.BeamFor(operation.ChargeTicks);
            if (cannon == null || cannon.ShotUsesBarrel)
                ship.RecordBarrelShot(operation.BarrelIndex, Math.Max(ship.GetBarrelShotTick(operation.BarrelIndex), operation.Tick));

            if (_world.Entities.TryGet(operation.EntityId, out var handle))
            {
                if (!_world.Bullets.TryGet(handle, out var existing) || _world.Bullets[existing].Owner != operation.Owner) return false;
                TakeCharge(cannon, existing, bulletDefinition, beamDefinition);
                _world.Bullets[existing].Confirmed = true;
                _triggers.RememberTriggerParent(_world.Bullets[existing]);
                if (cannon != null && operation.HasLaunchPose) Reaim(ship, cannon, existing, operation);
                return true;
            }

            // A copy that is gone here: the owner may still send its children.
            if (_predictedRemovalIds.Contains(operation.EntityId) || cannon != null && HasExpired(cannon, bulletDefinition, operation) ||
                beamDevice != null && HasExpired(beamDefinition, operation) || HasDueRemoval(operation.EntityId))
            {
                if (cannon != null && bulletDefinition.HasSpawnTriggers)
                {
                    if (!ship.TryGetSpawnSample(operation.Tick, out var shot)) shot = _world.ReadSpawnSample(ship);
                    _world.Launch(ship, operation.BarrelIndex, operation.EntityId, bulletDefinition, cannon.Definition.Spread,
                        cannon.Definition.RandomFactor, operation.Tick, shot, operation.HasLaunchPose ? operation : default,
                        out _, out _, out _, out var velocity, out _);
                    _triggers.RememberTriggerParent(ship, operation.DeviceIndex, bulletDefinition, operation.EntityId, velocity);
                }
                return true;
            }

            var copy = FindCopyOfShot(device, operation);
            if (copy >= 0)
            {
                TakeCharge(cannon, copy, bulletDefinition, beamDefinition);
                Adopt(ship, cannon, copy, operation);
                _triggers.RememberTriggerParent(_world.Bullets[copy]);
                return true;
            }

            int index;
            if (cannon != null)
            {
                // A continuous cannon's owner lost its earlier bullet before this one, so a bullet still held here is a
                // prediction (phase 5, step 4g).
                if (cannon.Definition.Mode == CannonMode.Continuous && cannon.TryGetHeldBullet(out var held) &&
                    _world.Bullets.TryGet(held, out var heldIndex))
                    RemoveBulletNow(heldIndex, _world.Bullets[heldIndex].Confirmed ? RemovalReason.Disposal : RemovalReason.Unconfirmed);
                if (!ship.TryGetSpawnSample(operation.Tick, out var sample)) sample = _world.ReadSpawnSample(ship);
                index = _world.CreateBullet(ship, operation.DeviceIndex, operation.BarrelIndex, operation.SpawnSequence,
                    bulletDefinition, cannon.Definition.Spread, cannon.Definition.RandomFactor, operation.Tick, sample,
                    out _, out _, out _, operation.HasLaunchPose ? operation : default);
            }
            else
            {
                // The owner stopped any earlier beam before this one, so a beam still attached here is a prediction. A shot
                // beam device holds no beam.
                if (beamDevice.TryGetBeam(out var current))
                {
                    if (_world.Bullets.TryGet(current, out var currentIndex))
                        RemoveBulletNow(currentIndex, _world.Bullets[currentIndex].Confirmed ? RemovalReason.Disposal : RemovalReason.Unconfirmed);
                    else beamDevice.Detach(current, Tick);
                }
                index = _world.CreateBeam(ship, beamDevice, beamDefinition, operation.SpawnSequence, operation.Tick);
            }

            if (index < 0) return false;
            _world.Bullets[index].Confirmed = true;
            _triggers.RememberTriggerParent(_world.Bullets[index]);
            _world.Events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _world.Events.Count, _world.Bullets[index]));
            return true;
        }

        // Phase 5, step 7b (H8): a remote Detonator's explosion at the owner's ship centre. The copy never derives it, so
        // it appears one network delay late; an explosion that has expired here is skipped.
        private bool ApplyRemoteExplosion(ShipEntity ship, DetonatorDevice detonator, in OwnerOperation operation)
        {
            var explosion = detonator.Definition.Explosion;
            if (!operation.HasLaunchPose || operation.BarrelIndex != 0 || _world.Entities.TryGet(operation.EntityId, out _))
                return false;
            if (operation.Tick + explosion.LifetimeTicks - 1 < Tick || HasDueRemoval(operation.EntityId)) return true;

            var index = _world.CreateBullet(ship, operation.DeviceIndex, SpawnRequest.ShipCentre, operation.SpawnSequence,
                explosion, 0f, 0f, operation.Tick, _world.ReadSpawnSample(ship), out _, out _, out _, operation);
            if (index < 0) return false;
            _world.Bullets[index].Confirmed = true;
            _world.Events.Add(BattleEvent.BulletSpawn(BattleId, Tick, _world.Events.Count, _world.Bullets[index]));
            return true;
        }

        // P1: the copy of a plain projectile takes the owner's launch, on its own line from the spawn tick to the present,
        // without hit tests. Contacts the copy made before are not undone; a copy already removed is not re-added.
        private void Reaim(ShipEntity ship, CannonDevice cannon, int index, in OwnerOperation launch)
        {
            ref readonly var bullet = ref _world.Bullets[index];
            // An attached copy rides on its own ship copy (phase 5, step 3).
            if (bullet.IsBeam || bullet.Definition.IsHoming || bullet.Definition.AttachedToParent) return;
            if (!ship.TryGetSpawnSample(launch.Tick, out var sample)) sample = _world.ReadSpawnSample(ship);
            _world.Launch(ship, launch.BarrelIndex, bullet.Id, bullet.Definition, cannon.Definition.Spread,
                cannon.Definition.RandomFactor, bullet.SpawnTick, sample, launch, out _, out var angle, out _, out var velocity, out var position);
            Physics.SetBulletPose(bullet.Body, position, angle);
            Physics.SetBulletVelocity(bullet.Body, velocity);
        }

        // P2: an unconfirmed copy of the confirmed shot from the same device, barrel and spawn sequence, at another spawn
        // tick (an overdue fire input fires it late).
        // - Continuous beam: the device's attached beam at any tick. The owner stopped any earlier beam before this one,
        //   so the attached beam is a prediction; if it was really a later beam, that beam's records restart it.
        // - Continuous cannon (phase 5, step 4g): its held bullet at any tick, as a continuous beam.
        // - Projectile or shot beam: the window is shorter than the weapon's shortest spacing between two shots, so the
        //   copy can be no other shot of the owner (a wider window would shift every copy of a held burst). The nearest
        //   spawn tick wins, then the earlier one.
        private int FindCopyOfShot(ShipDevice device, in OwnerOperation operation)
        {
            if (device is BeamDevice beamDevice && !beamDevice.Definition.IsShot)
            {
                if (!beamDevice.TryGetBeam(out var current) || !_world.Bullets.TryGet(current, out var beam)) return -1;
                return IsCopyOfShot(_world.Bullets[beam], operation, long.MaxValue) ? beam : -1;
            }
            if (device is CannonDevice continuous && continuous.Definition.Mode == CannonMode.Continuous)
            {
                if (!continuous.TryGetHeldBullet(out var held) || !_world.Bullets.TryGet(held, out var index)) return -1;
                return IsCopyOfShot(_world.Bullets[index], operation, long.MaxValue) ? index : -1;
            }

            var window = CopyWindow(device);
            if (window <= 0) return -1;
            var found = -1;
            var foundTick = 0L;
            for (var i = 0; i < _world.Bullets.Capacity; ++i)
            {
                if (!_world.Bullets.IsAlive(i)) continue;
                ref readonly var bullet = ref _world.Bullets[i];
                if (!IsCopyOfShot(bullet, operation, window)) continue;
                var distance = Math.Abs(bullet.SpawnTick - operation.Tick);
                var foundDistance = Math.Abs(foundTick - operation.Tick);
                if (found >= 0 && (distance > foundDistance || distance == foundDistance && bullet.SpawnTick > foundTick))
                    continue;
                found = i;
                foundTick = bullet.SpawnTick;
            }
            return found;
        }

        private static bool IsCopyOfShot(in BulletEntity bullet, in OwnerOperation operation, long window) =>
            !bullet.Confirmed && bullet.PendingRemoval == RemovalReason.None && bullet.Owner == operation.Owner &&
            bullet.ParentId == operation.ParentId && bullet.DeviceIndex == operation.DeviceIndex &&
            bullet.SpawnTick != operation.Tick && Math.Abs(bullet.SpawnTick - operation.Tick) <= window &&
            bullet.Id == SpawnIdentity.CreateChild(bullet.ParentId, bullet.SpawnTick, (uint)operation.DeviceIndex,
                (uint)operation.BarrelIndex, operation.SpawnSequence);

        // An aura's weapon renews its lifetime, so only the owner's removal record ends it (phase 5, step 4g).
        private bool HasExpired(CannonDevice cannon, BulletDefinition definition, in OwnerOperation operation)
        {
            if (definition.IsAura) return false;
            _world.DrawShot(operation.EntityId, operation.Tick, definition, cannon.Definition.RandomFactor,
                out _, out var lifetimeTicks);
            return operation.Tick + lifetimeTicks - 1 < Tick;
        }

        private bool HasExpired(BeamDefinition beam, in OwnerOperation operation) =>
            beam.IsShot && operation.Tick + beam.LifetimeTicks - 1 < Tick;

        // A cannon or a shot beam fires at most once per cooldown and barrel cooldown; a machine gun once per barrel
        // cooldown; a charging weapon once per barrel cooldown and shortest charge.
        private static long CopyWindow(ShipDevice device)
        {
            var spacing = device is CannonDevice cannon ? cannon.ShotSpacingTicks : ((BeamDevice)device).ShotSpacingTicks;
            return Math.Max(1, spacing) - 1;
        }

        // Phase 5, step 4f: a copy fired from this device's prediction of the charge takes the owner's charge. Adopt
        // then sets the expiry of a copy fired at another tick. The body keeps the radius of the predicted charge, and
        // contacts so far stay.
        private void TakeCharge(CannonDevice cannon, int index, BulletDefinition bulletDefinition,
            BeamDefinition beamDefinition)
        {
            ref var bullet = ref _world.Bullets[index];
            if (bullet.IsBeam)
            {
                if (beamDefinition == null || !beamDefinition.IsCharged || bullet.BeamDefinition == beamDefinition) return;
                bullet.BeamDefinition = beamDefinition;
                bullet.ExpiryTick = bullet.SpawnTick + beamDefinition.LifetimeTicks - 1;
                return;
            }

            if (cannon == null || cannon.Definition.Mode != CannonMode.Charging || bullet.Definition == bulletDefinition)
                return;
            bullet.Definition = bulletDefinition;
            _world.DrawShot(bullet.Id, bullet.SpawnTick, bulletDefinition, cannon.Definition.RandomFactor, out _,
                out var lifetimeTicks);
            bullet.ExpiryTick = bullet.SpawnTick + lifetimeTicks - 1;
        }

        // The copy takes the owner's ID, spawn tick and expiry; a plain projectile also takes the owner's launch (P1).
        // The body, beam attachment and contacts so far stay. The Rename event lets the scene keep the copy's view.
        private void Adopt(ShipEntity ship, CannonDevice cannon, int index, in OwnerOperation operation)
        {
            ref var bullet = ref _world.Bullets[index];
            var previous = bullet.Id;
            _world.Entities.Remove(previous);
            _world.TryRegister(operation.EntityId, _world.Bullets.GetHandle(index));
            bullet.Id = operation.EntityId;
            // A homing copy keeps its search phase relative to the owner's spawn tick (P3).
            bullet.NextSearchTick += operation.Tick - bullet.SpawnTick;
            bullet.SpawnTick = operation.Tick;
            if (!bullet.IsBeam)
            {
                // The owner's identity draws the speed and lifetime of a shot with a random factor (step 4a).
                _world.DrawShot(operation.EntityId, operation.Tick, bullet.Definition, cannon?.Definition.RandomFactor ?? 0f,
                    out var speedScale, out var lifetimeTicks);
                bullet.SpeedScale = speedScale;
                bullet.ExpiryTick = operation.Tick + lifetimeTicks - 1;
            }
            else if (bullet.BeamDefinition.IsShot)
                bullet.ExpiryTick = operation.Tick + bullet.BeamDefinition.LifetimeTicks - 1;
            bullet.Confirmed = true;
            _world.Events.Add(BattleEvent.BulletRename(BattleId, Tick, _world.Events.Count, bullet, previous));
            if (cannon != null && operation.HasLaunchPose) Reaim(ship, cannon, index, operation);
        }

        // A bullet that spawned and died between two deliveries: its owner removal is in the same batch.
        internal bool HasDueRemoval(EntityId id)
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
        internal void RemoveUnconfirmedBullets()
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

            for (var i = 0; i < _world.Bullets.Capacity; ++i)
            {
                if (!_world.Bullets.IsAlive(i)) continue;
                ref readonly var bullet = ref _world.Bullets[i];
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
            ref var bullet = ref _world.Bullets[index];
            bullet.PendingRemoval = reason;
            if (bullet.IsBeam) DetachBeam(bullet, _world.Bullets.GetHandle(index));
            _world.Events.Add(BattleEvent.BulletRemoval(BattleId, Tick, _world.Events.Count, bullet, BattleEventAuthority.Predicted));
            _battle.ReleaseBullet(index);
        }

        private void DetachBeam(in BulletEntity beam, EntityHandle handle) => DetachBeam(beam, handle, Tick);

        private void DetachBeam(in BulletEntity beam, EntityHandle handle, long stopTick)
        {
            var ship = _world.FindShip(beam.ParentId);
            if (ship != null && beam.DeviceIndex < ship.DeviceCount && ship.GetDevice(beam.DeviceIndex) is BeamDevice device)
                device.Detach(handle, stopTick);
        }

        // A remote bullet that this device removed before its owner confirmed it (N2, expiry, beam stop).
        internal void RecordPredictedRemoval(in BulletEntity bullet)
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
