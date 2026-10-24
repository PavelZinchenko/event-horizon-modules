using System;
using System.Collections.Generic;

namespace CombatSimulation.Network
{
    // One ordered byte stream of length-prefixed messages, cut into datagrams. Sequence numbers count datagrams.
    // Messages queued between two flushes share a datagram, and the unsent tail keeps filling while the window is full.
    // Sequence arithmetic uses unsigned distances. The live window never spans half the sequence space.
    internal sealed class ReliableChannel
    {
        internal const int WindowSize = 64;
        private const int MaxPrefixSize = 5;

        internal ReliableChannel(ITransport transport, IPeerReceiver receiver, int datagramSize, uint initialSequence)
        {
            _transport = transport;
            _receiver = receiver;
            _datagramSize = datagramSize;
            _dataOffset = PacketFormat.HeaderSize + PacketFormat.ReliableHeaderSize;
            _dataCapacity = datagramSize - _dataOffset;
            _sendBase = _nextSend = _ackFrontier = _receiveNext = initialSequence;
            for (var i = 0; i < WindowSize; ++i) _received[i] = new ReceivedPacket(_dataCapacity);
        }

        internal int PendingPackets => _pending.Count + (int)unchecked(_nextSend - _sendBase);
        internal bool IsFaulted { get; private set; }

        internal void Queue(ReadOnlySpan<byte> message)
        {
            Span<byte> prefix = stackalloc byte[MaxPrefixSize];
            var writer = new ByteWriter(prefix);
            writer.WriteVarUInt((uint)message.Length);
            var added = ((long)writer.Position + message.Length + _dataCapacity - 1) / _dataCapacity + 1;
            if (PendingPackets + added >= int.MaxValue)
                throw new InvalidOperationException("Reliable sequence space exhausted.");
            Append(prefix.Slice(0, writer.Position));
            Append(message);
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                if (_tail == null || _tail.Length == _datagramSize)
                {
                    _tail = _pool.Count == 0 ? new SentPacket(_datagramSize) : _pool.Pop();
                    _tail.Length = _dataOffset;
                    _tail.Acknowledged = false;
                    _pending.Enqueue(_tail);
                }
                var count = Math.Min(data.Length, _datagramSize - _tail.Length);
                data.Slice(0, count).CopyTo(_tail.Buffer.AsSpan(_tail.Length));
                _tail.Length += count;
                data = data.Slice(count);
            }
        }

        internal void Flush(double now, double resendSeconds, double fastResendSeconds)
        {
            if (IsFaulted) return;
            var outstanding = unchecked(_nextSend - _sendBase);
            var missing = unchecked(_ackFrontier - _sendBase);
            if (missing > outstanding) missing = 0;
            for (uint i = 0; i < outstanding; ++i)
            {
                var packet = _sent[unchecked(_sendBase + i) % WindowSize];
                // The peer has acknowledged a later packet, so this one is probably lost.
                var wait = i < missing ? fastResendSeconds : resendSeconds;
                if (!packet.Acknowledged && now - packet.LastSent >= wait &&
                    _transport.TrySend(packet.Buffer.AsSpan(0, packet.Length))) packet.LastSent = now;
            }
            while (_pending.Count != 0 && unchecked(_nextSend - _sendBase) < WindowSize)
            {
                var packet = _pending.Peek();
                // The sequence is assigned at the first send, so an unsent tail can still take more messages.
                packet.Sequence = _nextSend;
                var header = PacketFormat.Begin(packet.Buffer, PacketKind.Reliable, packet.Length - PacketFormat.HeaderSize);
                header.WriteUInt32(packet.Sequence);
                if (!_transport.TrySend(packet.Buffer.AsSpan(0, packet.Length))) break;
                _pending.Dequeue();
                if (packet == _tail) _tail = null;
                packet.LastSent = now;
                _sent[_nextSend % WindowSize] = packet;
                ++_nextSend;
            }
            if (!_ackDirty) return;
            ulong mask = 0;
            for (var i = 0; i < WindowSize; ++i)
                if (_received[unchecked(_receiveNext + (uint)i) % WindowSize].Present) mask |= 1UL << i;
            var writer = PacketFormat.Begin(_ackBuffer, PacketKind.Ack, 12);
            writer.WriteUInt32(_receiveNext);
            writer.WriteUInt64(mask);
            if (_transport.TrySend(_ackBuffer)) _ackDirty = false;
        }

