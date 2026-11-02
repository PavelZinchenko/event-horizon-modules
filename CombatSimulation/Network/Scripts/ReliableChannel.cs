using System;
using System.Collections.Generic;

namespace CombatSimulation.Network
{
    // One ordered byte stream of length-prefixed messages, cut into numbered packets. Sequence numbers count packets.
    // A packet is cut from the unsent stream at its first send, to the space its datagram has left, and resent unchanged.
    // So messages queued between two flushes share a packet, and the unsent stream keeps growing while the window is full.
    // Sequence arithmetic uses unsigned distances. The live window never spans half the sequence space.
    internal sealed class ReliableChannel
    {
        internal const int WindowSize = 64;
        private const int MaxPrefixSize = 5;
        // A packet that cannot take all unsent bytes is cut only if it gets at least this many, or it starts a datagram.
        private const int MinCut = 64;

        internal ReliableChannel(IPeerReceiver receiver, int datagramSize, uint initialSequence)
        {
            _receiver = receiver;
            DataCapacity = PacketFormat.MaxPayload(datagramSize - PacketFormat.HeaderSize) - PacketFormat.SequenceSize;
            _sendBase = _nextSend = _ackFrontier = _receiveNext = initialSequence;
            for (var i = 0; i < WindowSize; ++i) _received[i] = new ReceivedPacket(DataCapacity);
        }

        // The most stream bytes one packet carries: a datagram with only this section.
        internal int DataCapacity { get; }
        internal int PendingPackets => (int)unchecked(_nextSend - _sendBase) + (_unsentLength + DataCapacity - 1) / DataCapacity;
        internal bool IsFaulted { get; private set; }
        internal bool AckDue => _ackDirty;

        internal void Queue(ReadOnlySpan<byte> message)
        {
            Span<byte> prefix = stackalloc byte[MaxPrefixSize];
            var writer = new ByteWriter(prefix);
            writer.WriteVarUInt((uint)message.Length);
            if (message.Length > int.MaxValue - writer.Position - _unsentLength)
                throw new InvalidOperationException("Reliable queue exhausted.");
            Reserve(writer.Position + message.Length);
            var end = _unsentStart + _unsentLength;
            prefix.Slice(0, writer.Position).CopyTo(_unsent.AsSpan(end));
            message.CopyTo(_unsent.AsSpan(end + writer.Position));
            _unsentLength += writer.Position + message.Length;
        }

        private void Reserve(int count)
        {
            if (_unsent.Length - _unsentStart - _unsentLength >= count) return;
            var required = _unsentLength + count;
            var target = _unsent;
            if (target.Length < required)
                target = new byte[Math.Max(required, _unsent.Length <= int.MaxValue / 2 ? _unsent.Length * 2 : required)];
            _unsent.AsSpan(_unsentStart, _unsentLength).CopyTo(target);
            _unsent = target;
            _unsentStart = 0;
        }

        // Writes due resends, then new packets, into the datagram as far as they fit. Written packets count as sent
        // until Sent(false) takes them back. True: more is due; send the datagram and call again with an empty one.
        internal bool Write(OutgoingDatagram datagram, double now, double resendSeconds, double fastResendSeconds)
        {
            if (IsFaulted) return false;
            var outstanding = unchecked(_nextSend - _sendBase);
            var missing = unchecked(_ackFrontier - _sendBase);
            if (missing > outstanding) missing = 0;
            for (uint i = 0; i < outstanding; ++i)
            {
                var packet = _sent[unchecked(_sendBase + i) % WindowSize];
                // The peer has acknowledged a later packet, so this one is probably lost.
                var wait = i < missing ? fastResendSeconds : resendSeconds;
                if (packet.Acknowledged || now - packet.LastSent < wait) continue;
                if (!datagram.Fits(PacketFormat.SequenceSize + packet.Length)) return true;
                WritePacket(datagram, packet);
                packet.PreviousSent = packet.LastSent;
                packet.LastSent = now;
                _resent.Add(packet);
            }
            while (_unsentLength != 0 && unchecked(_nextSend - _sendBase) < WindowSize)
            {
                var count = Math.Min(_unsentLength, PacketFormat.MaxPayload(datagram.Space) - PacketFormat.SequenceSize);
                if (datagram.Sections != 0 && (count <= 0 || (count < _unsentLength && count < MinCut))) return true;
                if (!_cutting) { _cutting = true; _firstCut = _nextSend; }
                var packet = _pool.Count == 0 ? new SentPacket(DataCapacity) : _pool.Pop();
                _unsent.AsSpan(_unsentStart, count).CopyTo(packet.Buffer);
                _unsentStart += count;
                _unsentLength -= count;
                packet.Sequence = _nextSend;
                packet.Length = count;
                packet.LastSent = now;
                packet.Acknowledged = false;
                _sent[_nextSend % WindowSize] = packet;
                ++_nextSend;
                WritePacket(datagram, packet);
            }
            return false;
        }

