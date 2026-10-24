using System;
using CombatSimulation.Core;
using CombatSimulation.Network;
using CombatSimulation.Physics;

namespace CombatSimulation.Runtime
{
    // Input (1) is a separate bit-packed datagram (InputCodec) and Batch (4) is one reliable tick batch;
    // both only tag the receive queue. Hit and Operation are record kinds inside a batch.
    internal enum SessionMessage : byte { Input = 1, Hit, Operation, Batch, Correction }

    // Versioned little-endian messages. The caller supplies reusable buffers with the stated capacity.
    // A tick batch has its own short header: version, battle ID low 32 bits, tick and last operation sequence
    // (both VarUInt). Then records until the end: kind, record tick − batch tick (zigzag VarUInt), fields.
    // The sender is the owner of every record. Corrections keep the 22-byte message header.
    internal static class SessionCodec
    {
        internal const byte Version = 2;
        internal const int HeaderSize = 22;
        internal const int RecordCapacity = 160;
        internal const int BatchHeaderCapacity = 1 + 4 + 10 + 10;
        internal const int MaxBatchBytes = 1 << 20;

        internal static ByteWriter Header(Span<byte> buffer, SessionMessage kind, BattleId battle, int owner, long tick)
        {
            var w = new ByteWriter(buffer);
            w.WriteByte(Version); w.WriteByte((byte)kind); w.WriteUInt64(battle.Value);
            w.WriteUInt32((uint)owner); w.WriteInt64(tick);
            return w;
        }

        internal static int BatchHeader(Span<byte> buffer, BattleId battle, long tick, ulong lastOperation)
        {
            var w = new ByteWriter(buffer);
            w.WriteByte(Version); w.WriteUInt32(unchecked((uint)battle.Value));
            w.WriteVarUInt((ulong)tick); w.WriteVarUInt(lastOperation);
            return w.Position;
        }

        private static ByteWriter Record(Span<byte> buffer, SessionMessage kind, long batchTick, long tick)
        {
            var w = new ByteWriter(buffer);
            var offset = unchecked(tick - batchTick);
            w.WriteByte((byte)kind); w.WriteVarUInt(unchecked((ulong)((offset << 1) ^ (offset >> 63))));
            return w;
        }

        internal static int Hit(Span<byte> buffer, long batchTick, in ResolvedHit hit)
        {
            var w = Record(buffer, SessionMessage.Hit, batchTick, hit.Tick);
            w.WriteUInt64(hit.Sequence); w.WriteUInt64(hit.AttackerId.Value); w.WriteUInt32(unchecked((uint)hit.DeviceIndex));
            w.WriteUInt64(hit.TargetId.Value); w.WriteUInt32((uint)hit.TargetOwner); w.WriteUInt32((uint)hit.TargetShape);
            w.WriteByte((byte)hit.ContactState); w.WriteByte((byte)hit.ImpactKind); w.WriteSingle(hit.Amount);
            return w.Position;
        }

        internal static ResolvedHit ReadHit(ref Reader r)
        {
            var sequence = r.U64(); var attacker = new EntityId(r.U64()); var device = r.I32();
            var target = new EntityId(r.U64()); var owner = r.I32(); var shape = r.I32();
            var contact = r.Byte(); var impact = r.Byte(); var amount = r.Float();
            Require(sequence > 0 && !attacker.IsNull && !target.IsNull && device >= -1 && owner >= 0 && shape >= 0 &&
                contact <= (byte)HitContactState.End && impact <= (byte)HitImpactKind.DamageOverTime && amount >= 0);
            return new ResolvedHit(r.Battle, r.Tick, attacker, r.Owner, device, target, owner, shape,
                (HitContactState)contact, (HitImpactKind)impact, amount, sequence);
        }

        internal static int Operation(Span<byte> buffer, long batchTick, in OwnerOperation op)
        {
            var w = Record(buffer, SessionMessage.Operation, batchTick, op.Tick);
            w.WriteUInt64(op.Sequence); w.WriteByte((byte)op.Kind); w.WriteUInt64(op.EntityId.Value);
            switch (op.Kind)
            {
                case OwnerOperationKind.Roster:
                    var roster = op.Roster;
                    w.WriteUInt32(roster.RosterId.Value); w.WriteUInt32((uint)roster.Revision);
                    w.WriteByte((byte)roster.PreviousStatus); w.WriteByte((byte)roster.Status); w.WriteByte((byte)roster.Reason);
                    w.WriteUInt32(roster.DeploymentCount); w.WriteInt64(roster.DeploymentOrder);
                    w.WriteSingle(roster.ArmorDeficit); w.WriteSingle(roster.ShieldDeficit); w.WriteSingle(roster.DamageFraction);
                    Pose(ref w, op.Pose);
                    break;
                case OwnerOperationKind.DroneSpawn:
                    Bay(ref w, op.BayKey.Value); w.WriteUInt64(op.ParentId.Value); Pose(ref w, op.Pose);
                    break;
                case OwnerOperationKind.Removal:
                    w.WriteByte((byte)op.RemovalReason); w.WriteByte(op.BayKey.HasValue ? (byte)1 : (byte)0);
                    if (op.BayKey.HasValue) Bay(ref w, op.BayKey.Value);
                    break;
                case OwnerOperationKind.BulletSpawn:
                    w.WriteUInt64(op.ParentId.Value); w.WriteUInt32((uint)op.DeviceIndex);
                    w.WriteUInt32((uint)op.BarrelIndex); w.WriteUInt32(op.SpawnSequence);
                    break;
            }
            return w.Position;
        }

