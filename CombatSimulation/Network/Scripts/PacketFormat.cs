using System;

namespace CombatSimulation.Network
{
    internal enum PacketKind : byte { Input = 1, Reliable, Ack, Probe, ProbeReply, Unreliable }

    // Version 3: one version byte, then one or more sections to the end of the datagram.
    // A section is a LEB128 header (payload length * 8 + kind), then the payload.
    internal static class PacketFormat
    {
        internal const byte Version = 3;
        internal const int HeaderSize = 1;
        internal const int SequenceSize = 4;
        internal const int KindBits = 3;
        private const ulong KindMask = (1 << KindBits) - 1;

        internal static int SectionSize(int payloadLength) =>
            ByteWriter.VarUIntSize((ulong)payloadLength << KindBits) + payloadLength;

        // The largest payload whose section fits in space bytes, or -1.
        internal static int MaxPayload(int space)
        {
            for (var header = 1; header <= 3; ++header)
            {
                var payload = space - header;
                if (payload >= 0 && ByteWriter.VarUIntSize((ulong)payload << KindBits) <= header) return payload;
            }
            return -1;
        }

        // Framing only: the version, at least one section, and every section complete with a known kind.
        internal static bool Validate(ReadOnlySpan<byte> datagram)
        {
            if (datagram.Length <= HeaderSize || datagram[0] != Version) return false;
            var reader = Sections(datagram);
            while (reader.Remaining != 0)
                if (!ReadSection(ref reader, out _, out _)) return false;
            return true;
        }

        internal static ByteReader Sections(ReadOnlySpan<byte> datagram) => new ByteReader(datagram.Slice(HeaderSize));

        internal static bool ReadSection(ref ByteReader reader, out PacketKind kind, out ReadOnlySpan<byte> payload)
        {
            kind = default;
            payload = default;
            var start = reader;
            if (!reader.ReadVarUInt(out var header)) return false;
            var value = header & KindMask;
            var length = header >> KindBits;
            if (value < 1 || value > (ulong)PacketKind.Unreliable || length > (ulong)reader.Remaining)
            {
                reader = start;
                return false;
            }
            kind = (PacketKind)value;
            return reader.ReadBytes((int)length, out payload);
        }
    }

    // The datagram being built. Sections are appended until one does not fit; the owner sends and clears it.
    internal sealed class OutgoingDatagram
    {
        internal OutgoingDatagram(int size)
        {
            _buffer = new byte[size];
            Clear();
        }

        internal int Length { get; private set; }
        internal int Space => _buffer.Length - Length;
        internal int Sections { get; private set; }
        // Sections other than acknowledgements and clock probes.
        internal int DataSections { get; private set; }
        internal ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, Length);

        internal bool Fits(int payloadLength) => PacketFormat.SectionSize(payloadLength) <= Space;

        // Writes the section header; the caller fills the returned payload span.
        internal Span<byte> Add(PacketKind kind, int payloadLength, bool isData)
        {
            if (!Fits(payloadLength)) throw new InvalidOperationException("The section does not fit in the datagram.");
            var writer = new ByteWriter(_buffer.AsSpan(Length));
            writer.WriteVarUInt(((ulong)payloadLength << PacketFormat.KindBits) | (byte)kind);
            Length += writer.Position;
            var payload = _buffer.AsSpan(Length, payloadLength);
            Length += payloadLength;
            ++Sections;
            if (isData) ++DataSections;
            return payload;
        }

        internal void Clear()
        {
            _buffer[0] = PacketFormat.Version;
            Length = PacketFormat.HeaderSize;
            Sections = DataSections = 0;
        }

        private readonly byte[] _buffer;
    }
}
