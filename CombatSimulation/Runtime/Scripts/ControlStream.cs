using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;

namespace CombatSimulation.Runtime
{
    // Network sessions only: controls as sent on the wire. Course in whole degrees (NoCourse: none), throttle in
    // 1/127 steps. The owner applies the dequantized value too, so both copies fly the same frame.
    internal readonly struct QuantizedControls : IEquatable<QuantizedControls>
    {
        internal const int NoCourse = 511;
        internal const int CourseBits = 9;
        internal const int ThrottleBits = 7;
        internal const int ThrottleSteps = 127;

        // Keep the held degree while the raw course stays this close to it, so a course near a half-degree
        // boundary does not alternate between two values.
        internal const double HysteresisDegrees = 0.75;

        internal readonly int Course;
        internal readonly int Throttle;
        internal readonly ulong Systems;

        internal QuantizedControls(int course, int throttle, ulong systems)
        { Course = course; Throttle = throttle; Systems = systems; }

        internal static bool IsValid(int course, int throttle) =>
            (course >= 0 && course < 360 || course == NoCourse) && throttle >= 0 && throttle <= ThrottleSteps;

        internal static QuantizedControls From(in PilotControls raw, in QuantizedControls held, bool hasHeld)
        {
            var course = NoCourse;
            if (raw.HasCourse)
            {
                var degrees = raw.Course * (180.0 / Math.PI);
                degrees -= Math.Floor(degrees / 360) * 360;
                if (hasHeld && held.Course != NoCourse && Math.Abs(DeltaDegrees(degrees, held.Course)) <= HysteresisDegrees)
                    course = held.Course;
                else
                    course = (int)Math.Round(degrees) % 360;
            }
            return new QuantizedControls(course, QuantizeThrottle(raw.Throttle), raw.Systems);
        }

        internal static int QuantizeThrottle(float throttle) => (int)Math.Round(throttle * ThrottleSteps);
        internal static float Dequantize(int throttle) => throttle / (float)ThrottleSteps;

        internal PilotControls ToControls(bool selfDestruct = false) =>
            PilotControls.Create(Course == NoCourse ? (float?)null : Course * DegreesToRadians, Dequantize(Throttle), Systems, selfDestruct);

        public bool Equals(QuantizedControls other) => Course == other.Course && Throttle == other.Throttle && Systems == other.Systems;
        public override bool Equals(object obj) => obj is QuantizedControls other && Equals(other);
        public override int GetHashCode() => (Course * 397 ^ Throttle) * 397 ^ Systems.GetHashCode();

        private static double DeltaDegrees(double degrees, int held)
        {
            var delta = (degrees - held) % 360;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            return delta;
        }

        private const float DegreesToRadians = (float)(Math.PI / 180.0);
    }

    internal readonly struct ControlRecord
    {
        internal readonly uint Index;
        internal readonly long Tick;
        internal readonly QuantizedControls Controls;
        internal ControlRecord(uint index, long tick, in QuantizedControls controls) { Index = index; Tick = tick; Controls = controls; }
    }

    // Input datagram payload, a bit stream (BitWriter):
    //   version (8 bits), battle ID low 32 bits, base tick (VarUInt), then records until only zero padding remains:
    //   control index (VarUInt), base tick - original tick (VarUInt), course (9), throttle (7), systems (VarUInt).
    // The owner of every record is the sending peer. A control index names a ship or drone by spawn order (ControlSender).
    internal static class InputCodec
    {
        internal const byte Version = 1;

        internal static int RecordBits(long baseTick, in ControlRecord record) =>
            BitWriter.VarUIntBits(record.Index) + BitWriter.VarUIntBits((ulong)(baseTick - record.Tick)) +
            QuantizedControls.CourseBits + QuantizedControls.ThrottleBits + BitWriter.VarUIntBits(record.Controls.Systems);

        internal static BitWriter Begin(Span<byte> buffer, BattleId battle, long baseTick)
        {
            var w = new BitWriter(buffer);
            if (!w.WriteBits(Version, 8) || !w.WriteBits(unchecked((uint)battle.Value), 32) || !w.WriteVarUInt((ulong)baseTick))
                throw new ArgumentException("Input buffer is too small.", nameof(buffer));
            return w;
        }

        internal static bool Write(ref BitWriter w, long baseTick, in ControlRecord record)
        {
            if (w.RemainingBits < RecordBits(baseTick, record)) return false;
            w.WriteVarUInt(record.Index); w.WriteVarUInt((ulong)(baseTick - record.Tick));
            w.WriteBits((ulong)record.Controls.Course, QuantizedControls.CourseBits);
            w.WriteBits((ulong)record.Controls.Throttle, QuantizedControls.ThrottleBits);
            w.WriteVarUInt(record.Controls.Systems);
            return true;
        }

        // All or nothing: a malformed datagram leaves records empty.
        internal static void Read(ReadOnlySpan<byte> payload, BattleId battle, List<ControlRecord> records)
        {
            records.Clear();
            var r = new BitReader(payload);
            var header = r.ReadBits(8, out var version) & r.ReadBits(32, out var id) & r.ReadVarUInt(out var baseTick);
            SessionCodec.Require(header && version == Version && id == unchecked((uint)battle.Value) && baseTick <= long.MaxValue - 6);
            while (!r.AtPadding())
            {
                if (!(r.ReadVarUInt(out var index) && index <= uint.MaxValue && r.ReadVarUInt(out var age) && age <= baseTick &&
                    r.ReadBits(QuantizedControls.CourseBits, out var course) && r.ReadBits(QuantizedControls.ThrottleBits, out var throttle) &&
                    r.ReadVarUInt(out var systems) && QuantizedControls.IsValid((int)course, (int)throttle)))
                { records.Clear(); throw new FormatException("Invalid input record."); }
                records.Add(new ControlRecord((uint)index, (long)(baseTick - age), new QuantizedControls((int)course, (int)throttle, systems)));
            }
        }
    }

