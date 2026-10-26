using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;
using CombatSimulation.Physics;

namespace CombatSimulation.Runtime
{
    // Input and Correction are bit-packed datagrams (InputCodec, CorrectionCodec) and Batch is one reliable tick batch;
    // they only tag the receive queue.
    internal enum SessionMessage : byte { Input = 1, Batch = 4, Correction }

    // The names one owner's reliable stream gives its entities (network plan, G2). Every record travels on the ordered,
    // exactly-once stream, so both peers build the same tables: ships and drones by control index (spawn-record order),
    // bullets by bullet index (bullet-spawn-record order). The stream also counts its hits and operation sequences.
    internal sealed class StreamNames
    {
        // The ship definition of a spawn record; both peers derive it from the same setup.
        internal delegate ShipDefinition Definitions(in OwnerOperation spawn);

        internal readonly List<ControlledBody> Bodies = new List<ControlledBody>();
        internal ulong Bullets { get; private set; }
        internal ulong Hits;
        internal ulong LastOperation;

        internal StreamNames(Definitions definitions) => _definitions = definitions;

        // Ships and drones get control indices in spawn-record order.
        internal static bool Spawns(in OwnerOperation operation) => operation.Kind == OwnerOperationKind.DroneSpawn ||
            (operation.Kind == OwnerOperationKind.Roster && operation.Roster.Reason == RosterTransitionReason.Deployed);

        internal bool TryGetBody(EntityId entity, out uint index) => _bodyIndices.TryGetValue(entity, out index);

        // A bullet is named by its age: the latest bullet index minus its own.
        internal bool TryGetBullet(EntityId entity, out ulong age)
        {
            age = 0;
            if (!_bulletIndices.TryGetValue(entity, out var index)) return false;
            age = Bullets - 1 - index;
            return true;
        }

        internal bool TryGetBullet(ulong age, out EntityId entity)
        {
            entity = default;
            return age < Bullets && _bulletEntities.TryGetValue(Bullets - 1 - age, out entity);
        }

        // Corrections travel unreliably, so the two peers can count different latest indices: they name a bullet by
        // its absolute index (P3).
        internal bool TryGetBulletIndex(EntityId entity, out ulong index) => _bulletIndices.TryGetValue(entity, out index);
        internal bool TryGetBulletAt(ulong index, out EntityId entity) => _bulletEntities.TryGetValue(index, out entity);

        internal void AddBody(in OwnerOperation spawn)
        {
            _bodyIndices[spawn.EntityId] = (uint)Bodies.Count;
            Bodies.Add(new ControlledBody(spawn.EntityId, _definitions(spawn),
                spawn.Kind == OwnerOperationKind.DroneSpawn ? spawn.BayKey : null));
        }

        internal void AddBullet(EntityId entity)
        {
            _bulletIndices[entity] = Bullets;
            _bulletEntities.Add(Bullets, entity);
            ++Bullets;
        }

        // A removed bullet keeps its index until the end of the batch: a hit of the same tick can still name it.
        internal void RemoveBullet(EntityId entity) => _removedBullets.Add(entity);

        internal void EndBatch()
        {
            foreach (var entity in _removedBullets)
                if (_bulletIndices.TryGetValue(entity, out var index))
                {
                    _bulletIndices.Remove(entity);
                    _bulletEntities.Remove(index);
                }
            _removedBullets.Clear();
        }

        private readonly Definitions _definitions;
        private readonly Dictionary<EntityId, uint> _bodyIndices = new Dictionary<EntityId, uint>();
        private readonly Dictionary<EntityId, ulong> _bulletIndices = new Dictionary<EntityId, ulong>();
        private readonly Dictionary<ulong, EntityId> _bulletEntities = new Dictionary<ulong, EntityId>();
        private readonly List<EntityId> _removedBullets = new List<EntityId>();
    }

