using System;
using NUnit.Framework;

namespace CombatSimulation.Network.Tests
{
    public class ByteCodecTests
    {
        [Test]
        public void IntegersHaveFixedLittleEndianBytes()
        {
            var bytes = new byte[15];
            var writer = new ByteWriter(bytes);
            Assert.IsTrue(writer.WriteByte(0xff));
            Assert.IsTrue(writer.WriteUInt16(0x1234));
            Assert.IsTrue(writer.WriteUInt32(0x89abcdef));
            Assert.IsTrue(writer.WriteUInt64(0x0123456789abcdef));
            CollectionAssert.AreEqual(new byte[] { 255, 0x34, 0x12, 0xef, 0xcd, 0xab, 0x89, 0xef, 0xcd, 0xab, 0x89, 0x67, 0x45, 0x23, 1 }, bytes);
            var reader = new ByteReader(bytes);
            Assert.IsTrue(reader.ReadByte(out var a)); Assert.AreEqual(255, a);
            Assert.IsTrue(reader.ReadUInt16(out var b)); Assert.AreEqual(0x1234, b);
            Assert.IsTrue(reader.ReadUInt32(out var c)); Assert.AreEqual(0x89abcdef, c);
            Assert.IsTrue(reader.ReadUInt64(out var d)); Assert.AreEqual(0x0123456789abcdef, d);
            Assert.AreEqual(0, reader.Remaining);
        }

        [TestCase(long.MinValue)]
        [TestCase(long.MaxValue)]
        [TestCase(-1L)]
        [TestCase(0L)]
        public void SignedValuesRoundTrip(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            var writer = new ByteWriter(bytes);
            Assert.IsTrue(writer.WriteInt64(value));
            var reader = new ByteReader(bytes);
            Assert.IsTrue(reader.ReadInt64(out var actual));
            Assert.AreEqual(value, actual);
        }

        [TestCase(0UL)]
        [TestCase(0x8000000000000000UL)]
        [TestCase(0x7ff0000000000000UL)]
        [TestCase(0x7ff123456789abcdUL)]
        [TestCase(ulong.MaxValue)]
        public void FloatingPointBitsRoundTripExactly(ulong bits)
        {
            Span<byte> bytes = stackalloc byte[12];
            var writer = new ByteWriter(bytes);
            writer.WriteDouble(BitConverter.Int64BitsToDouble(unchecked((long)bits)));
            writer.WriteSingle(BitConverter.Int32BitsToSingle(unchecked((int)bits)));
            var reader = new ByteReader(bytes);
            Assert.IsTrue(reader.ReadDouble(out var d));
            Assert.IsTrue(reader.ReadSingle(out var f));
            Assert.AreEqual(bits, unchecked((ulong)BitConverter.DoubleToInt64Bits(d)));
            Assert.AreEqual(unchecked((uint)bits), unchecked((uint)BitConverter.SingleToInt32Bits(f)));
        }

        [Test]
        public void FailedOperationsDoNotAdvanceOrOverwrite()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var writer = new ByteWriter(bytes);
            Assert.IsFalse(writer.WriteUInt32(100));
            Assert.IsFalse(writer.WriteBytes(new byte[4]));
            Assert.AreEqual(0, writer.Position);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
            var reader = new ByteReader(bytes);
            Assert.IsFalse(reader.ReadUInt64(out _));
            Assert.IsFalse(reader.ReadBytes(-1, out _));
            Assert.IsFalse(reader.ReadBytes(int.MaxValue, out _));
            Assert.AreEqual(0, reader.Position);
            Assert.IsTrue(reader.ReadBytes(3, out var result));
            Assert.AreEqual(3, result.Length);
            Assert.IsTrue(reader.ReadBytes(0, out _));
            Assert.IsFalse(reader.ReadByte(out _));
        }

        [TestCase(0UL, new byte[] { 0 })]
        [TestCase(127UL, new byte[] { 127 })]
        [TestCase(128UL, new byte[] { 0x80, 1 })]
        [TestCase(300UL, new byte[] { 0xac, 2 })]
        [TestCase(ulong.MaxValue, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 1 })]
        public void VarUIntUsesSevenBitGroupsLowFirst(ulong value, byte[] expected)
        {
            var bytes = new byte[10];
            var writer = new ByteWriter(bytes);
            Assert.AreEqual(expected.Length, ByteWriter.VarUIntSize(value));
            Assert.IsTrue(writer.WriteVarUInt(value));
            CollectionAssert.AreEqual(expected, bytes.AsSpan(0, writer.Position).ToArray());
            var reader = new ByteReader(expected);
            Assert.IsTrue(reader.ReadVarUInt(out var actual));
            Assert.AreEqual(value, actual);
            Assert.AreEqual(0, reader.Remaining);
            Assert.IsFalse(new ByteWriter(new byte[expected.Length - 1]).WriteVarUInt(value));
        }

        [TestCase(new byte[0])]
        [TestCase(new byte[] { 0x80 })]
        [TestCase(new byte[] { 0x80, 0 })]
        [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 2 })]
        [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x81, 0 })]
        public void VarUIntRejectsTruncatedOversizedAndNonMinimalCodes(byte[] bytes)
        {
            var reader = new ByteReader(bytes);
            Assert.IsFalse(reader.ReadVarUInt(out var value));
            Assert.AreEqual(0UL, value);
            Assert.AreEqual(0, reader.Position);
        }

        [Test]
        public void ReadersAndWritersAllocateNothing()
        {
            var bytes = new byte[32];
            CodecLoop(bytes);
            var before = GC.GetAllocatedBytesForCurrentThread();
            CodecLoop(bytes);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
        }

        private static void CodecLoop(byte[] bytes)
        {
            for (var i = 0; i < 1000; ++i)
            {
                var writer = new ByteWriter(bytes);
                writer.WriteInt64(i); writer.WriteSingle(i); writer.WriteDouble(i);
                var reader = new ByteReader(bytes);
                reader.ReadInt64(out _); reader.ReadSingle(out _); reader.ReadDouble(out _);
            }
        }
    }
}
