using System;

namespace CombatSimulation.Network
{
    // Single thread, one match, one remote peer. Recreate with a fresh transport for another match.
    public sealed class PeerConnection
    {
        public const int DefaultDatagramSize = 1200;

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
            _sendBuffer = new byte[maxDatagramSize];
            Clock = new TickClock(isClockReference);
            _reliable = new ReliableChannel(transport, receiver, maxDatagramSize, initialSequence);
            _inputs = new InputChannel(transport, maxDatagramSize);
        }

        public TickClock Clock { get; }
        public int MaxDatagramSize { get; }
        public int MaxInputPayload => _inputs.MaxPayload;
        public int MaxUnreliablePayload => MaxDatagramSize - PacketFormat.HeaderSize;
        public int PendingReliablePackets => _reliable.PendingPackets;
        public long InvalidPackets { get; private set; }
        public bool IsFaulted => _reliable.IsFaulted;

        // Queue methods copy payloads; callers may immediately reuse their buffers.
        public void QueueReliable(ReadOnlySpan<byte> payload) { EnsureHealthy(); _reliable.Queue(payload); }
        public void QueueInput(ulong entityKey, long originalTick, ReadOnlySpan<byte> payload)
        {
            EnsureHealthy();
            _inputs.Queue(entityKey, originalTick, payload);
        }
        public void SendInputs(long currentTick) { EnsureHealthy(); _inputs.Send(currentTick); }

        public bool TrySendUnreliable(ReadOnlySpan<byte> payload)
        {
            EnsureHealthy();
            if (payload.Length > MaxUnreliablePayload) throw new ArgumentOutOfRangeException(nameof(payload));
            var writer = PacketFormat.Begin(_sendBuffer, PacketKind.Unreliable, payload.Length);
            writer.WriteBytes(payload);
            return _transport.TrySend(_sendBuffer.AsSpan(0, writer.Position));
        }

        // Call regularly even when the battle has no inputs or events to send.
        public void Update(double now)
        {
            EnsureHealthy();
            if (!TickClock.Finite(now) || now < _lastUpdate) throw new ArgumentOutOfRangeException(nameof(now));
            _lastUpdate = now;
            while (_transport.TryReceive(_receiveBuffer, out var length))
            {
                if (length < 0 || length > _receiveBuffer.Length ||
                    !PacketFormat.Read(_receiveBuffer.AsSpan(0, Math.Max(0, length)), out var kind, out var payload))
                {
                    ++InvalidPackets;
                    continue;
                }
                var valid = true;
                switch (kind)
                {
                    case PacketKind.Input: valid = InputChannel.Receive(payload, _receiver); break;
                    case PacketKind.Reliable: valid = _reliable.Receive(payload); break;
                    case PacketKind.Ack: valid = _reliable.ReceiveAck(payload); break;
                    case PacketKind.Probe: valid = ReceiveProbe(payload, now); break;
                    case PacketKind.ProbeReply: valid = ReceiveProbeReply(payload, now); break;
                    case PacketKind.Unreliable: _receiver.OnUnreliable(payload); break;
                }
                if (!valid) ++InvalidPackets;
                if (IsFaulted) return;
            }
            _reliable.Flush(now, Clock.ResendSeconds);
            if (now >= _nextProbeTime) SendProbe(now);
            if (Clock.IsStarted) Clock.Advance(now);
        }

        private void SendProbe(double now)
        {
            var id = _nextProbe;
            var writer = PacketFormat.Begin(_sendBuffer, PacketKind.Probe, 12);
            writer.WriteUInt32(id);
            writer.WriteDouble(now);
            if (!_transport.TrySend(_sendBuffer.AsSpan(0, writer.Position))) return;
            _probes[id % 16] = new Probe { Id = id, Sent = now, Pending = true };
            ++_nextProbe;
            _nextProbeTime = now + TickClock.ProbeInterval;
        }

        private bool ReceiveProbe(ReadOnlySpan<byte> payload, double now)
        {
            var reader = new ByteReader(payload);
            if (!reader.ReadUInt32(out var id) || !reader.ReadDouble(out var sent) ||
                reader.Remaining != 0 || !TickClock.Finite(sent)) return false;
            var writer = PacketFormat.Begin(_sendBuffer, PacketKind.ProbeReply, 28);
            writer.WriteUInt32(id);
            writer.WriteDouble(sent);
            writer.WriteDouble(now);
            writer.WriteDouble(now);
            _transport.TrySend(_sendBuffer.AsSpan(0, writer.Position));
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
        private readonly ITransport _transport;
        private readonly IPeerReceiver _receiver;
        private readonly ReliableChannel _reliable;
        private readonly InputChannel _inputs;
        private readonly byte[] _receiveBuffer, _sendBuffer;
        private readonly Probe[] _probes = new Probe[16];
        private uint _nextProbe;
        private double _nextProbeTime = double.NegativeInfinity, _lastUpdate = double.NegativeInfinity;
    }
}