    // Reliable tick batch, a bit stream (BitWriter), network plan G2:
    //   version (8 bits), batch tick and last operation sequence (both VarUInt), then records until only zero padding
    //   remains. The connection is bound to one battle, so no battle ID travels. The sender owns every record.
    // Record: kind (3 bits, RecordKind), record tick − batch tick (zigzag VarUInt), then by kind:
    //   Hit: attacker kind (1 bit: control index, bullet age), attacker (VarUInt), target kind (1 bit: control index,
    //     bullet), target: control index in the receiver's stream (VarUInt) or, for a bullet, its entity ID (64; the
    //     sender may know only a predicted copy, which no stream names), target shape (VarUInt), device index + 1
    //     (VarUInt), contact (2), impact (3), amount (32-bit float). The sequence is the hit's position in the stream.
    //   Roster: entity (64), roster ID (32), revision (VarUInt), previous status (2), status (2), reason (3),
    //     deployment count and order (VarUInt), armor deficit, shield deficit, damage fraction (32-bit floats), pose.
    //   Drone launch: entity (64), bay roster ID (32), bay device (VarUInt), parent entity (64), pose.
    //   Removal: entity kind (1 bit), reason (3), control index or bullet age (VarUInt). A drone's bay is known from
    //     its launch.
    //   Bullet spawn: parent kind (1 bit), parent control index or bullet age (VarUInt), device, barrel and spawn
    //     sequence (VarUInt), launch flag (1 bit); with the flag, the muzzle position per axis on the correction grid
    //     (CorrectionCodec.Arena) and the launch angle (14 bits), network plan P1. The receiver derives the entity ID
    //     with SpawnIdentity.CreateChild.
    //   Retreat: ship control index (VarUInt).
    //   Missile target (P3): missile bullet age (VarUInt), target control index + 1 in the receiver's stream (VarUInt;
    //     0: no target).
    //   Pose: position, angle, linear velocity, angular velocity (six 32-bit floats).
    // Operation sequences are consecutive inside a batch and end at the header's value. Operations come before hits,
    // so a hit names only bullets already counted.
    internal static class SessionCodec
    {
        internal enum RecordKind : byte { Hit = 1, Roster, DroneLaunch, Removal, BulletSpawn, Retreat, MissileTarget }

        internal const byte Version = 5;
        internal const int MaxBatchBytes = 1 << 20;
        private const int KindBits = 3, StatusBits = 2, ReasonBits = 3, ContactBits = 2, ImpactBits = 3;
        private const int MaxVarUIntBits = 16 + 1 + 64;
        // Upper bounds for buffer sizing. The largest record, a roster change, needs about 720 bits.
        private const int HeaderBits = 8 + 2 * MaxVarUIntBits, RecordBits = 1024;

        internal static int Capacity(int records) => (HeaderBits + records * RecordBits + 7) / 8;

        // Writes the tick batch of the local owner (own) and advances its names. Hit targets are named in the peer's
        // stream. A record that names an entity the streams do not know, or a sequence gap, is a session bug and throws.
        internal static int WriteBatch(Span<byte> buffer, long tick, ReadOnlySpan<OwnerOperation> operations,
            ReadOnlySpan<ResolvedHit> hits, StreamNames own, StreamNames peer, in CorrectionCodec.Arena arena)
        {
            if (buffer.Length < Capacity(operations.Length + hits.Length))
                throw new ArgumentException("Batch buffer is too small.", nameof(buffer));
            var w = new BitWriter(buffer);
            var lastOperation = own.LastOperation + (ulong)operations.Length;
            w.WriteBits(Version, 8); w.WriteVarUInt((ulong)tick); w.WriteVarUInt(lastOperation);
            foreach (var operation in operations) WriteOperation(ref w, tick, operation, own, peer, arena);
            foreach (var hit in hits) WriteHit(ref w, tick, hit, own, peer);
            own.EndBatch();
            return w.Finish();
        }

