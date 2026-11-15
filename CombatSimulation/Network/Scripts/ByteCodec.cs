using System;
using System.Buffers.Binary;

namespace CombatSimulation.Network
{
    // Failed operations leave Position unchanged. No native-endian wire values.
    public ref struct ByteWriter
    {
        private readonly Span<byte> _buffer;
        public int Position { get; private set; }
        public int Remaining => _buffer.Length - Position;
        public ByteWriter(Span<byte> buffer) { _buffer = buffer; Position = 0; }
        public bool WriteByte(byte value)
        {
            if (Remaining < 1) return false;
            _buffer[Position++] = value;
            return true;
        }
        public bool WriteUInt16(ushort value)
        {
            if (Remaining < 2) return false;
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(Position), value);
            Position += 2;
            return true;
        }
        public bool WriteUInt32(uint value)
        {
            if (Remaining < 4) return false;
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(Position), value);
            Position += 4;
            return true;
        }
        public bool WriteUInt64(ulong value)
        {
            if (Remaining < 8) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(Position), value);
            Position += 8;
            return true;
        }
        public bool WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));
        public bool WriteSingle(float value) => WriteUInt32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        public bool WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));
        public bool WriteBytes(ReadOnlySpan<byte> value)
        {
            if (Remaining < value.Length) return false;
            value.CopyTo(_buffer.Slice(Position));
            Position += value.Length;
            return true;
        }

        // Unsigned LEB128: seven bits per byte, low group first; every byte but the last has its high bit set.
        public static int VarUIntSize(ulong value)
        {
            var size = 1;
            while (value >= 0x80) { value >>= 7; ++size; }
            return size;
        }
        public bool WriteVarUInt(ulong value)
        {
            if (Remaining < VarUIntSize(value)) return false;
            while (value >= 0x80) { _buffer[Position++] = (byte)(value | 0x80); value >>= 7; }
            _buffer[Position++] = (byte)value;
            return true;
        }
    }

    public ref struct ByteReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        public int Position { get; private set; }
        public int Remaining => _buffer.Length - Position;
        public ByteReader(ReadOnlySpan<byte> buffer) { _buffer = buffer; Position = 0; }
        public bool ReadByte(out byte value)
        {
            value = 0;
            if (Remaining < 1) return false;
            value = _buffer[Position++];
            return true;
        }
        public bool ReadUInt16(out ushort value)
        {
            value = 0;
            if (Remaining < 2) return false;
            value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(Position));
            Position += 2;
            return true;
        }
        public bool ReadUInt32(out uint value)
        {
            value = 0;
            if (Remaining < 4) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(Position));
            Position += 4;
            return true;
        }
        public bool ReadUInt64(out ulong value)
        {
            value = 0;
            if (Remaining < 8) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(Position));
            Position += 8;
            return true;
        }
        public bool ReadInt64(out long value)
        {
            var ok = ReadUInt64(out var bits);
            value = unchecked((long)bits);
            return ok;
        }
        public bool ReadSingle(out float value)
        {
            var ok = ReadUInt32(out var bits);
            value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            return ok;
        }
        public bool ReadDouble(out double value)
        {
            var ok = ReadInt64(out var bits);
            value = BitConverter.Int64BitsToDouble(bits);
            return ok;
        }
        public bool ReadBytes(int length, out ReadOnlySpan<byte> value)
        {
            value = default;
            if (length < 0 || length > Remaining) return false;
            value = _buffer.Slice(Position, length);
            Position += length;
            return true;
        }

        // Rejects truncated, over-64-bit and non-minimal codes, so every value has one encoding.
        public bool ReadVarUInt(out ulong value)
        {
            value = 0;
            ulong result = 0;
            for (var i = 0; i < 10 && Position + i < _buffer.Length; ++i)
            {
                var b = _buffer[Position + i];
                if (i == 9 && b > 1) return false;
                result |= (ulong)(b & 0x7f) << (7 * i);
                if ((b & 0x80) != 0) continue;
                if (b == 0 && i > 0) return false;
                value = result;
                Position += i + 1;
                return true;
            }
            return false;
        }
    }
}
