using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    internal sealed partial class BattleNetwork
    {
        internal bool Submit(int senderOwner, in OwnerOperation operation)
        {
            if (operation.BattleId != BattleId || operation.Owner != senderOwner || !IsRemoteOwner(senderOwner) ||
                operation.Sequence == 0 || operation.Tick < 0 || operation.Tick == long.MaxValue ||
                !ValidateOperation(operation)) return false;
            if (!AcceptSequence(_operationSequences, senderOwner, operation.Sequence)) return false;
            var index = _remoteOperations.Count;
            while (index > 0 && CompareOperations(operation, _remoteOperations[index - 1]) < 0) --index;
            _remoteOperations.Insert(index, operation);
            return true;
        }

        internal bool Submit(in InboundHit record)
        {
            var hit = record.Hit;
            if (hit.BattleId != BattleId || !IsRemoteOwner(record.SenderOwner) ||
                hit.AttackerOwner != record.SenderOwner || !Setup.HasOwner(hit.TargetOwner) ||
                !Setup.IsLocalOwner(hit.TargetOwner) || record.Sequence == 0 || hit.AttackerId.IsNull ||
                hit.TargetId.IsNull || hit.Tick < 0 || hit.Tick > long.MaxValue - 6 ||
                hit.DeviceIndex < -1 || hit.TargetShape < 0 || !Finite(hit.Amount) || hit.Amount < 0f ||
                hit.ContactState < HitContactState.Start || hit.ContactState > HitContactState.End ||
                hit.ImpactKind < HitImpactKind.Impact || hit.ImpactKind > HitImpactKind.Stop ||
                !Setup.DamageTypes.Contains(hit.DamageType) || hit.Effect > HitEffect.ShieldDrain)
                return false;
            // A drain names a ship target, without damage type or push (phase 5, step 5a).
            if (hit.Effect != HitEffect.Damage && (hit.TargetIsBullet || hit.DamageType != 0 ||
                hit.Impulse.X != 0f || hit.Impulse.Y != 0f)) return false;
            // A bullet target: one record per contact. Disarm and Stop name bullets only.
            if (hit.TargetIsBullet ? hit.TargetShape != 0 || hit.ContactState != HitContactState.Start
                : hit.ImpactKind > HitImpactKind.DamageOverTime) return false;
            // A push travels only with the impact of a projectile on a ship (phase 4 E4).
            if (!Finite(hit.Impulse.X) || !Finite(hit.Impulse.Y) ||
                ((hit.Impulse.X != 0f || hit.Impulse.Y != 0f) && (hit.TargetIsBullet ||
                    hit.ImpactKind == HitImpactKind.DamageOverTime || hit.ContactState != HitContactState.Start)))
                return false;
            // The attacker may already be gone. A live copy, when present, must agree with the sender.
            if (_world.Entities.TryGet(hit.AttackerId, out var handle) && _world.EntityOwner(handle) != record.SenderOwner)
                return false;
            if (_world.Entities.TryGet(hit.TargetId, out handle) &&
                (IsContactTarget(handle.Kind) != hit.TargetIsBullet || _world.EntityOwner(handle) != hit.TargetOwner)) return false;
            if (!AcceptSequence(_hitSequences, record.SenderOwner, record.Sequence)) return false;
            var index = _remoteHits.Count;
            while (index > 0 && CompareHits(record, _remoteHits[index - 1]) < 0) --index;
            _remoteHits.Insert(index, record);
            return true;
        }

        internal List<OwnerOperation> OutgoingOperations => _outgoingOperations;
        internal List<OwnerOperation> AppliedOperations => _appliedOperations;

        // Runtime checks the peer binding before submitting controls, including a spawn due in stage 1.
        // A received removal or roster exit (stage 12) from an earlier tick is overdue: it applies in stage 1, before
        // controls, so the entity takes no more input.
        internal bool AcceptsInputFrom(EntityId entity, int owner)
        {
            var ship = _world.FindShip(entity);
            if (ship != null)
            {
                if (ship.Owner != owner) return false;
                foreach (var operation in _remoteOperations)
                    if (operation.EntityId == entity && operation.Tick < Tick && OperationStage(operation) == 12) return false;
                return true;
            }
            foreach (var operation in _remoteOperations)
                if (operation.EntityId == entity && operation.Owner == owner && operation.Tick <= Tick &&
                    operation.Kind == OwnerOperationKind.Roster && operation.Roster.Reason == RosterTransitionReason.Deployed)
                    return true;
            return false;
        }

        private bool IsRemoteOwner(int owner) => Setup.HasOwner(owner) && !Setup.IsLocalOwner(owner);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidPose(in SpawnPose pose) => Finite(pose.Position.X) && Finite(pose.Position.Y) &&
            Finite(pose.Angle) && Finite(pose.LinearVelocity.X) && Finite(pose.LinearVelocity.Y) && Finite(pose.AngularVelocity);

        // A hit record on a bullet or a tail segment names one contact (ResolvedHit.TargetIsBullet; network plan 9d).
        private static bool IsContactTarget(EntityKind kind) => kind == EntityKind.Bullet || kind == EntityKind.Segment;

        private DroneBayInventory FindBay(DroneBayKey key)
        {
            var slot = Roster.Find(key.RosterId);
            return slot != null && key.DeviceIndex >= 0 && key.DeviceIndex < slot.Setup.Ship.Devices.Count
                ? slot.GetBay(key.DeviceIndex) : null;
        }

        private bool ValidateOperation(in OwnerOperation operation)
        {
            switch (operation.Kind)
            {
                case OwnerOperationKind.Roster:
                    var record = operation.Roster;
                    var slot = Roster.Find(record.RosterId);
                    if (slot == null || slot.Owner != operation.Owner || record.Revision < 1 ||
                        !Finite(record.ArmorDeficit) || record.ArmorDeficit < 0f || record.ArmorDeficit > slot.Setup.Ship.Armor ||
                        !Finite(record.ShieldDeficit) || record.ShieldDeficit < 0f || record.ShieldDeficit > slot.Setup.Ship.Shield.Max ||
                        !Finite(record.DamageFraction) || record.DamageFraction < 0f || record.DamageFraction > 1f ||
                        record.DeploymentOrder < 0) return false;
                    switch (record.Reason)
                    {
                        case RosterTransitionReason.Deployed:
                            return record.PreviousStatus == RosterStatus.Ready && record.Status == RosterStatus.Active &&
                                record.DeploymentCount > 0 && record.DeploymentOrder > 0 && ValidPose(operation.Pose) &&
                                operation.EntityId == SpawnIdentity.CreateRoot(BattleId, record.RosterId, record.DeploymentCount, operation.Tick);
                        case RosterTransitionReason.Retreated:
                            return record.PreviousStatus == RosterStatus.Active && record.Status == RosterStatus.Ready && !operation.EntityId.IsNull;
                        case RosterTransitionReason.Killed:
                            return record.PreviousStatus == RosterStatus.Active && record.Status == RosterStatus.Destroyed && !operation.EntityId.IsNull;
                        case RosterTransitionReason.ForcedDestruction:
                            return (record.PreviousStatus == RosterStatus.Ready || record.PreviousStatus == RosterStatus.Active) &&
                                record.Status == RosterStatus.Destroyed && (record.PreviousStatus == RosterStatus.Ready) == operation.EntityId.IsNull;
                        default: return false;
                    }
                case OwnerOperationKind.DroneSpawn:
                    var bay = operation.BayKey.HasValue ? FindBay(operation.BayKey.Value) : null;
                    return bay != null && bay.Slot.Owner == operation.Owner && !operation.EntityId.IsNull &&
                        !operation.ParentId.IsNull && ValidPose(operation.Pose);
                case OwnerOperationKind.Removal:
                    if (operation.EntityId.IsNull || !Battle.IsSentRemovalReason(operation.RemovalReason)) return false;
                    if (operation.BayKey.HasValue)
                    {
                        var removalBay = FindBay(operation.BayKey.Value);
                        if (removalBay == null || removalBay.Slot.Owner != operation.Owner) return false;
                    }
                    return !_world.Entities.TryGet(operation.EntityId, out var handle) ||
                        (_world.EntityOwner(handle) == operation.Owner &&
                         (IsContactTarget(handle.Kind) ? !operation.BayKey.HasValue :
                          _world.Ships.Get(handle)?.Bay?.Key == operation.BayKey && operation.BayKey.HasValue));
                // The missile or its target may be gone; a live missile must be the owner's homing or sticky projectile.
                // A tail segment target is a sticky bullet's (phase 5, step 5a).
                case OwnerOperationKind.MissileTarget:
                    if (operation.EntityId.IsNull || operation.TargetIsSegment && operation.TargetId.IsNull) return false;
                    if (_world.Entities.TryGet(operation.TargetId, out handle) && (handle.Kind == EntityKind.Bullet ||
                        (handle.Kind == EntityKind.Segment) != operation.TargetIsSegment)) return false;
                    return !_world.Entities.TryGet(operation.EntityId, out handle) ||
                        (_world.Bullets.TryGet(handle, out var missile) && _world.Bullets[missile].Owner == operation.Owner &&
                         !_world.Bullets[missile].IsBeam && (_world.Bullets[missile].Definition.IsSticky ||
                         _world.Bullets[missile].Definition.IsHoming && !operation.TargetIsSegment));
                // The ship may be gone; a live ship must be the owner's.
                case OwnerOperationKind.Allegiance:
                    return !operation.EntityId.IsNull && Allegiances.IsValid(operation.Allegiance) &&
                        (!_world.Entities.TryGet(operation.EntityId, out handle) ||
                         (handle.Kind == EntityKind.Ship && _world.EntityOwner(handle) == operation.Owner));
                case OwnerOperationKind.Retreat:
                    return !operation.EntityId.IsNull && (!_world.Entities.TryGet(operation.EntityId, out handle) ||
                        (handle.Kind == EntityKind.Ship && _world.EntityOwner(handle) == operation.Owner && _world.Ships.Get(handle)?.Slot != null));
                // The ship may be gone; a live ship must be the owner's and have the device.
                case OwnerOperationKind.DeviceUse:
                    return !operation.EntityId.IsNull && operation.DeviceIndex >= 0 &&
                        (!_world.Entities.TryGet(operation.EntityId, out handle) ||
                         (handle.Kind == EntityKind.Ship && _world.EntityOwner(handle) == operation.Owner &&
                          operation.DeviceIndex < _world.Ships.Get(handle).DeviceCount));
                // The parent may not exist yet; it is checked when the record applies.
                case OwnerOperationKind.BulletSpawn:
                    return !operation.ParentId.IsNull && operation.DeviceIndex >= 0 && operation.BarrelIndex >= 0 &&
                        operation.EntityId == SpawnIdentity.CreateChild(operation.ParentId, operation.Tick,
                            (uint)operation.DeviceIndex, (uint)operation.BarrelIndex, operation.SpawnSequence);
                default: return false;
            }
        }

        // On-time operations retain their spawn/structure stage. Late operations apply before commands,
        // including complete chains of roster revisions; past ticks are never replayed.
        internal void ApplyRemoteOperations(int stage = 1)
        {
            if (_remoteOperations.Count == 0) return;
            _applyingRemoteOperation = true;
            try
            {
                bool progressed;
                do
                {
                    progressed = false;
                    for (var i = 0; i < _remoteOperations.Count;)
                    {
                        var operation = _remoteOperations[i];
                        if (operation.Tick > Tick || (operation.Tick == Tick && OperationStage(operation) > stage))
                        { ++i; continue; }
                        if (operation.Kind == OwnerOperationKind.Roster)
                        {
                            var slot = Roster.Find(operation.Roster.RosterId);
                            if (operation.Roster.Revision > slot.Revision + 1) { ++i; continue; }
                            if (operation.Roster.Revision == slot.Revision + 1 && ApplyRemoteRoster(operation, slot))
                                _appliedOperations.Add(operation);
                        }
                        else if (operation.Kind == OwnerOperationKind.DroneSpawn)
                        {
                            if (ApplyRemoteDrone(operation)) _appliedOperations.Add(operation);
                        }
                        else if (operation.Kind == OwnerOperationKind.BulletSpawn)
                        {
                            if (ApplyRemoteBulletSpawn(operation)) _appliedOperations.Add(operation);
                        }
                        else if (operation.Kind == OwnerOperationKind.Retreat)
                        {
                            if (ApplyRemoteRetreat(operation)) _appliedOperations.Add(operation);
                        }
                        else if (operation.Kind == OwnerOperationKind.MissileTarget)
                        {
                            ApplyRemoteMissileTarget(operation);
                            _appliedOperations.Add(operation);
                        }
                        else if (operation.Kind == OwnerOperationKind.Allegiance)
                        {
                            ApplyRemoteAllegiance(operation);
                            _appliedOperations.Add(operation);
                        }
                        else if (operation.Kind == OwnerOperationKind.DeviceUse)
                        {
                            ApplyRemoteDeviceUse(operation);
                            _appliedOperations.Add(operation);
                        }
                        else if (ApplyRemoteRemoval(operation)) _appliedOperations.Add(operation);
                        _remoteOperations.RemoveAt(i);
                        progressed = true;
                    }
                } while (progressed && _remoteOperations.Count > 0);
            }
            finally { _applyingRemoteOperation = false; }
        }

        private bool ApplyRemoteRoster(in OwnerOperation operation, RosterSlot slot)
        {
            var record = operation.Roster;
            if (slot.Status != record.PreviousStatus || slot.Status == RosterStatus.Destroyed) return false;
            ShipEntity ship = null;
            if (record.Reason == RosterTransitionReason.Deployed)
            {
                if (slot.DeploymentCount == uint.MaxValue || record.DeploymentCount != slot.DeploymentCount + 1 ||
                    _world.Entities.TryGet(operation.EntityId, out _)) return false;
                ship = _world.Ships.Allocate();
                if (!_world.TryRegister(operation.EntityId, ship.Handle)) { _world.Ships.Release(ship); return false; }
                var body = Physics.CreateBody(new BodyDef { Mass = slot.Setup.Ship.Mass });
                // Initialize with the owner's retained resources, not values inferred from a prediction.
                slot.Import(record, null);
                ship.Initialize(operation.EntityId, slot, body, operation.Tick, Setup.DamageTypes.Count);
                _world.AddShipShapes(ship, CollisionFilters.Ship(ship.Allegiance));
                SetSpawnPose(body, operation.Pose);
                _world.Events.Add(BattleEvent.Spawn(BattleId, Tick, _world.Events.Count, ship));
            }
            else
            {
                if (record.DeploymentCount != slot.DeploymentCount || record.DeploymentOrder != slot.DeploymentOrder ||
                    (slot.ActiveShip?.Id ?? default) != operation.EntityId) return false;
                if (slot.ActiveShip != null)
                {
                    _battle.RemoveShip(slot.ActiveShip, record.Reason == RosterTransitionReason.Retreated ? RemovalReason.Retreat : RemovalReason.Death);
                }
            }
            slot.Import(record, ship);
            _world.DeploymentOrder = Math.Max(_world.DeploymentOrder, record.DeploymentOrder);
            _battle.Flow.Add(operation.Tick, slot.Side, record.PreviousStatus, record.Status);
            _battle.AddTransition(slot, record.PreviousStatus, record.Reason, operation.EntityId);
            return true;
        }

        private bool ApplyRemoteDrone(in OwnerOperation operation)
        {
            if (_world.Entities.TryGet(operation.EntityId, out _) || _removedRemoteEntities.Contains(operation.EntityId)) return false;
            var bay = FindBay(operation.BayKey.Value);
            if (bay.InHangar <= 0 || operation.EntityId != SpawnIdentity.CreateChild(operation.ParentId,
                operation.Tick, (uint)bay.Key.DeviceIndex, (uint)bay.Launched, 0)) return false;
            var ship = _world.Ships.Allocate();
            if (!_world.TryRegister(operation.EntityId, ship.Handle)) { _world.Ships.Release(ship); return false; }
            var body = Physics.CreateBody(new BodyDef { Mass = bay.Drone.Mass });
            ship.InitializeDrone(operation.EntityId, bay, body, operation.Tick, Setup.DamageTypes.Count);
            _world.AddShipShapes(ship, CollisionFilters.Drone(ship.Allegiance));
            SetSpawnPose(body, operation.Pose);
            _world.AddDrone(ship);
            bay.RecordLaunch();
            _world.Events.Add(BattleEvent.Spawn(BattleId, Tick, _world.Events.Count, ship));

            // The copy of the parent links its clone (phase 5, step 7c); a parent that already left needs nothing.
            var parent = _world.FindShip(operation.ParentId);
            if (parent != null && parent.Slot == bay.Slot)
                parent.GetDevice(bay.Key.DeviceIndex)
                    .OnShipLaunched(_battle.DeviceContext, parent, ship.Id, operation.Tick);
            return true;
        }

        // The completion tick is absolute. An overdue record turns the copy's engine and devices off from now on;
        // the owner's roster record completes the retreat.
        private bool ApplyRemoteRetreat(in OwnerOperation operation)
        {
            var ship = _world.FindShip(operation.EntityId);
            if (ship == null || ship.Owner != operation.Owner || ship.Slot == null ||
                ship.RetreatCompletionTick != ShipEntity.NoRetreat) return false;
            ship.RetreatCompletionTick = operation.Tick + Battle.RetreatTicks;
            return true;
        }

        // P3: the copy takes the owner's target and search schedule. A copy that is already gone needs nothing; the
        // record still commits. A target that is gone here makes the copy search at once (HomingSteering). Phase 5,
        // step 4b: a sticky copy sticks to the owner's target, unless that target is gone here.
        private void ApplyRemoteMissileTarget(in OwnerOperation operation)
        {
            if (!_world.Entities.TryGet(operation.EntityId, out var handle) || !_world.Bullets.TryGet(handle, out var index)) return;
            ref var bullet = ref _world.Bullets[index];
            if (bullet.Definition.IsSticky)
            {
                if (!bullet.Stuck) StickyMine.Stick(_battle, index, operation.TargetId, operation.Tick);
                return;
            }
            bullet.TargetId = operation.TargetId;
            bullet.NextSearchTick = operation.Tick + HomingSteering.SearchTicks;
        }

        // D13: the copy and its bullets take the owner's allegiance, including copies predicted before the record
        // arrived; later spawns read it from the ship. A ship that is already gone needs nothing; the record still
        // commits. Until the record arrives, the copy collides with its old allegiance.
        private void ApplyRemoteAllegiance(in OwnerOperation operation)
        {
            var ship = _world.FindShip(operation.EntityId);
            if (ship != null && ship.Owner == operation.Owner && ship.Allegiance != operation.Allegiance)
                _world.SetAllegiance(ship, operation.Allegiance);
        }

        // Phase 5, step 7 (H8): the copy's device starts or ends the use at the owner's tick, before this tick's
        // systems. A ship that is already gone needs nothing; the record still commits. Until the record arrives, the
        // copy has the old status.
        private void ApplyRemoteDeviceUse(in OwnerOperation operation)
        {
            var ship = _world.FindShip(operation.EntityId);
            if (ship != null && ship.Owner == operation.Owner && operation.DeviceIndex < ship.DeviceCount)
                ship.GetDevice(operation.DeviceIndex)
                    .ApplyRemoteUse(_battle.DeviceContext, ship, operation.Tick, operation.UseStarted);
        }

        private bool ApplyRemoteRemoval(in OwnerOperation operation)
        {
            if (!_world.Entities.TryGet(operation.EntityId, out var handle))
            {
                // A projectile may already have disappeared through prediction. Its owner removal is still a commit.
                if (operation.BayKey.HasValue) return false;
                _triggers.MarkTriggerParentRemoved(operation.EntityId, operation.Tick);
                return true;
            }
            if (_world.EntityOwner(handle) != operation.Owner) return false;
            if (handle.Kind == EntityKind.Segment)
            {
                if (operation.BayKey.HasValue) return false;
                _tails.RemoveSegment(_world.Segments.Get(handle), operation.RemovalReason);
            }
            else if (handle.Kind == EntityKind.Bullet)
            {
                if (operation.BayKey.HasValue || !_world.Bullets.TryGet(handle, out var index)) return false;
                ref var bullet = ref _world.Bullets[index];
                bullet.PendingRemoval = operation.RemovalReason;
                if (bullet.IsBeam) DetachBeam(bullet, handle, operation.Tick);
                else if (_world.FindShip(bullet.ParentId)?.GetDevice(bullet.DeviceIndex) is CannonDevice cannon)
                    cannon.OnRemoteRemoval(handle, operation.Tick);
                _world.Events.Add(BattleEvent.BulletRemoval(BattleId, Tick, _world.Events.Count, bullet));
                _battle.ReleaseBullet(index);
                _triggers.MarkTriggerParentRemoved(operation.EntityId, operation.Tick);
            }
            else
            {
                var ship = _world.Ships.Get(handle);
                if (ship == null || !ship.IsDrone || ship.Bay.Key != operation.BayKey) return false;
                _removedRemoteEntities.Add(ship.Id);
                _world.Drones.Remove(ship);
                _battle.RemoveShip(ship, operation.RemovalReason);
            }
            return true;
        }

        private void SetSpawnPose(BodyId body, in SpawnPose pose)
        {
            Physics.SetPose(body, pose.Position, pose.Angle);
            Physics.SetLinearVelocity(body, pose.LinearVelocity);
            Physics.SetAngularVelocity(body, pose.AngularVelocity);
        }

        private ulong NextOperationSequence(int owner)
        {
            _nextOperationSequences.TryGetValue(owner, out var sequence);
            _nextOperationSequences[owner] = checked(++sequence);
            return sequence;
        }

        private bool ShouldCapture(int owner) => Setup.HasRemoteOwners && !_applyingRemoteOperation && Setup.IsLocalOwner(owner);

        internal void CaptureRoster(RosterSlot slot, RosterStatus previous, RosterTransitionReason reason, EntityId id)
        {
            if (!ShouldCapture(slot.Owner)) return;
            var state = new RosterSlotState(slot);
            var record = new RosterRecord(slot.RosterId, slot.Revision, previous, slot.Status, reason,
                slot.DeploymentCount, slot.DeploymentOrder, slot.ArmorDeficit, slot.ShieldDeficit, state.DamageFraction);
            _outgoingOperations.Add(OwnerOperation.RosterChange(BattleId, slot.Owner, NextOperationSequence(slot.Owner),
                Tick, id, record, slot.ActiveShip == null ? default : _world.ReadSpawnPose(slot.ActiveShip)));
        }

        // Phase 5, step 7 (H8): a device use that started or ended on a local ship. The tick is the use's first status
        // tick: this tick, or the next one for a use that lethal damage started at stage 10.
        internal void CaptureDeviceUse(ShipEntity ship, int deviceIndex, bool started, long tick)
        {
            if (ShouldCapture(ship.Owner))
                _outgoingOperations.Add(OwnerOperation.DeviceUsed(BattleId, ship.Owner, NextOperationSequence(ship.Owner),
                    tick, ship.Id, deviceIndex, started));
        }

        internal void CaptureDroneSpawn(ShipEntity ship, EntityId parent)
        {
            if (ShouldCapture(ship.Owner))
                _outgoingOperations.Add(OwnerOperation.DroneLaunch(BattleId, ship.Owner, NextOperationSequence(ship.Owner),
                    Tick, ship.Id, ship.Bay.Key, parent, _world.ReadSpawnPose(ship)));
        }

        // P3: an owner search changed a homing projectile's target.
        internal void CaptureMissileTarget(in BulletEntity bullet, EntityId target, bool targetIsSegment = false)
        {
            if (ShouldCapture(bullet.Owner))
                _outgoingOperations.Add(OwnerOperation.MissileTargetChanged(BattleId, bullet.Owner,
                    NextOperationSequence(bullet.Owner), Tick, bullet.Id, target, targetIsSegment));
        }

        internal void CaptureRemoval(EntityId id, int owner, RemovalReason reason, DroneBayKey? bay = null)
        {
            if (ShouldCapture(owner))
                _outgoingOperations.Add(OwnerOperation.Remove(BattleId, owner, NextOperationSequence(owner), Tick, id, reason, bay));
        }

        // A tail segment is a bullet spawn with the device index as source and the segment number as spawn slot (H8).
        internal void CaptureSegmentSpawn(ShipEntity ship, int deviceIndex, int number, EntityId id)
        {
            if (ShouldCapture(ship.Owner))
                _outgoingOperations.Add(OwnerOperation.BulletFired(BattleId, ship.Owner, NextOperationSequence(ship.Owner),
                    Tick, id, ship.Id, deviceIndex, number, 0));
        }

        // Reliable streams normally arrive in order. The sparse set also handles repeated/out-of-order submissions
        // without retaining every sequence for the lifetime of the match.
        private sealed class SequenceWindow
        {
            private ulong _through;
            private readonly HashSet<ulong> _later = new HashSet<ulong>();

            // Every sequence up to this one has been accepted.
            public ulong Through => _through;

            public bool Accept(ulong sequence)
            {
                if (sequence <= _through || !_later.Add(sequence)) return false;
                while (_through < ulong.MaxValue && _later.Remove(_through + 1)) ++_through;
                return true;
            }
        }

        private static bool AcceptSequence(Dictionary<int, SequenceWindow> streams, int owner, ulong sequence)
        {
            if (!streams.TryGetValue(owner, out var window)) streams.Add(owner, window = new SequenceWindow());
            return window.Accept(sequence);
        }

        private static int CompareOperations(in OwnerOperation a, in OwnerOperation b)
        {
            var compare = a.Tick.CompareTo(b.Tick);
            if (compare == 0) compare = a.Owner.CompareTo(b.Owner);
            return compare == 0 ? a.Sequence.CompareTo(b.Sequence) : compare;
        }

        // Stage 6 stands for the end of stage 5: fire confirmations follow the local spawns of their tick. A missile
        // target applies before the steering of its tick, where the owner's search ran.
        private static int OperationStage(in OwnerOperation operation) =>
            operation.Kind == OwnerOperationKind.MissileTarget ? 4 :
            operation.Kind == OwnerOperationKind.DroneSpawn ? 5 :
            operation.Kind == OwnerOperationKind.BulletSpawn ? 6 :
            operation.Kind == OwnerOperationKind.Retreat || operation.Kind == OwnerOperationKind.Allegiance ||
            operation.Kind == OwnerOperationKind.DeviceUse ? 1 :
            operation.Kind == OwnerOperationKind.Roster && operation.Roster.Reason == RosterTransitionReason.Deployed ? 1 : 12;

        private static int CompareHits(in InboundHit a, in InboundHit b)
        {
            var compare = a.Hit.Tick.CompareTo(b.Hit.Tick);
            if (compare == 0) compare = a.SenderOwner.CompareTo(b.SenderOwner);
            return compare == 0 ? a.Sequence.CompareTo(b.Sequence) : compare;
        }

        private readonly List<OwnerOperation> _remoteOperations = new List<OwnerOperation>();
        private readonly List<OwnerOperation> _outgoingOperations = new List<OwnerOperation>();
        private readonly List<OwnerOperation> _appliedOperations = new List<OwnerOperation>();
        private readonly List<InboundHit> _remoteHits = new List<InboundHit>();
        private readonly Dictionary<int, SequenceWindow> _operationSequences = new Dictionary<int, SequenceWindow>();
        private readonly Dictionary<int, SequenceWindow> _hitSequences = new Dictionary<int, SequenceWindow>();
        private readonly Dictionary<int, ulong> _nextOperationSequences = new Dictionary<int, ulong>();
        private readonly HashSet<EntityId> _removedRemoteEntities = new HashSet<EntityId>();
        private bool _applyingRemoteOperation;
    }
}