        private static void WriteOperation(ref BitWriter w, long batchTick, in OwnerOperation op, StreamNames own,
            StreamNames peer, in CorrectionCodec.Arena arena)
        {
            if (op.Sequence != own.LastOperation + 1) throw new InvalidOperationException("Operation sequences must be consecutive.");
            own.LastOperation = op.Sequence;
            switch (op.Kind)
            {
                case OwnerOperationKind.Roster:
                    Header(ref w, RecordKind.Roster, batchTick, op.Tick);
                    var roster = op.Roster;
                    w.WriteBits(op.EntityId.Value, 64); w.WriteBits(roster.RosterId.Value, 32);
                    w.WriteVarUInt((ulong)roster.Revision);
                    w.WriteBits((ulong)roster.PreviousStatus, StatusBits); w.WriteBits((ulong)roster.Status, StatusBits);
                    w.WriteBits((ulong)roster.Reason, ReasonBits);
                    w.WriteVarUInt(roster.DeploymentCount); w.WriteVarUInt((ulong)roster.DeploymentOrder);
                    Float(ref w, roster.ArmorDeficit); Float(ref w, roster.ShieldDeficit); Float(ref w, roster.DamageFraction);
                    Pose(ref w, op.Pose);
                    if (StreamNames.Spawns(op)) own.AddBody(op);
                    break;
                case OwnerOperationKind.DroneSpawn:
                    Header(ref w, RecordKind.DroneLaunch, batchTick, op.Tick);
                    var bay = op.BayKey.Value;
                    w.WriteBits(op.EntityId.Value, 64); w.WriteBits(bay.RosterId.Value, 32); w.WriteVarUInt((ulong)bay.DeviceIndex);
                    w.WriteBits(op.ParentId.Value, 64); Pose(ref w, op.Pose);
                    own.AddBody(op);
                    break;
                case OwnerOperationKind.Removal:
                    var bullet = own.TryGetBullet(op.EntityId, out var name);
                    if (!bullet && own.TryGetBody(op.EntityId, out var index) && own.Bodies[(int)index].Bay is DroneBayKey known &&
                        op.BayKey == known) name = index;
                    else if (!bullet || op.BayKey.HasValue)
                        throw new InvalidOperationException("A removal names an entity the stream does not know.");
                    Header(ref w, RecordKind.Removal, batchTick, op.Tick);
                    w.WriteBool(bullet); w.WriteBits((ulong)op.RemovalReason, ReasonBits); w.WriteVarUInt(name);
                    if (bullet) own.RemoveBullet(op.EntityId);
                    break;
                case OwnerOperationKind.BulletSpawn:
                    var parentBullet = !own.TryGetBody(op.ParentId, out var parentIndex);
                    var parent = (ulong)parentIndex;
                    if (parentBullet && !own.TryGetBullet(op.ParentId, out parent))
                        throw new InvalidOperationException("A bullet spawn names a parent the stream does not know.");
                    if (op.EntityId != SpawnIdentity.CreateChild(op.ParentId, op.Tick, (uint)op.DeviceIndex,
                        (uint)op.BarrelIndex, op.SpawnSequence))
                        throw new InvalidOperationException("A bullet spawn's entity ID is not derived from its record.");
                    Header(ref w, RecordKind.BulletSpawn, batchTick, op.Tick);
                    w.WriteBool(parentBullet); w.WriteVarUInt(parent);
                    w.WriteVarUInt((ulong)op.DeviceIndex); w.WriteVarUInt((ulong)op.BarrelIndex); w.WriteVarUInt(op.SpawnSequence);
                    w.WriteBool(op.HasLaunchPose);
                    if (op.HasLaunchPose)
                    {
                        w.WriteBits((ulong)CorrectionCodec.Position(op.Pose.Position.X, arena.XCells), arena.XBits);
                        w.WriteBits((ulong)CorrectionCodec.Position(op.Pose.Position.Y, arena.YCells), arena.YBits);
                        w.WriteBits((ulong)CorrectionCodec.Angle(op.Pose.Angle), CorrectionCodec.AngleBits);
                    }
                    own.AddBullet(op.EntityId);
                    break;
                case OwnerOperationKind.Retreat:
                    if (!own.TryGetBody(op.EntityId, out var ship))
                        throw new InvalidOperationException("A retreat names a ship the stream does not know.");
                    Header(ref w, RecordKind.Retreat, batchTick, op.Tick);
                    w.WriteVarUInt(ship);
                    break;
                case OwnerOperationKind.MissileTarget:
                    if (!own.TryGetBullet(op.EntityId, out var missile))
                        throw new InvalidOperationException("A missile target names a missile the stream does not know.");
                    var target = 0UL;
                    if (!op.TargetId.IsNull)
                    {
                        if (!peer.TryGetBody(op.TargetId, out var targetIndex))
                            throw new InvalidOperationException("A missile target names a ship the peer's stream does not know.");
                        target = (ulong)targetIndex + 1;
                    }
                    Header(ref w, RecordKind.MissileTarget, batchTick, op.Tick);
                    w.WriteVarUInt(missile); w.WriteVarUInt(target);
                    break;
                default: throw new InvalidOperationException("Unknown owner operation.");
            }
        }

