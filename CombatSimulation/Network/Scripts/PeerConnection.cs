using System;

namespace CombatSimulation.Network
{
    // Single thread, one match, one remote peer. Recreate with a fresh transport for another match.
    // Every section the peer sends between two flushes shares one datagram (packet format version 3).
    public sealed class PeerConnection
    {
        public const int DefaultDatagramSize = 1200;
        private const int ProbeSize = 12, ReplySize = 28;
        // Runtime sends data about every tick; 1.5 ticks also covers the follower's slowest clock rate.
        private const double HoldSeconds = 1.5 / TickClock.TicksPerSecond;

        public PeerConnection(ITransport transport, IPeerReceiver receiver, bool isClockReference,
            int maxDatagramSize = DefaultDatagramSize)
            : this(transport, receiver, isClockReference, maxDatagramSize, 0) { }

        internal PeerConnection(ITransport transport, IPeerReceiver receiver, bool isClockReference,
            int maxDatagramSize, uint initialSequence)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            if (maxDatagramSize < 64 || maxDatagramSize > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maxDatagramSize));
            MaxDatagramSize = maxDatagramSize;
            _receiveBuffer = new byte[maxDatagramSize];
            _outgoing = new OutgoingDatagram(maxDatagramSize);
            Clock = new TickClock(isClockReference);
            _reliable = new ReliableChannel(receiver, maxDatagramSize, initialSequence);
        }

        public TickClock Clock { get; }
        public int MaxDatagramSize { get; }
        public int MaxInputPayload => PacketFormat.MaxPayload(MaxDatagramSize - PacketFormat.HeaderSize);
        public int MaxUnreliablePayload => MaxInputPayload;
        public int PendingReliablePackets => _reliable.PendingPackets;
        // Malformed datagrams, and sections with invalid contents.
        public long InvalidPackets { get; private set; }
        public bool IsFaulted => _reliable.IsFaulted;

        // Queue methods copy payloads; callers may immediately reuse their buffers.
        public void QueueReliable(ReadOnlySpan<byte> payload) { EnsureHealthy(); _reliable.Queue(payload); }
        // Input and plain unreliable data: one section each, no delivery promise. The next Update sends them with the
        // acknowledgement and reliable data. A section that does not fit sends the datagram before it.
        // False means local backpressure: the datagram could not be sent, and the payload is not queued.
        public bool TrySendInput(ReadOnlySpan<byte> payload) => QueueSection(PacketKind.Input, payload);
        public bool TrySendUnreliable(ReadOnlySpan<byte> payload) => QueueSection(PacketKind.Unreliable, payload);

        private bool QueueSection(PacketKind kind, ReadOnlySpan<byte> payload)
        {
            EnsureHealthy();
            if (payload.Length > MaxInputPayload) throw new ArgumentOutOfRangeException(nameof(payload));
            if (!_outgoing.Fits(payload.Length))
            {
                // Between flushes the datagram holds only these sections, so nothing needs a commit.
                if (!_transport.TrySend(_outgoing.Bytes)) return false;
                _outgoing.Clear();
            }
            payload.CopyTo(_outgoing.Add(kind, payload.Length, true));
            return true;
        }

        // Call regularly even when the battle has no inputs or events to send.
        public void Update(double now)
        {
            EnsureHealthy();
            if (!TickClock.Finite(now) || now < _lastUpdate) throw new ArgumentOutOfRangeException(nameof(now));
            _lastUpdate = now;
            while (_transport.TryReceive(_receiveBuffer, out var length))
            {
                var datagram = _receiveBuffer.AsSpan(0, Math.Max(0, Math.Min(length, _receiveBuffer.Length)));
                // Framing is checked first, so a malformed datagram delivers none of its sections.
                if (length < 0 || length > _receiveBuffer.Length || !PacketFormat.Validate(datagram))
                {
                    ++InvalidPackets;
                    continue;
                }
                var sections = PacketFormat.Sections(datagram);
                while (PacketFormat.ReadSection(ref sections, out var kind, out var payload))
                {
                    var valid = true;
                    switch (kind)
                    {
                        case PacketKind.Input: _receiver.OnInput(payload); break;
                        case PacketKind.Reliable: valid = _reliable.Receive(payload); break;
                        case PacketKind.Ack: valid = _reliable.ReceiveAck(payload); break;
                        case PacketKind.Probe: valid = ReceiveProbe(payload, now); break;
                        case PacketKind.ProbeReply: valid = ReceiveProbeReply(payload, now); break;
                        case PacketKind.Unreliable: _receiver.OnUnreliable(payload); break;
                    }
                    if (!valid) ++InvalidPackets;
                    if (IsFaulted) return;
                }
            }
            Flush(now);
            if (Clock.IsStarted) Clock.Advance(now);
        }

        // Queued sections, then the probe reply, probe and acknowledgement, then reliable packets in the space left.
        // A datagram of only replies, probes and acknowledgements waits up to HoldSeconds for data to join.
        private void Flush(double now)
        {
            var probeDue = now >= _nextProbeTime;
            var ackSize = _reliable.AckDue ? _reliable.AckSize() : -1;
            var controlSize = (_replyPending ? PacketFormat.SectionSize(ReplySize) : 0) +
                (probeDue ? PacketFormat.SectionSize(ProbeSize) : 0) + (ackSize >= 0 ? PacketFormat.SectionSize(ackSize) : 0);
            if (controlSize > _outgoing.Space && !Send(now)) return;
            if (_replyPending)
            {
                var writer = new ByteWriter(_outgoing.Add(PacketKind.ProbeReply, ReplySize, false));
                writer.WriteUInt32(_reply.Id);
                writer.WriteDouble(_reply.Sent);
                writer.WriteDouble(_reply.Received);
                writer.WriteDouble(now);
                _replyWritten = true;
            }
            if (probeDue)
            {
                var writer = new ByteWriter(_outgoing.Add(PacketKind.Probe, ProbeSize, false));
                writer.WriteUInt32(_nextProbe);
                writer.WriteDouble(now);
                _probeWritten = true;
            }
            if (ackSize >= 0)
            {
                _reliable.WriteAck(_outgoing.Add(PacketKind.Ack, ackSize, false));
                _ackWritten = true;
            }
            while (_reliable.Write(_outgoing, now, Clock.ResendSeconds, Clock.FastResendSeconds))
                if (!Send(now)) return;
            if (_outgoing.Sections == 0) return;
            if (_outgoing.DataSections == 0 && !(now >= _heldSince + HoldSeconds))
            {
                if (double.IsPositiveInfinity(_heldSince)) _heldSince = now;
                _reliable.Sent(false);
                _replyWritten = _probeWritten = _ackWritten = false;
                _outgoing.Clear();
                return;
            }
            Send(now);
        }

        // A refused datagram is dropped; its reliable packets and control sections wait for the next flush.
        private bool Send(double now)
        {
            var sent = _transport.TrySend(_outgoing.Bytes);
            _reliable.Sent(sent);
            if (sent)
            {
                if (_ackWritten) _reliable.AckSent();
                if (_replyWritten) _replyPending = false;
                if (_probeWritten)
                {
                    _probes[_nextProbe % 16] = new Probe { Id = _nextProbe, Sent = now, Pending = true };
                    ++_nextProbe;
                    _nextProbeTime = now + TickClock.ProbeInterval;
                }
                _heldSince = double.PositiveInfinity;
            }
            _replyWritten = _probeWritten = _ackWritten = false;
            _outgoing.Clear();
            return sent;
        }

        // The reply is sent at the next flush, with its own send time; a newer probe replaces an unsent reply.
        private bool ReceiveProbe(ReadOnlySpan<byte> payload, double now)
        {
            var reader = new ByteReader(payload);
            if (!reader.ReadUInt32(out var id) || !reader.ReadDouble(out var sent) ||
                reader.Remaining != 0 || !TickClock.Finite(sent)) return false;
            _reply = new Reply { Id = id, Sent = sent, Received = now };
            _replyPending = true;
            return true;
        }

        private bool ReceiveProbeReply(ReadOnlySpan<byte> payload, double now)
        {
            var reader = new ByteReader(payload);
            if (!reader.ReadUInt32(out var id) || !reader.ReadDouble(out var t1) ||
                !reader.ReadDouble(out var t2) || !reader.ReadDouble(out var t3) || reader.Remaining != 0) return false;
            ref var probe = ref _probes[id % 16];
            // Expired and duplicate replies cannot change the estimate.
            if (!probe.Pending || probe.Id != id || probe.Sent != t1) return true;
            if (!Clock.Observe(t1, t2, t3, now)) return false;
            probe.Pending = false;
            return true;
        }

        private void EnsureHealthy()
        {
            if (IsFaulted) throw new InvalidOperationException("Contradictory reliable fragments; connection is faulted.");
        }

        private struct Probe { internal uint Id; internal double Sent; internal bool Pending; }
        private struct Reply { internal uint Id; internal double Sent, Received; }
        private readonly ITransport _transport;
        private readonly IPeerReceiver _receiver;
        private readonly ReliableChannel _reliable;
        private readonly OutgoingDatagram _outgoing;
        private readonly byte[] _receiveBuffer;
        private readonly Probe[] _probes = new Probe[16];
        private Reply _reply;
        private uint _nextProbe;
        private double _nextProbeTime = double.NegativeInfinity, _lastUpdate = double.NegativeInfinity;
        // The time a control-only datagram was first held; positive infinity when none is held.
        private double _heldSince = double.PositiveInfinity;
        private bool _replyPending, _replyWritten, _probeWritten, _ackWritten;
    }
}
