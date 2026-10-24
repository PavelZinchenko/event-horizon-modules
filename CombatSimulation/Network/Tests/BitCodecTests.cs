using System;
using NUnit.Framework;
using Session.Utils;

namespace CombatSimulation.Network.Tests
{
    public class BitCodecTests
    {
        private static readonly ulong[] Values = { 0, 1, 2, 15, 16, 255, 256, 359, 4096, 123456789, uint.MaxValue, ulong.MaxValue };

        [Test]
        public void VarUIntMatchesTheSessionEliasGammaEncoder()
        {
            var expected = new byte[256];
            var stream = new MemoryWriterStream(expected);
            using (var encoder = new EliasGammaEncoder(stream, 4))
                foreach (var value in Values) encoder.WriteUnsigned(value);
            var actual = new byte[256];
            var writer = new BitWriter(actual);
            var bits = 0;
            foreach (var value in Values) { Assert.IsTrue(writer.WriteVarUInt(value)); bits += BitWriter.VarUIntBits(value); }
            Assert.AreEqual(bits, writer.BitPosition);
            var length = writer.Finish();
            Assert.AreEqual(stream.Position, length);
            CollectionAssert.AreEqual(expected.AsSpan(0, length).ToArray(), actual.AsSpan(0, length).ToArray());
        }

        [Test]
        public void FixedAndVariableFieldsRoundTripAndPadWithZeros()
        {
            var bytes = new byte[64];
            for (var i = 0; i < bytes.Length; ++i) bytes[i] = 0xff; // Stale bytes must not leak into the packet.
            var writer = new BitWriter(bytes);
            Assert.IsTrue(writer.WriteBits(359, 9)); Assert.IsTrue(writer.WriteBits(127, 7));
            Assert.IsTrue(writer.WriteBool(false));
            foreach (var value in Values) Assert.IsTrue(writer.WriteVarUInt(value));
            Assert.IsTrue(writer.WriteBits(ulong.MaxValue, 64));
            var length = writer.Finish();
            var reader = new BitReader(bytes.AsSpan(0, length));
            Assert.IsTrue(reader.ReadBits(9, out var course)); Assert.AreEqual(359, course);
            Assert.IsTrue(reader.ReadBits(7, out var throttle)); Assert.AreEqual(127, throttle);
            Assert.IsTrue(reader.ReadBool(out var flag)); Assert.IsFalse(flag);
            foreach (var value in Values) { Assert.IsTrue(reader.ReadVarUInt(out var read)); Assert.AreEqual(value, read); }
            Assert.IsTrue(reader.ReadBits(64, out var all)); Assert.AreEqual(ulong.MaxValue, all);
            Assert.IsTrue(reader.AtPadding());
        }

        [Test]
        public void FailedOperationsLeavePositionUnchanged()
        {
            var bytes = new byte[2];
            var writer = new BitWriter(bytes);
            Assert.IsTrue(writer.WriteBits(1, 10));
            Assert.IsFalse(writer.WriteBits(0, 7));
            Assert.IsFalse(writer.WriteVarUInt(255)); // 11 bits
            Assert.AreEqual(10, writer.BitPosition);
            Assert.IsTrue(writer.WriteVarUInt(15)); // 6 bits
            Assert.AreEqual(16, writer.BitPosition);

            var reader = new BitReader(new byte[] { 0xff });
            Assert.IsFalse(reader.ReadVarUInt(out _)); // Unary prefix never ends.
            Assert.AreEqual(0, reader.BitPosition);
            Assert.IsFalse(reader.ReadBits(9, out _));
            Assert.AreEqual(0, reader.BitPosition);
            Assert.IsFalse(reader.AtPadding());
        }

        [Test]
        public void NonMinimalAndOversizedVarUIntsAreRejected()
        {
            var bytes = new byte[8];
            var writer = new BitWriter(bytes);
            writer.WriteBits(0b011, 3); writer.WriteBits(1, 8); // Two sections for the value 1.
            writer.Finish();
            var reader = new BitReader(bytes);
            Assert.IsFalse(reader.ReadVarUInt(out _));
            Assert.AreEqual(0, reader.BitPosition);

            var oversized = new byte[16];
            for (var i = 0; i < 3; ++i) oversized[i] = 0xff; // 17 or more sections.
            Assert.IsFalse(new BitReader(oversized).ReadVarUInt(out _));
        }
    }
}