        private static void WriteHit(ref BitWriter w, long batchTick, in ResolvedHit hit, StreamNames own, StreamNames peer)
        {
            if (hit.Sequence != own.Hits + 1) throw new InvalidOperationException("Hit sequences must be consecutive.");
            var bullet = own.TryGetBullet(hit.AttackerId, out var attacker);
            if (!bullet && own.TryGetBody(hit.AttackerId, out var index)) attacker = index;
            else if (!bullet) throw new InvalidOperationException("A hit names an attacker the stream does not know.");
            var target = 0U;
            if (!hit.TargetIsBullet && !peer.TryGetBody(hit.TargetId, out target))
                throw new InvalidOperationException("A hit names a target the peer's stream does not know.");
            Header(ref w, RecordKind.Hit, batchTick, hit.Tick);
            w.WriteBool(bullet); w.WriteVarUInt(attacker); w.WriteBool(hit.TargetIsBullet);
            if (hit.TargetIsBullet) w.WriteBits(hit.TargetId.Value, 64);
            else w.WriteVarUInt(target);
            w.WriteVarUInt((ulong)hit.TargetShape); w.WriteVarUInt((ulong)(hit.DeviceIndex + 1));
            w.WriteBits((ulong)hit.ContactState, ContactBits); w.WriteBits((ulong)hit.ImpactKind, ImpactBits);
            Float(ref w, hit.Amount);
            own.Hits = hit.Sequence;
        }

        private static void Header(ref BitWriter w, RecordKind kind, long batchTick, long tick)
        {
            var offset = unchecked(tick - batchTick);
            w.WriteBits((ulong)kind, KindBits); w.WriteVarUInt(unchecked((ulong)((offset << 1) ^ (offset >> 63))));
        }

        private static void Float(ref BitWriter w, float value) => w.WriteBits(unchecked((uint)BitConverter.SingleToInt32Bits(value)), 32);

        private static void Pose(ref BitWriter w, in SpawnPose pose)
        {
            Float(ref w, pose.Position.X); Float(ref w, pose.Position.Y); Float(ref w, pose.Angle);
            Float(ref w, pose.LinearVelocity.X); Float(ref w, pose.LinearVelocity.Y); Float(ref w, pose.AngularVelocity);
        }

