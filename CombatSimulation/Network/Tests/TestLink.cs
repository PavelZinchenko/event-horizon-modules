using System;
using System.Collections.Generic;
using Utilites;

namespace CombatSimulation.Network.Tests
{
    // Test-only virtual link. Each direction has an independent seeded impairment stream.
    internal sealed class TestLink
    {
        internal double Now;
        internal readonly Endpoint A, B;
        internal TestLink(int seed = 1)
        {
            A = new Endpoint(this, seed);
            B = new Endpoint(this, seed + 1);
            A.Remote = B;
            B.Remote = A;
        }

        internal sealed class Endpoint : ITransport
        {
            internal Endpoint Remote;
            internal double Delay, Jitter, Loss, Duplication, Reordering;
            internal double BlackoutStart = -1, BlackoutEnd = -1;
            internal bool BlockSend;
            internal PacketKind DropKind;
            internal int DropCount;
            internal int LargestDatagram, ReliableTransmissions;
            internal long SentBytes;
            internal ulong Trace = 14695981039346656037UL;

            internal Endpoint(TestLink link, int seed) { _link = link; _random = new PcgRandom(seed); }

            public bool TrySend(ReadOnlySpan<byte> datagram)
            {
                if (BlockSend) return false;
                LargestDatagram = Math.Max(LargestDatagram, datagram.Length);
                SentBytes += datagram.Length;
                var kind = (PacketKind)datagram[1];
                if (kind == PacketKind.Reliable) ++ReliableTransmissions;
                if (kind == DropKind && DropCount > 0) { --DropCount; return true; }
                if ((_link.Now >= BlackoutStart && _link.Now < BlackoutEnd) || _random.NextDouble() < Loss) return true;
                Schedule(datagram);
                if (_random.NextDouble() < Duplication) Schedule(datagram);
                return true;
            }

            private void Schedule(ReadOnlySpan<byte> bytes)
            {
                var delay = Math.Max(0, Delay + (2 * _random.NextDouble() - 1) * Jitter);
                if (_random.NextDouble() < Reordering) delay += 0.1;
                Remote.Inject(bytes, _link.Now + delay);
            }

            internal void Inject(ReadOnlySpan<byte> bytes, double due)
            {
                var packet = _pool.Count == 0 ? new Packet() : _pool.Pop();
                if (packet.Bytes.Length < bytes.Length) Array.Resize(ref packet.Bytes, bytes.Length);
                bytes.CopyTo(packet.Bytes);
                packet.Length = bytes.Length;
                packet.Due = due;
                packet.Order = _nextOrder++;
                _packets.Add(packet);
            }

            public bool TryReceive(Span<byte> buffer, out int length)
            {
                length = 0;
                var selected = -1;
                for (var i = 0; i < _packets.Count; ++i)
                {
                    var candidate = _packets[i];
                    if (candidate.Due > _link.Now) continue;
                    if (selected < 0 || candidate.Due < _packets[selected].Due ||
                        (candidate.Due == _packets[selected].Due && candidate.Order < _packets[selected].Order)) selected = i;
                }
                if (selected < 0) return false;
                var packet = _packets[selected];
                _packets.RemoveAt(selected);
                length = packet.Length;
                packet.Bytes.AsSpan(0, Math.Min(length, buffer.Length)).CopyTo(buffer);
                for (var i = 0; i < length; ++i) Trace = unchecked((Trace ^ packet.Bytes[i]) * 1099511628211UL);
                _pool.Push(packet);
                return true;
            }

            private sealed class Packet
            {
                internal byte[] Bytes = Array.Empty<byte>();
                internal int Length;
                internal double Due;
                internal long Order;
            }
            private readonly TestLink _link;
            private readonly PcgRandom _random;
            private readonly List<Packet> _packets = new List<Packet>();
            private readonly Stack<Packet> _pool = new Stack<Packet>();
            private long _nextOrder;
        }
    }

    internal sealed class RecordingReceiver : IPeerReceiver
    {
        internal readonly List<byte[]> Reliable = new List<byte[]>();
        internal readonly List<byte[]> Unreliable = new List<byte[]>();
        internal readonly List<byte[]> Inputs = new List<byte[]>();
        public void OnReliable(ReadOnlySpan<byte> payload) => Reliable.Add(payload.ToArray());
        public void OnUnreliable(ReadOnlySpan<byte> payload) => Unreliable.Add(payload.ToArray());
        public void OnInput(ReadOnlySpan<byte> payload) => Inputs.Add(payload.ToArray());
    }

    internal sealed class CountingReceiver : IPeerReceiver
    {
        internal int ReliableCount, InputCount, UnreliableCount;
        internal long Bytes;
        public void OnReliable(ReadOnlySpan<byte> payload) { ++ReliableCount; Bytes += payload.Length; }
        public void OnUnreliable(ReadOnlySpan<byte> payload) { ++UnreliableCount; Bytes += payload.Length; }
        public void OnInput(ReadOnlySpan<byte> payload) { ++InputCount; Bytes += payload.Length; }
    }
}