        internal bool ReceiveAck(ReadOnlySpan<byte> payload)
        {
            var reader = new ByteReader(payload);
            if (!reader.ReadUInt32(out var next) || !reader.ReadUInt64(out var mask) || reader.Remaining != 0) return false;
            var outstanding = unchecked(_nextSend - _sendBase);
            var advance = unchecked(next - _sendBase);
            // Old ACKs are harmless; future ACKs must never free unsent data.
            if (advance > outstanding) return true;
            var remaining = outstanding - advance;
            if (remaining < 64 && (mask >> (int)remaining) != 0) return false;
            uint frontier = 0;
            for (uint i = 0; i < outstanding; ++i)
            {
                var selected = i < advance || ((mask >> (int)(i - advance)) & 1) != 0;
                if (!selected) continue;
                _sent[unchecked(_sendBase + i) % WindowSize].Acknowledged = true;
                frontier = i + 1;
            }
            var known = unchecked(_ackFrontier - _sendBase);
            if (known > outstanding || frontier > known) _ackFrontier = unchecked(_sendBase + frontier);
            while (_sendBase != _nextSend && _sent[_sendBase % WindowSize].Acknowledged)
            {
                var packet = _sent[_sendBase % WindowSize];
                _sent[_sendBase % WindowSize] = null;
                _pool.Push(packet);
                ++_sendBase;
            }
            return true;
        }

        internal bool Receive(ReadOnlySpan<byte> payload)
        {
            var reader = new ByteReader(payload);
            if (!reader.ReadUInt32(out var sequence) || reader.Remaining == 0 || reader.Remaining > _dataCapacity) return false;

            var distance = unchecked(sequence - _receiveNext);
            if (distance >= WindowSize)
            {
                // Includes old duplicates. Re-ACK even after an earlier ACK was lost.
                _ackDirty = true;
                return true;
            }
            var packet = _received[sequence % WindowSize];
            if (packet.Present) { _ackDirty = true; return true; }
            reader.ReadBytes(reader.Remaining, out var data);
            data.CopyTo(packet.Buffer);
            packet.Length = data.Length;
            packet.Present = true;

            while (_received[_receiveNext % WindowSize].Present)
            {
                packet = _received[_receiveNext % WindowSize];
                // Grow only for received bytes, never for an untrusted length prefix.
                var required = checked(_streamLength + packet.Length);
                if (_stream.Length < required)
                    Array.Resize(ref _stream, Math.Max(required, _stream.Length <= int.MaxValue / 2 ? _stream.Length * 2 : required));
                packet.Buffer.AsSpan(0, packet.Length).CopyTo(_stream.AsSpan(_streamLength));
                _streamLength = required;
                packet.Present = false;
                ++_receiveNext;
                // An acknowledged stream that cannot be parsed cannot be repaired.
                // Stop delivery rather than silently discard an application message.
                if (!Deliver())
                {
                    IsFaulted = true;
                    return false;
                }
            }
            _ackDirty = true;
            return true;
        }

        // Delivers every complete message at the start of the stream and keeps the incomplete rest.
        private bool Deliver()
        {
            var position = 0;
            while (true)
            {
                var available = _streamLength - position;
                var result = ReadPrefix(_stream.AsSpan(position, available), out var length, out var size);
                if (result < 0) return false;
                if (result == 0 || available - size < length) break;
                _receiver.OnReliable(_stream.AsSpan(position + size, length));
                position += size + length;
            }
            if (position != 0)
            {
                _stream.AsSpan(position, _streamLength - position).CopyTo(_stream);
                _streamLength -= position;
            }
            return true;
        }

        // 1: a complete prefix; 0: more bytes needed; -1: malformed, non-minimal or above int.MaxValue.
        private static int ReadPrefix(ReadOnlySpan<byte> data, out int length, out int size)
        {
            length = 0;
            size = 0;
            ulong value = 0;
            for (var i = 0; i < MaxPrefixSize; ++i)
            {
                if (i == data.Length) return 0;
                var b = data[i];
                value |= (ulong)(b & 0x7f) << (7 * i);
                if ((b & 0x80) != 0) continue;
                if ((b == 0 && i > 0) || value > int.MaxValue) return -1;
                length = (int)value;
                size = i + 1;
                return 1;
            }
            return -1;
        }

        private sealed class SentPacket
        {
            internal readonly byte[] Buffer;
            internal uint Sequence;
            internal int Length;
            internal double LastSent;
            internal bool Acknowledged;
            internal SentPacket(int size) { Buffer = new byte[size]; }
        }
        private sealed class ReceivedPacket
        {
            internal readonly byte[] Buffer;
            internal int Length;
            internal bool Present;
            internal ReceivedPacket(int size) { Buffer = new byte[size]; }
        }

        private readonly ITransport _transport;
        private readonly IPeerReceiver _receiver;
        private readonly int _datagramSize, _dataOffset, _dataCapacity;
        private readonly Queue<SentPacket> _pending = new Queue<SentPacket>();
        private readonly Stack<SentPacket> _pool = new Stack<SentPacket>();
        private readonly SentPacket[] _sent = new SentPacket[WindowSize];
        private readonly ReceivedPacket[] _received = new ReceivedPacket[WindowSize];
        private readonly byte[] _ackBuffer = new byte[16];
        private byte[] _stream = Array.Empty<byte>();
        private SentPacket _tail;
        // One past the highest sequence the peer has acknowledged; unacknowledged packets below it are missing.
        private uint _sendBase, _nextSend, _ackFrontier, _receiveNext;
        private int _streamLength;
        private bool _ackDirty;
    }
}