        // Reads one tick batch of the peer's stream (peer) into operations and hits, and returns its watermark. Hit
        // targets are resolved in this device's stream (own). A malformed batch throws after the peer's names may have
        // changed, so the caller must drop it and every later batch. With null names the batch is only scanned: the
        // records are checked and listed in scan, nothing is resolved or returned.
        internal static OwnerWatermark ReadBatch(ReadOnlySpan<byte> data, BattleId battle, int sender, int receiver,
            in CorrectionCodec.Arena arena, StreamNames peer, StreamNames own, List<OwnerOperation> operations, List<ResolvedHit> hits,
            List<ScannedRecord> scan = null)
        {
            operations?.Clear(); hits?.Clear();
            var r = Reader.Batch(data);
            var resolve = peer != null;
            var start = r.BitPosition;
            while (r.NextRecord())
            {
                var reason = RemovalReason.None;
                switch (r.Kind)
                {
                    case RecordKind.Hit:
                        var attackerBullet = r.Bool(); var attackerName = r.VarUInt(); var targetBullet = r.Bool();
                        var targetName = targetBullet ? r.Bits(64) : r.VarUInt();
                        var shape = r.Int(); var device = r.Int() - 1;
                        var contact = r.Bits(ContactBits); var impact = r.Bits(ImpactBits); var amount = r.Float();
                        Require(contact <= (ulong)HitContactState.End && impact <= (ulong)HitImpactKind.Stop && amount >= 0 &&
                            (targetBullet ? targetName != 0 : impact <= (ulong)HitImpactKind.DamageOverTime));
                        if (!resolve) break;
                        var attacker = attackerBullet ? Bullet(peer, attackerName) : Body(peer, attackerName);
                        hits.Add(new ResolvedHit(battle, r.Tick, attacker, sender, device,
                            targetBullet ? new EntityId(targetName) : Body(own, targetName), receiver, shape,
                            (HitContactState)contact, (HitImpactKind)impact, amount, ++peer.Hits, targetBullet));
                        break;
                    case RecordKind.Roster:
                        var entity = r.Entity(); var id = new RosterId((uint)r.Bits(32)); var revision = r.Int();
                        var before = r.Bits(StatusBits); var after = r.Bits(StatusBits); var transition = r.Bits(ReasonBits);
                        var count = r.VarUInt(); var order = r.VarUInt();
                        var armor = r.Float(); var shield = r.Float(); var damage = r.Float(); var pose = r.Pose();
                        Require(revision > 0 && before <= (ulong)RosterStatus.Destroyed && after <= (ulong)RosterStatus.Destroyed &&
                            transition <= (ulong)RosterTransitionReason.ForcedDestruction && count <= uint.MaxValue &&
                            order <= long.MaxValue && armor >= 0 && shield >= 0 && damage >= 0 && damage <= 1);
                        if (!resolve) break;
                        var record = new RosterRecord(id, revision, (RosterStatus)before, (RosterStatus)after,
                            (RosterTransitionReason)transition, (uint)count, (long)order, armor, shield, damage);
                        var roster = OwnerOperation.RosterChange(battle, sender, ++peer.LastOperation, r.Tick, entity, record, pose);
                        if (StreamNames.Spawns(roster)) peer.AddBody(roster);
                        operations.Add(roster);
                        break;
                    case RecordKind.DroneLaunch:
                        entity = r.Entity(); var bay = new DroneBayKey(new RosterId((uint)r.Bits(32)), r.Int());
                        var parent = r.Entity(); pose = r.Pose();
                        Require(!entity.IsNull && !parent.IsNull);
                        if (!resolve) break;
                        var launch = OwnerOperation.DroneLaunch(battle, sender, ++peer.LastOperation, r.Tick, entity, bay, parent, pose);
                        peer.AddBody(launch);
                        operations.Add(launch);
                        break;
                    case RecordKind.Removal:
                        var bullet = r.Bool(); reason = (RemovalReason)r.Bits(ReasonBits); var name = r.VarUInt();
                        Require(reason >= RemovalReason.Death && reason <= RemovalReason.Disposal);
                        if (!resolve) break;
                        entity = bullet ? Bullet(peer, name) : Body(peer, name);
                        var removalBay = bullet ? null : peer.Bodies[(int)name].Bay;
                        Require(bullet || removalBay.HasValue);
                        if (bullet) peer.RemoveBullet(entity);
                        operations.Add(OwnerOperation.Remove(battle, sender, ++peer.LastOperation, r.Tick, entity, reason, removalBay));
                        break;
                    case RecordKind.BulletSpawn:
                        var parentBullet = r.Bool(); var parentName = r.VarUInt();
                        device = r.Int(); var barrel = r.Int(); var spawn = r.VarUInt();
                        var launched = r.Bool();
                        ulong muzzleX = 0, muzzleY = 0, angle = 0;
                        if (launched)
                        {
                            muzzleX = r.Bits(arena.XBits); muzzleY = r.Bits(arena.YBits); angle = r.Bits(CorrectionCodec.AngleBits);
                            Require((long)muzzleX < arena.XCells && (long)muzzleY < arena.YCells);
                        }
                        Require(spawn <= uint.MaxValue);
                        if (!resolve) break;
                        parent = parentBullet ? Bullet(peer, parentName) : Body(peer, parentName);
                        entity = SpawnIdentity.CreateChild(parent, r.Tick, (uint)device, (uint)barrel, (uint)spawn);
                        peer.AddBullet(entity);
                        operations.Add(launched
                            ? OwnerOperation.BulletFired(battle, sender, ++peer.LastOperation, r.Tick, entity, parent,
                                device, barrel, (uint)spawn,
                                new Vec2((float)(muzzleX / CorrectionCodec.PositionScale), (float)(muzzleY / CorrectionCodec.PositionScale)),
                                (float)(angle * (2 * Math.PI / (1 << CorrectionCodec.AngleBits))))
                            : OwnerOperation.BulletFired(battle, sender, ++peer.LastOperation, r.Tick, entity, parent,
                                device, barrel, (uint)spawn));
                        break;
                    case RecordKind.Retreat:
                        name = r.VarUInt();
                        if (!resolve) break;
                        operations.Add(OwnerOperation.RetreatStarted(battle, sender, ++peer.LastOperation, r.Tick, Body(peer, name)));
                        break;
                    case RecordKind.MissileTarget:
                        name = r.VarUInt(); var targetIndex = r.VarUInt();
                        if (!resolve) break;
                        operations.Add(OwnerOperation.MissileTargetChanged(battle, sender, ++peer.LastOperation, r.Tick,
                            Bullet(peer, name), targetIndex == 0 ? default : Body(own, targetIndex - 1)));
                        break;
                }
                scan?.Add(new ScannedRecord(r.Kind, r.BitPosition - start, reason));
                start = r.BitPosition;
            }
            if (resolve)
            {
                Require(peer.LastOperation == r.LastOperation);
                peer.EndBatch();
            }
            return new OwnerWatermark(battle, sender, r.BatchTick, r.LastOperation);
        }

