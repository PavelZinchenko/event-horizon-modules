using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;
using CombatSimulation.Physics;

namespace CombatSimulation.Runtime
{
    // A ship or drone by control index. Both peers know its definition from the spawn record, so no counts travel.
    internal readonly struct ControlledBody
    {
        internal readonly EntityId Entity;
        internal readonly ShipDefinition Definition;
        internal ControlledBody(EntityId entity, ShipDefinition definition) { Entity = entity; Definition = definition; }
    }

    // Correction datagram payload (network plan, G1), a bit stream (BitWriter):
    //   version (8 bits), battle ID low 32 bits, batch tick and applied hit sequence (both VarUInt), then bodies in
    //   increasing control index order until only zero padding remains. The owner of every body is the sending peer.
    // Body: index gap (VarUInt: the index minus the previous index minus 1; the first body: its index). Position per
    //   axis on a 1/1024-unit grid, with the bit count from the arena size; angle (14 bits); linear velocity per axis on
    //   a 1/128 unit/s grid (15 bits); angular velocity on a 1/512 rad/s grid (14 bits). Velocity field 0 escapes to a
    //   32-bit float, because a ship can exceed its engine's maximum speed.
    //   Armor, shield and energy (ResourceLevel): no bits for a maximum of 0; 1 bit full; if not full, 1 bit delay
    //   running (only for a resource that recharges) and the 8-bit level.
    //   Point-defense timers in device order, stop then query: 1 bit ready; if not ready, the age (VarUInt).
    // A batch that does not fit in one datagram continues in more datagrams, each with its own header.
    internal static class CorrectionCodec
    {
        internal const byte Version = 1;
        internal const double PositionScale = 1024, LinearScale = 128, AngularScale = 512;
        internal const int AngleBits = 14, LinearBits = 15, AngularBits = 14, LevelBits = 8;

        internal readonly struct Arena
        {
            internal readonly long XCells, YCells;
            internal readonly int XBits, YBits;

            internal Arena(float width, float height)
            {
                XCells = Cells(width); YCells = Cells(height);
                XBits = BitLength(XCells - 1); YBits = BitLength(YCells - 1);
            }

            private static long Cells(float size) => Math.Max(1L, (long)Math.Ceiling(size * PositionScale));
        }

        internal static BitWriter Begin(Span<byte> buffer, BattleId battle, long tick, ulong appliedHitSequence)
        {
            var w = new BitWriter(buffer);
            if (!w.WriteBits(Version, 8) || !w.WriteBits(unchecked((uint)battle.Value), 32) ||
                !w.WriteVarUInt((ulong)tick) || !w.WriteVarUInt(appliedHitSequence))
                throw new ArgumentException("Correction buffer is too small.", nameof(buffer));
            return w;
        }

        internal static int BodyBits(in Arena arena, uint gap, in BodyCorrection body, ShipDefinition definition,
            ReadOnlySpan<PointDefenseTimers> timers)
        {
            var motion = body.Motion;
            var bits = BitWriter.VarUIntBits(gap) + arena.XBits + arena.YBits + AngleBits +
                VelocityBits(motion.LinearVelocity.X, LinearScale, LinearBits) +
                VelocityBits(motion.LinearVelocity.Y, LinearScale, LinearBits) +
                VelocityBits(motion.AngularVelocity, AngularScale, AngularBits) +
                ResourceBits(body.Armor, definition.ArmorResource) + ResourceBits(body.Shield, definition.Shield) +
                ResourceBits(body.Energy, definition.Energy);
            foreach (var item in timers) bits += TimerBits(item.StopAge) + TimerBits(item.QueryAge);
            return bits;
        }

        // False, with nothing written, when the body does not fit.
        internal static bool Write(ref BitWriter w, in Arena arena, uint gap, in BodyCorrection body, ShipDefinition definition,
            ReadOnlySpan<PointDefenseTimers> timers)
        {
            if (timers.Length != definition.PointDefenseCount) throw new ArgumentException("Timer count differs from the definition.", nameof(timers));
            if (w.RemainingBits < BodyBits(arena, gap, body, definition, timers)) return false;
            var motion = body.Motion;
            w.WriteVarUInt(gap);
            w.WriteBits((ulong)Position(motion.Position.X, arena.XCells), arena.XBits);
            w.WriteBits((ulong)Position(motion.Position.Y, arena.YCells), arena.YBits);
            w.WriteBits((ulong)Angle(motion.Angle), AngleBits);
            WriteVelocity(ref w, motion.LinearVelocity.X, LinearScale, LinearBits);
            WriteVelocity(ref w, motion.LinearVelocity.Y, LinearScale, LinearBits);
            WriteVelocity(ref w, motion.AngularVelocity, AngularScale, AngularBits);
            WriteResource(ref w, body.Armor, definition.ArmorResource);
            WriteResource(ref w, body.Shield, definition.Shield);
            WriteResource(ref w, body.Energy, definition.Energy);
            foreach (var item in timers) { WriteTimer(ref w, item.StopAge); WriteTimer(ref w, item.QueryAge); }
            return true;
        }