        internal static OwnerOperation ReadOperation(ref Reader r)
        {
            var sequence = r.U64(); var kind = r.Byte(); var entity = new EntityId(r.U64());
            Require(sequence > 0);
            switch ((OwnerOperationKind)kind)
            {
                case OwnerOperationKind.Roster:
                    var id = new RosterId(r.U32()); var revision = r.I32();
                    var before = r.Byte(); var after = r.Byte(); var reason = r.Byte();
                    var count = r.U32(); var order = r.I64(); var armor = r.Float(); var shield = r.Float(); var damage = r.Float();
                    Require(revision > 0 && before <= 2 && after <= 2 && reason <= (byte)RosterTransitionReason.ForcedDestruction &&
                        order >= 0 && armor >= 0 && shield >= 0 && damage >= 0 && damage <= 1);
                    var record = new RosterRecord(id, revision, (RosterStatus)before, (RosterStatus)after,
                        (RosterTransitionReason)reason, count, order, armor, shield, damage);
                    return OwnerOperation.RosterChange(r.Battle, r.Owner, sequence, r.Tick, entity, record, r.Pose());
                case OwnerOperationKind.DroneSpawn:
                    var bay = r.Bay(); var parent = new EntityId(r.U64()); var pose = r.Pose();
                    Require(!entity.IsNull && !parent.IsNull);
                    return OwnerOperation.DroneLaunch(r.Battle, r.Owner, sequence, r.Tick, entity, bay, parent, pose);
                case OwnerOperationKind.Removal:
                    var removal = r.Byte(); var hasBay = r.Bool();
                    DroneBayKey? optionalBay = hasBay ? r.Bay() : (DroneBayKey?)null;
                    Require(!entity.IsNull && removal >= (byte)RemovalReason.Death && removal <= (byte)RemovalReason.Disposal);
                    return OwnerOperation.Remove(r.Battle, r.Owner, sequence, r.Tick, entity, (RemovalReason)removal, optionalBay);
                case OwnerOperationKind.BulletSpawn:
                    parent = new EntityId(r.U64()); var device = r.I32(); var barrel = r.I32(); var spawn = r.U32();
                    Require(!entity.IsNull && !parent.IsNull && device >= 0 && barrel >= 0);
                    return OwnerOperation.BulletFired(r.Battle, r.Owner, sequence, r.Tick, entity, parent, device, barrel, spawn);
                default: throw new FormatException("Unknown owner operation.");
            }
        }

        internal static int CorrectionSize(CorrectionBatch batch) => checked(HeaderSize + 12 +
            batch.Bodies.Length * 68 + batch.Devices.Length * 18 + batch.BarrelShotTicks.Length * 8);

        internal static int Correction(Span<byte> buffer, CorrectionBatch batch)
        {
            if (buffer.Length < CorrectionSize(batch)) throw new ArgumentException("Correction buffer is too small.", nameof(buffer));
            var w = Header(buffer, SessionMessage.Correction, batch.BattleId, batch.Owner, batch.Tick);
            w.WriteUInt64(batch.AppliedHitSequence); w.WriteUInt32((uint)batch.Bodies.Length);
            foreach (var body in batch.Bodies)
            {
                w.WriteUInt64(body.EntityId.Value); Pose(ref w, body.Motion); w.WriteSingle(body.Armor);
                w.WriteSingle(body.Shield.Value); w.WriteSingle(body.Shield.DelayRemaining);
                w.WriteSingle(body.Energy.Value); w.WriteSingle(body.Energy.DelayRemaining);
                w.WriteInt64(body.RetreatCompletionTick); w.WriteUInt32((uint)body.DeviceCount); w.WriteUInt32((uint)body.BarrelCount);
                foreach (var device in batch.GetDevices(body))
                {
                    w.WriteInt64(device.LastTick); w.WriteInt64(device.NextTick);
                    w.WriteByte(device.Enabled ? (byte)1 : (byte)0); w.WriteByte(device.Started ? (byte)1 : (byte)0);
                }
                foreach (var tick in batch.GetBarrelShotTicks(body)) w.WriteInt64(tick);
            }
            return w.Position;
        }