        private static EntityId Body(StreamNames names, ulong index)
        {
            Require(index < (ulong)names.Bodies.Count);
            return names.Bodies[(int)index].Entity;
        }

        private static EntityId Bullet(StreamNames names, ulong age)
        {
            Require(names.TryGetBullet(age, out var entity));
            return entity;
        }

        internal static void Require(bool valid) { if (!valid) throw new FormatException("Invalid session message."); }

        // A scanned record: its kind, its size in bits including its kind and tick, and a removal's reason.
        internal readonly struct ScannedRecord
        {
            internal readonly RecordKind Kind;
            internal readonly int Bits;
            internal readonly RemovalReason RemovalReason;
            internal ScannedRecord(RecordKind kind, int bits, RemovalReason reason) { Kind = kind; Bits = bits; RemovalReason = reason; }
        }

        internal ref struct Reader
        {
            private BitReader _reader;
            internal long BatchTick;
            internal ulong LastOperation;
            internal RecordKind Kind;
            internal long Tick;
            internal int BitPosition => _reader.BitPosition;

            internal static Reader Batch(ReadOnlySpan<byte> data)
            {
                var r = default(Reader);
                r._reader = new BitReader(data);
                Require(r.Bits(8) == Version);
                var tick = r.VarUInt(); r.LastOperation = r.VarUInt();
                Require(tick <= long.MaxValue - 6);
                r.BatchTick = r.Tick = (long)tick;
                return r;
            }

            // Reads the next record's kind and tick; false at the end of the batch. Kinds start at 1, so a record
            // never reads as padding.
            internal bool NextRecord()
            {
                if (_reader.AtPadding()) return false;
                var kind = Bits(KindBits);
                Require(kind >= (ulong)RecordKind.Hit && kind <= (ulong)RecordKind.MissileTarget);
                Kind = (RecordKind)kind;
                var code = VarUInt();
                var offset = (long)(code >> 1) ^ -(long)(code & 1);
                Require(offset >= -BatchTick && offset <= long.MaxValue - 6 - BatchTick);
                Tick = BatchTick + offset;
                return true;
            }

            internal ulong Bits(int count) { Require(_reader.ReadBits(count, out var v)); return v; }
            internal bool Bool() => Bits(1) != 0;
            internal ulong VarUInt() { Require(_reader.ReadVarUInt(out var v)); return v; }
            internal int Int() { var v = VarUInt(); Require(v <= int.MaxValue); return (int)v; }
            internal EntityId Entity() => new EntityId(Bits(64));
            internal float Float()
            {
                var v = BitConverter.Int32BitsToSingle(unchecked((int)Bits(32)));
                Require(!float.IsNaN(v) && !float.IsInfinity(v));
                return v;
            }
            internal SpawnPose Pose() => new SpawnPose(new Vec2(Float(), Float()), Float(), new Vec2(Float(), Float()), Float());
        }
    }
}
