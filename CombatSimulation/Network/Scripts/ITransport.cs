using System;

namespace CombatSimulation.Network
{
    // One connected peer, datagrams only. The caller owns the transport's lifetime.
    public interface ITransport
    {
        // Consume or copy before returning. False means local backpressure, not network loss.
        bool TrySend(ReadOnlySpan<byte> datagram);

        // Consume one whole datagram. Report its ORIGINAL length even if the buffer is too small.
        // False means no packet is available. Never retain the supplied span.
        bool TryReceive(Span<byte> buffer, out int length);
    }

    // A transport that its loop pumps: Update receives before the connection's update, Flush sends after it.
    // All calls come from one thread at a time.
    public interface IPumpedTransport : ITransport
    {
        void Update();
        void Flush();
        // The link is gone for good: the peer left, or the Relay server dropped the allocation.
        bool IsClosed { get; }
    }

    public interface IPeerReceiver
    {
        // Spans are valid only during the callback. Callbacks must not reenter PeerConnection.
        // One input section's payload. Runtime owns its encoding, change detection and redundancy.
        void OnInput(ReadOnlySpan<byte> payload);
        void OnReliable(ReadOnlySpan<byte> payload);
        void OnUnreliable(ReadOnlySpan<byte> payload);
    }
}
