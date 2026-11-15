using System;

namespace CombatSimulation.Network
{
    // Bits are packed least significant first within each byte, and values least significant bit first.
    // VarUInt is the Utilites EliasGammaEncoder code with section size 4: 0 is a single 0 bit; any other value is
    // n 1 bits, a 0 bit, then the value in n 4-bit sections. Failed operations leave BitPosition unchanged.
    public ref struct BitWriter
    {
        private readonly Span<byte> _buffer;
        public int BitPosition { get; private set; }
        public int ByteLength => (BitPosition + 7) >> 3;
        public int RemainingBits => _buffer.Length * 8 - BitPosition;
        public BitWriter(Span<byte> buffer) { _buffer = buffer; BitPosition = 0; }

        public static int VarUIntBits(ulong value)
        {
            if (value == 0) return 1;
            var sections = Sections(value);
            return sections + 1 + 4 * sections;
        }

        public bool WriteBits(ulong value, int count)
        {
            if (count < 0 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
            if (RemainingBits < count) return false;
            for (var i = 0; i < count; ++i) Put(((value >> i) & 1) != 0);
            return true;
        }

        public bool WriteBool(bool value) => WriteBits(value ? 1UL : 0UL, 1);

        public bool WriteVarUInt(ulong value)
        {
            if (RemainingBits < VarUIntBits(value)) return false;
            if (value == 0) { Put(false); return true; }
            var sections = Sections(value);
            for (var i = 0; i < sections; ++i) Put(true);
            Put(false);
            for (var i = 0; i < sections * 4; ++i) Put(((value >> i) & 1) != 0);
            return true;
        }

        // Zeroes the unused bits of the last byte and returns the byte length. Packet hashes then never see stale bits.
        public int Finish()
        {
            while ((BitPosition & 7) != 0) Put(false);
            return ByteLength;
        }

        private void Put(bool bit)
        {
            ref var target = ref _buffer[BitPosition >> 3];
            var mask = (byte)(1 << (BitPosition & 7));
            target = bit ? (byte)(target | mask) : (byte)(target & ~mask);
            ++BitPosition;
        }

        internal static int Sections(ulong value)
        {
            var bits = 0;
            while (value != 0) { ++bits; value >>= 1; }
            return (bits + 3) / 4;
        }
    }

    public ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        public int BitPosition { get; private set; }
        public int RemainingBits => _buffer.Length * 8 - BitPosition;
        public BitReader(ReadOnlySpan<byte> buffer) { _buffer = buffer; BitPosition = 0; }

        public bool ReadBits(int count, out ulong value)
        {
            if (count < 0 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
            value = 0;
            if (RemainingBits < count) return false;
            for (var i = 0; i < count; ++i) if (Take()) value |= 1UL << i;
            return true;
        }

        public bool ReadBool(out bool value)
        {
            var ok = ReadBits(1, out var bit);
            value = bit != 0;
            return ok;
        }

        // Rejects more than 16 sections (over 64 bits) and non-minimal codes, so every value has one encoding.
        public bool ReadVarUInt(out ulong value)
        {
            value = 0;
            var start = BitPosition;
            var sections = 0;
            while (true)
            {
                if (RemainingBits < 1 || sections > 16) { BitPosition = start; return false; }
                if (!Take()) break;
                ++sections;
            }
            if (sections > 16 || !ReadBits(sections * 4, out value) ||
                (sections > 0 && BitWriter.Sections(value) != sections)) { BitPosition = start; value = 0; return false; }
            return true;
        }

        // True when fewer than eight bits remain and all of them are zero: the writer's padding.
        public bool AtPadding()
        {
            if (RemainingBits >= 8) return false;
            for (var i = BitPosition; i < _buffer.Length * 8; ++i)
                if ((_buffer[i >> 3] & (1 << (i & 7))) != 0) return false;
            return true;
        }

        private bool Take()
        {
            var bit = (_buffer[BitPosition >> 3] & (1 << (BitPosition & 7))) != 0;
            ++BitPosition;
            return bit;
        }
    }
}