    // Change-only input for the local owner's ships and drones. A changed frame goes into the next Copies datagrams;
    // every held frame is repeated once per RefreshTicks, staggered by control index, so a lost change still arrives.
    internal sealed class ControlSender
    {
        internal const int Copies = 3;
        internal const int RefreshTicks = 25;

        // Indices follow the local owner's spawn operations in sequence order; the receiver counts the same stream.
        internal void Register(EntityId entity, uint index)
        {
            if (_lookup.ContainsKey(entity)) return;
            _lookup.Add(entity, _held.Count);
            _held.Add(new Held { Entity = entity, Index = index });
        }

        internal bool TryGetIndex(EntityId entity, out uint index)
        {
            index = 0;
            if (!_lookup.TryGetValue(entity, out var slot)) return false;
            index = _held[slot].Index;
            return true;
        }

        // Quantizes against the held frame. False: the frame equals the held one, nothing to apply or send.
        // An entity without an index (no spawn record yet) is quantized against nothing and never sent.
        internal bool Quantize(EntityId entity, in PilotControls raw, out QuantizedControls controls)
        {
            if (!_lookup.TryGetValue(entity, out var slot))
            {
                controls = QuantizedControls.From(raw, default, false);
                return true;
            }
            var item = _held[slot];
            controls = QuantizedControls.From(raw, item.Controls, item.HasFrame);
            if (item.HasFrame && controls.Equals(item.Controls)) return false;
            item.Controls = controls; item.HasFrame = true; _held[slot] = item;
            return true;
        }

        internal bool TryGetHeld(EntityId entity, out QuantizedControls controls)
        {
            controls = default;
            if (!_lookup.TryGetValue(entity, out var slot) || !_held[slot].HasFrame) return false;
            controls = _held[slot].Controls;
            return true;
        }

        internal void Changed(EntityId entity, long tick, in QuantizedControls controls)
        {
            if (!_lookup.TryGetValue(entity, out var slot)) return;
            var item = _held[slot]; item.Tick = tick; _held[slot] = item;
            _pending.Add(new Pending { Record = new ControlRecord(item.Index, tick, controls), Remaining = Copies });
        }

        // A removed entity's held frame and unsent copies are dropped.
        internal void Forget(EntityId entity)
        {
            if (!_lookup.TryGetValue(entity, out var slot)) return;
            var index = _held[slot].Index;
            _held.RemoveAt(slot); _lookup.Remove(entity);
            for (var i = slot; i < _held.Count; ++i) _lookup[_held[i].Entity] = i;
            for (var i = _pending.Count - 1; i >= 0; --i)
                if (_pending[i].Record.Index == index) _pending.RemoveAt(i);
        }

        // Newest changes first, then due refreshes. Stops at local backpressure: records after the refused datagram
        // keep their copies.
        internal void Send(PeerConnection peer, BattleId battle, long tick, byte[] buffer)
        {
            var w = InputCodec.Begin(buffer, battle, tick);
            var empty = true;
            for (var i = _pending.Count - 1; i >= 0; --i)
            {
                if (!Write(ref w, ref empty, peer, battle, tick, buffer, _pending[i].Record)) return;
                var item = _pending[i]; --item.Remaining; _pending[i] = item;
            }
            for (var i = 0; i < _held.Count; ++i)
            {
                var held = _held[i];
                if (!held.HasFrame || (tick + held.Index) % RefreshTicks != 0 || IsPending(held.Index, held.Tick)) continue;
                if (!Write(ref w, ref empty, peer, battle, tick, buffer, new ControlRecord(held.Index, held.Tick, held.Controls))) break;
            }
            if (!empty) peer.TrySendInput(buffer.AsSpan(0, w.Finish()));
            for (var i = _pending.Count - 1; i >= 0; --i)
                if (_pending[i].Remaining <= 0) _pending.RemoveAt(i);
        }

        private bool IsPending(uint index, long tick)
        {
            foreach (var item in _pending) if (item.Record.Index == index && item.Record.Tick == tick) return true;
            return false;
        }

        private static bool Write(ref BitWriter w, ref bool empty, PeerConnection peer, BattleId battle, long tick,
            byte[] buffer, in ControlRecord record)
        {
            if (InputCodec.Write(ref w, tick, record)) { empty = false; return true; }
            if (empty) throw new InvalidOperationException("An input record does not fit in one datagram.");
            if (!peer.TrySendInput(buffer.AsSpan(0, w.Finish()))) return false;
            w = InputCodec.Begin(buffer, battle, tick);
            InputCodec.Write(ref w, tick, record);
            return true;
        }

        private struct Held
        {
            internal EntityId Entity;
            internal uint Index;
            internal bool HasFrame;
            internal long Tick;
            internal QuantizedControls Controls;
        }
        private struct Pending
        {
            internal ControlRecord Record;
            internal int Remaining;
        }
        private readonly List<Held> _held = new List<Held>();
        private readonly Dictionary<EntityId, int> _lookup = new Dictionary<EntityId, int>();
        private readonly List<Pending> _pending = new List<Pending>();
    }
}