        // Reads one datagram into the batch; a malformed datagram throws, and the caller submits nothing. Bodies are
        // sorted by index and the receiver knows a prefix of the indices, so reading stops at the first index whose
        // spawn record has not arrived: every later body is unknown too.
        internal static void Read(ReadOnlySpan<byte> payload, BattleId battle, int owner, in Arena arena,
            List<ControlledBody> bodies, CorrectionBatch batch, ref PointDefenseTimers[] timers)
        {
            var r = new BitReader(payload);
            var header = r.ReadBits(8, out var version) & r.ReadBits(32, out var id) & r.ReadVarUInt(out var tick) &
                r.ReadVarUInt(out var ack);
            Require(header && version == Version && id == unchecked((uint)battle.Value) && tick <= long.MaxValue - 6);
            batch.Begin(battle, owner, (long)tick, ack);
            var index = -1L;
            while (!r.AtPadding())
            {
                Require(r.ReadVarUInt(out var gap) && gap <= uint.MaxValue);
                index += (long)gap + 1;
                if (index >= bodies.Count || bodies[(int)index].Definition == null) return;
                var body = bodies[(int)index];
                var definition = body.Definition;
                var x = Bits(ref r, arena.XBits); var y = Bits(ref r, arena.YBits);
                Require((long)x < arena.XCells && (long)y < arena.YCells);
                var angle = Bits(ref r, AngleBits) * (2 * Math.PI / (1 << AngleBits));
                var vx = ReadVelocity(ref r, LinearScale, LinearBits);
                var vy = ReadVelocity(ref r, LinearScale, LinearBits);
                var spin = ReadVelocity(ref r, AngularScale, AngularBits);
                var armor = ReadResource(ref r, definition.ArmorResource);
                var shield = ReadResource(ref r, definition.Shield);
                var energy = ReadResource(ref r, definition.Energy);
                var count = definition.PointDefenseCount;
                if (timers.Length < count) Array.Resize(ref timers, count);
                for (var i = 0; i < count; ++i) timers[i] = new PointDefenseTimers(ReadTimer(ref r), ReadTimer(ref r));
                var pose = new SpawnPose(new Vec2((float)(x / PositionScale), (float)(y / PositionScale)), (float)angle,
                    new Vec2(vx, vy), spin);
                batch.Add(body.Entity, pose, armor, shield, energy, timers.AsSpan(0, count));
            }
        }

        // The cell of a torus coordinate; the cell past the edge wraps to 0.
        internal static long Position(float value, long cells)
        {
            var cell = (long)Math.Round(value * PositionScale) % cells;
            return cell < 0 ? cell + cells : cell;
        }

        internal static long Angle(float angle)
        {
            var turns = angle / (2 * Math.PI);
            turns -= Math.Floor(turns);
            return (long)Math.Round(turns * (1 << AngleBits)) & ((1 << AngleBits) - 1);
        }

        // Offset binary; 0 is the escape to a full float.
        internal static long VelocityField(float value, double scale, int bits)
        {
            var steps = Math.Round(value * scale);
            var limit = (1L << (bits - 1)) - 1;
            return Math.Abs(steps) <= limit ? (long)steps + (1L << (bits - 1)) : 0;
        }

        private static int VelocityBits(float value, double scale, int bits) => bits + (VelocityField(value, scale, bits) == 0 ? 32 : 0);

        private static void WriteVelocity(ref BitWriter w, float value, double scale, int bits)
        {
            var field = VelocityField(value, scale, bits);
            w.WriteBits((ulong)field, bits);
            if (field == 0) w.WriteBits(unchecked((uint)BitConverter.SingleToInt32Bits(value)), 32);
        }

        // An escaped value must be outside the field, so every velocity has one encoding.
        private static float ReadVelocity(ref BitReader r, double scale, int bits)
        {
            var field = Bits(ref r, bits);
            if (field != 0) return (float)(((long)field - (1L << (bits - 1))) / scale);
            var value = BitConverter.Int32BitsToSingle(unchecked((int)Bits(ref r, 32)));
            Require(!float.IsNaN(value) && !float.IsInfinity(value) && VelocityField(value, scale, bits) == 0);
            return value;
        }

        private static int ResourceBits(in ResourceLevel level, in ResourceDefinition resource) =>
            !(resource.Max > 0f) ? 0 : level.IsFull ? 1 : 1 + (resource.RechargeRate > 0f ? 1 : 0) + LevelBits;

        private static void WriteResource(ref BitWriter w, in ResourceLevel level, in ResourceDefinition resource)
        {
            if (!(resource.Max > 0f)) return;
            w.WriteBool(level.IsFull);
            if (level.IsFull) return;
            if (resource.RechargeRate > 0f) w.WriteBool(level.DelayRunning);
            w.WriteBits(level.Level, LevelBits);
        }

        private static ResourceLevel ReadResource(ref BitReader r, in ResourceDefinition resource)
        {
            if (!(resource.Max > 0f) || Bits(ref r, 1) != 0) return ResourceLevel.Full;
            var delay = resource.RechargeRate > 0f && Bits(ref r, 1) != 0;
            return ResourceLevel.Partial((byte)Bits(ref r, LevelBits), delay);
        }

        private static int TimerBits(int age) => age == PointDefenseTimers.Ready ? 1 : 1 + BitWriter.VarUIntBits((ulong)age);

        private static void WriteTimer(ref BitWriter w, int age)
        {
            if (age != PointDefenseTimers.Ready && age < 0) throw new ArgumentOutOfRangeException(nameof(age));
            w.WriteBool(age == PointDefenseTimers.Ready);
            if (age != PointDefenseTimers.Ready) w.WriteVarUInt((ulong)age);
        }

        private static int ReadTimer(ref BitReader r)
        {
            if (Bits(ref r, 1) != 0) return PointDefenseTimers.Ready;
            Require(r.ReadVarUInt(out var age) && age <= int.MaxValue);
            return (int)age;
        }

        private static ulong Bits(ref BitReader r, int count)
        {
            Require(r.ReadBits(count, out var value));
            return value;
        }

        private static int BitLength(long value)
        {
            var bits = 1;
            while ((value >>= 1) != 0) ++bits;
            return bits;
        }

        private static void Require(bool valid) => SessionCodec.Require(valid);
    }
}
