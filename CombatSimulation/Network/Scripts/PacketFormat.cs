using System;

namespace CombatSimulation.Network
{
    internal enum PacketKind : byte { Input = 1, Reliable, Ack, Probe, ProbeReply, Unreliable }

    internal static class PacketFormat
    {
        internal const byte Version = 1;
        internal const int HeaderSize = 4;
        internal const int ReliableHeaderSize = 20;

        internal static ByteWriter Begin(Span<byte> buffer, PacketKind kind, int payloadLength)
        {
            var writer = new ByteWriter(buffer);
            writer.WriteByte(Version);
            writer.WriteByte((byte)kind);
            writer.WriteUInt16((ushort)payloadLength);
            return writer;
        }

        internal static bool Read(ReadOnlySpan<byte> packet, out PacketKind kind, out ReadOnlySpan<byte> payload)
        {
            kind = default;
            payload = default;
            var reader = new ByteReader(packet);
            if (!reader.ReadByte(out var version) || version != Version ||
                !reader.ReadByte(out var channel) || channel < 1 || channel > (byte)PacketKind.Unreliable ||
                !reader.ReadUInt16(out var length) || length != reader.Remaining) return false;
            kind = (PacketKind)channel;
            return reader.ReadBytes(length, out payload);
        }
    }
}