        // Scratch arrays grow only when a larger body first appears. Nothing reaches Core until End succeeds.
        internal static void ReadCorrection(ref Reader r, CorrectionBatch batch, ref DeviceState[] devices, ref long[] barrels)
        {
            var sequence = r.U64(); var count = r.I32();
            Require(count >= 0 && count <= r.Remaining / 68);
            batch.Begin(r.Battle, r.Owner, r.Tick, sequence);
            for (var i = 0; i < count; ++i)
            {
                var entity = new EntityId(r.U64()); var pose = r.Pose(); var armor = r.Float();
                var shield = new RechargeState(r.Float(), r.Float()); var energy = new RechargeState(r.Float(), r.Float());
                var retreat = r.I64(); var deviceCount = r.I32(); var barrelCount = r.I32();
                Require(!entity.IsNull && armor >= 0 && shield.Value >= 0 && shield.DelayRemaining >= 0 &&
                    energy.Value >= 0 && energy.DelayRemaining >= 0 && retreat >= -1 &&
                    deviceCount >= 0 && deviceCount <= ShipDefinition.MaxDevices && barrelCount >= 0 &&
                    (long)deviceCount * 18 + (long)barrelCount * 8 <= r.Remaining);
                if (devices.Length < deviceCount) Array.Resize(ref devices, deviceCount);
                if (barrels.Length < barrelCount) Array.Resize(ref barrels, barrelCount);
                for (var d = 0; d < deviceCount; ++d) devices[d] = new DeviceState(r.I64(), r.I64(), r.Bool(), r.Bool());
                for (var b = 0; b < barrelCount; ++b) barrels[b] = r.I64();
                batch.Add(entity, pose, armor, shield, energy, retreat, devices.AsSpan(0, deviceCount), barrels.AsSpan(0, barrelCount));
            }
        }

        private static void Pose(ref ByteWriter w, in SpawnPose pose)
        {
            w.WriteSingle(pose.Position.X); w.WriteSingle(pose.Position.Y); w.WriteSingle(pose.Angle);
            w.WriteSingle(pose.LinearVelocity.X); w.WriteSingle(pose.LinearVelocity.Y); w.WriteSingle(pose.AngularVelocity);
        }
        private static void Bay(ref ByteWriter w, in DroneBayKey bay)
        { w.WriteUInt32(bay.RosterId.Value); w.WriteUInt32((uint)bay.DeviceIndex); }
        internal static void Require(bool valid) { if (!valid) throw new FormatException("Invalid session message."); }

        internal ref struct Reader
        {
            private ByteReader _reader;
            internal BattleId Battle;
            internal int Owner;
            internal long Tick;
            internal SessionMessage Kind;
            internal long BatchTick;
            internal ulong LastOperation;
            internal int Remaining => _reader.Remaining;
            internal Reader(ReadOnlySpan<byte> data)
            {
                this = default; _reader = new ByteReader(data);
                Require(Byte() == Version); Kind = (SessionMessage)Byte();
                Battle = new BattleId(U64()); Owner = I32(); Tick = I64();
                Require(Owner >= 0 && Tick >= 0 && Tick <= long.MaxValue - 6);
            }

            // The batch names only the low 32 bits of the battle; the receiver supplies its own ID and the sender.
            internal static Reader Batch(ReadOnlySpan<byte> data, BattleId battle, int owner)
            {
                var r = default(Reader);
                r._reader = new ByteReader(data);
                Require(r.Byte() == Version && r.U32() == unchecked((uint)battle.Value));
                var tick = r.VarUInt(); r.LastOperation = r.VarUInt();
                Require(tick <= long.MaxValue - 6);
                r.Battle = battle; r.Owner = owner; r.BatchTick = r.Tick = (long)tick; r.Kind = SessionMessage.Batch;
                return r;
            }

            // Reads the next record's kind and tick; false at the end of the batch.
            internal bool NextRecord()
            {
                if (Remaining == 0) return false;
                Kind = (SessionMessage)Byte();
                Require(Kind == SessionMessage.Hit || Kind == SessionMessage.Operation);
                var code = VarUInt();
                var offset = (long)(code >> 1) ^ -(long)(code & 1);
                Require(offset >= -BatchTick && offset <= long.MaxValue - 6 - BatchTick);
                Tick = BatchTick + offset;
                return true;
            }
            internal byte Byte() { Require(_reader.ReadByte(out var v)); return v; }
            internal bool Bool() { var v = Byte(); Require(v <= 1); return v != 0; }
            internal uint U32() { Require(_reader.ReadUInt32(out var v)); return v; }
            internal int I32() => unchecked((int)U32());
            internal ulong U64() { Require(_reader.ReadUInt64(out var v)); return v; }
            internal long I64() => unchecked((long)U64());
            internal ulong VarUInt() { Require(_reader.ReadVarUInt(out var v)); return v; }
            internal float Float()
            { Require(_reader.ReadSingle(out var v) && !float.IsNaN(v) && !float.IsInfinity(v)); return v; }
            internal SpawnPose Pose() => new SpawnPose(new Vec2(Float(), Float()), Float(), new Vec2(Float(), Float()), Float());
            internal DroneBayKey Bay()
            { var id = new RosterId(U32()); var device = I32(); Require(device >= 0); return new DroneBayKey(id, device); }
            internal void End() => Require(Remaining == 0);
        }
    }
}
