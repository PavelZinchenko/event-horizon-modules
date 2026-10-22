using System;
using System.Collections.Generic;

namespace CombatSimulation.Network
{
    internal sealed class InputChannel
    {
        internal InputChannel(ITransport transport, int datagramSize)
        {
            _transport = transport;
            _buffer = new byte[datagramSize];
            MaxPayload = datagramSize - PacketFormat.HeaderSize - PacketFormat.InputHeaderSize;
            for (var i = 0; i < 4; ++i) _ticks[i] = new TickRecords();
        }

        internal int MaxPayload { get; }

        internal void Queue(ulong key, long tick, ReadOnlySpan<byte> payload)
        {
            if (payload.Length > MaxPayload) throw new ArgumentOutOfRangeException(nameof(payload));
            Advance(tick);
            var bucket = _ticks[tick % 4];
            if (bucket.Tick != tick) { bucket.Tick = tick; bucket.Count = 0; }
            var index = 0;
            while (index < bucket.Count && bucket.Records[index].Key != key) ++index;
            if (index == bucket.Records.Count) bucket.Records.Add(new Record());
            var record = bucket.Records[index];
            if (index == bucket.Count) ++bucket.Count;
            if (record.Payload.Length < payload.Length) Array.Resize(ref record.Payload, payload.Length);
            payload.CopyTo(record.Payload);
            record.Length = payload.Length;
            record.Key = key;
        }

        internal void Send(long tick)
        {
            Advance(tick);
            var writer = PacketFormat.Begin(_buffer, PacketKind.Input, 0);
            // Current input goes first when transport backpressure limits this send.
            for (var age = 0; age < 4 && tick >= age; ++age)
            {
                var recordTick = tick - age;
                var bucket = _ticks[recordTick % 4];
                if (bucket.Tick != recordTick) continue;
                for (var i = 0; i < bucket.Count; ++i)
                {
                    var record = bucket.Records[i];
                    if (writer.Remaining < PacketFormat.InputHeaderSize + record.Length)
                    {
                        if (!Flush(writer.Position)) return;
                        writer = PacketFormat.Begin(_buffer, PacketKind.Input, 0);
                    }
                    writer.WriteUInt64(record.Key);
                    writer.WriteInt64(recordTick);
                    writer.WriteUInt16((ushort)record.Length);
                    writer.WriteBytes(record.Payload.AsSpan(0, record.Length));
                }
            }
            if (writer.Position > PacketFormat.HeaderSize) Flush(writer.Position);
        }

        internal static bool Receive(ReadOnlySpan<byte> payload, IPeerReceiver receiver)
        {
            // Validate every record before making any callbacks.
            var reader = new ByteReader(payload);
            while (reader.Remaining != 0)
                if (!ReadRecord(ref reader, out _, out _, out _)) return false;
            reader = new ByteReader(payload);
            while (reader.Remaining != 0)
            {
                ReadRecord(ref reader, out var key, out var tick, out var data);
                receiver.OnInput(key, tick, data);
            }
            return true;
        }

        private static bool ReadRecord(ref ByteReader reader, out ulong key, out long tick, out ReadOnlySpan<byte> data)
        {
            key = 0;
            tick = 0;
            data = default;
            return reader.ReadUInt64(out key) && reader.ReadInt64(out tick) && tick >= 0 &&
                reader.ReadUInt16(out var length) && reader.ReadBytes(length, out data);
        }

        private void Advance(long tick)
        {
            if (tick < 0 || tick < _latestTick) throw new ArgumentOutOfRangeException(nameof(tick));
            _latestTick = tick;
        }

        private bool Flush(int length)
        {
            var header = new ByteWriter(_buffer.AsSpan(2));
            header.WriteUInt16((ushort)(length - PacketFormat.HeaderSize));
            return _transport.TrySend(_buffer.AsSpan(0, length));
        }

        private sealed class Record
        {
            internal ulong Key;
            internal int Length;
            internal byte[] Payload = Array.Empty<byte>();
        }
        private sealed class TickRecords
        {
            internal long Tick = -1;
            internal int Count;
            internal readonly List<Record> Records = new List<Record>();
        }
        private readonly ITransport _transport;
        private readonly byte[] _buffer;
        private readonly TickRecords[] _ticks = new TickRecords[4];
        private long _latestTick = -1;
    }
}
