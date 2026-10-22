using System;
using System.Collections.Generic;

namespace CombatSimulation.Network
{
    // Sequence arithmetic uses unsigned distances. The live window never spans half the sequence space.
    internal sealed class ReliableChannel
    {
        internal const int WindowSize = 64;

        internal ReliableChannel(ITransport transport, IPeerReceiver receiver, int datagramSize, uint initialSequence)
        {
            _transport = transport;
            _receiver = receiver;
            _datagramSize = datagramSize;
            _fragmentSize = datagramSize - PacketFormat.HeaderSize - PacketFormat.ReliableHeaderSize;
            _sendBase = _nextSend = _nextQueued = _receiveNext = initialSequence;
            for (var i = 0; i < WindowSize; ++i) _received[i] = new ReceivedPacket(_fragmentSize);
        }

        internal int PendingPackets => _pending.Count + (int)unchecked(_nextSend - _sendBase);
        internal bool IsFaulted { get; private set; }

        internal void Queue(ReadOnlySpan<byte> message)
        {
            var count = Math.Max(1, (int)(((long)message.Length + _fragmentSize - 1) / _fragmentSize));
            if ((long)PendingPackets + count >= int.MaxValue)
                throw new InvalidOperationException("Reliable sequence space exhausted.");
            var first = _nextQueued;
            for (var i = 0; i < count; ++i)
            {
                var packet = _pool.Count == 0 ? new SentPacket(_datagramSize) : _pool.Pop();
                var offset = (int)((long)i * _fragmentSize);
                var length = Math.Min(_fragmentSize, message.Length - offset);
                packet.Sequence = _nextQueued++;
                packet.Length = PacketFormat.HeaderSize + PacketFormat.ReliableHeaderSize + length;
                packet.Acknowledged = false;
                var writer = PacketFormat.Begin(packet.Buffer, PacketKind.Reliable, packet.Length - PacketFormat.HeaderSize);
                writer.WriteUInt32(packet.Sequence);
                writer.WriteUInt32(first);
                writer.WriteUInt32((uint)i);
                writer.WriteUInt32((uint)count);
                writer.WriteUInt32((uint)message.Length);
                writer.WriteBytes(message.Slice(offset, length));
                _pending.Enqueue(packet);
            }
        }

        internal void Flush(double now, double resendSeconds)
        {
            if (IsFaulted) return;
            for (var sequence = _sendBase; sequence != _nextSend; ++sequence)
            {
                var packet = _sent[sequence % WindowSize];
                if (!packet.Acknowledged && now - packet.LastSent >= resendSeconds &&
                    _transport.TrySend(packet.Buffer.AsSpan(0, packet.Length))) packet.LastSent = now;
            }
            while (_pending.Count != 0 && unchecked(_nextSend - _sendBase) < WindowSize)
            {
                var packet = _pending.Peek();
                if (!_transport.TrySend(packet.Buffer.AsSpan(0, packet.Length))) break;
                _pending.Dequeue();
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
            for (uint i = 0; i < outstanding; ++i)
            {
                var selected = i < advance || ((mask >> (int)(i - advance)) & 1) != 0;
                if (selected) _sent[unchecked(_sendBase + i) % WindowSize].Acknowledged = true;
            }
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
            if (!reader.ReadUInt32(out var sequence) || !reader.ReadUInt32(out var first) ||
                !reader.ReadUInt32(out var index) || !reader.ReadUInt32(out var count) ||
                !reader.ReadUInt32(out var total) || total > int.MaxValue || count == 0 || index >= count ||
                unchecked(first + index) != sequence) return false;
            var expectedCount = Math.Max(1L, ((long)total + _fragmentSize - 1) / _fragmentSize);
            var offset = (long)index * _fragmentSize;
            if (count != expectedCount || offset > total || reader.Remaining != Math.Min(_fragmentSize, total - offset)) return false;

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
            packet.First = first;
            packet.Index = index;
            packet.Count = count;
            packet.Total = (int)total;
            packet.Present = true;

            while (_received[_receiveNext % WindowSize].Present)
            {
                packet = _received[_receiveNext % WindowSize];
                // Individually valid but contradictory fragments cannot form a reliable stream.
                // Stop delivery rather than silently discard an acknowledged application message.
                if ((_assemblyIndex == 0 && packet.Index != 0) ||
                    (_assemblyIndex != 0 && (packet.First != _assemblyFirst || packet.Total != _assemblyTotal)) ||
                    packet.Index != _assemblyIndex)
                {
                    IsFaulted = true;
                    return false;
                }
                if (_assemblyIndex == 0)
                {
                    _assemblyFirst = packet.First;
                    _assemblyTotal = packet.Total;
                    _assemblyLength = 0;
                }
                // Grow only for received bytes, not for an untrusted advertised total.
                var required = checked(_assemblyLength + packet.Length);
                if (_assembly.Length < required) Array.Resize(ref _assembly, Math.Max(required, _assembly.Length <= int.MaxValue / 2 ? _assembly.Length * 2 : required));
                packet.Buffer.AsSpan(0, packet.Length).CopyTo(_assembly.AsSpan(_assemblyLength));
                _assemblyLength = required;
                ++_assemblyIndex;
                ++_receiveNext;
                packet.Present = false;
                if (_assemblyIndex == packet.Count)
                {
                    _assemblyIndex = 0;
                    _receiver.OnReliable(_assembly.AsSpan(0, _assemblyLength));
                }
            }
            _ackDirty = true;
            return true;
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
            internal uint First, Index, Count;
            internal int Length, Total;
            internal bool Present;
            internal ReceivedPacket(int size) { Buffer = new byte[size]; }
        }

        private readonly ITransport _transport;
        private readonly IPeerReceiver _receiver;
        private readonly int _datagramSize, _fragmentSize;
        private readonly Queue<SentPacket> _pending = new Queue<SentPacket>();
        private readonly Stack<SentPacket> _pool = new Stack<SentPacket>();
        private readonly SentPacket[] _sent = new SentPacket[WindowSize];
        private readonly ReceivedPacket[] _received = new ReceivedPacket[WindowSize];
        private readonly byte[] _ackBuffer = new byte[16];
        private byte[] _assembly = Array.Empty<byte>();
        private uint _sendBase, _nextSend, _nextQueued, _receiveNext, _assemblyFirst, _assemblyIndex;
        private int _assemblyTotal, _assemblyLength;
        private bool _ackDirty;
    }
}