        private static void WritePacket(OutgoingDatagram datagram, SentPacket packet)
        {
            var writer = new ByteWriter(datagram.Add(PacketKind.Reliable, PacketFormat.SequenceSize + packet.Length, true));
            writer.WriteUInt32(packet.Sequence);
            writer.WriteBytes(packet.Buffer.AsSpan(0, packet.Length));
        }

        // After the datagram from Write: false (local backpressure or held) restores resend times and returns new
        // packets to the unsent stream, so a sequence is assigned only to bytes that left.
        internal void Sent(bool sent)
        {
            if (!sent)
            {
                foreach (var packet in _resent) packet.LastSent = packet.PreviousSent;
                if (_cutting)
                    while (_nextSend != _firstCut)
                    {
                        --_nextSend;
                        var packet = _sent[_nextSend % WindowSize];
                        _sent[_nextSend % WindowSize] = null;
                        _unsentStart -= packet.Length;
                        _unsentLength += packet.Length;
                        _pool.Push(packet);
                    }
            }
            _resent.Clear();
            _cutting = false;
        }

        internal int AckSize()
        {
            // Bit 0 is always clear: _receiveNext itself is never buffered.
            _ackMask = 0;
            for (var i = 1; i < WindowSize; ++i)
                if (_received[unchecked(_receiveNext + (uint)i) % WindowSize].Present) _ackMask |= 1UL << (i - 1);
            return 4 + ByteWriter.VarUIntSize(_ackMask);
        }

        // Call AckSize first; the span has its size.
        internal void WriteAck(Span<byte> payload)
        {
            var writer = new ByteWriter(payload);
            writer.WriteUInt32(_receiveNext);
            writer.WriteVarUInt(_ackMask);
        }

        internal void AckSent() => _ackDirty = false;

        internal bool ReceiveAck(ReadOnlySpan<byte> payload)
        {
            var reader = new ByteReader(payload);
            if (!reader.ReadUInt32(out var next) || !reader.ReadVarUInt(out var later) || reader.Remaining != 0 ||
                (later >> (WindowSize - 1)) != 0) return false;
            var mask = later << 1;
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
            if (!reader.ReadUInt32(out var sequence) || reader.Remaining == 0 || reader.Remaining > DataCapacity) return false;

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
            internal double LastSent, PreviousSent;
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

        private readonly IPeerReceiver _receiver;
        private readonly Stack<SentPacket> _pool = new Stack<SentPacket>();
        private readonly List<SentPacket> _resent = new List<SentPacket>(WindowSize);
        private readonly SentPacket[] _sent = new SentPacket[WindowSize];
        private readonly ReceivedPacket[] _received = new ReceivedPacket[WindowSize];
        private byte[] _stream = Array.Empty<byte>();
        // Queued stream bytes without a sequence yet.
        private byte[] _unsent = Array.Empty<byte>();
        private int _unsentStart, _unsentLength;
        // One past the highest sequence the peer has acknowledged; unacknowledged packets below it are missing.
        private uint _sendBase, _nextSend, _ackFrontier, _receiveNext, _firstCut;
        private int _streamLength;
        private ulong _ackMask;
        private bool _ackDirty, _cutting;
    }
}
