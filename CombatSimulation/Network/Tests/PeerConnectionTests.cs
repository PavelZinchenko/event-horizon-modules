using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CombatSimulation.Network.Tests
{
    public class PeerConnectionTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1176)]
        [TestCase(1177)]
        [TestCase(85000)]
        public void LoopbackDeliversCompleteMessagesIncludingMultipleWindows(int size)
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            var data = Payload(size, 3);
            a.QueueReliable(data);
            a.QueueReliable(new byte[] { 99 });
            Pump(link, a, b, 200);
            Assert.AreEqual(2, receiver.Reliable.Count);
            CollectionAssert.AreEqual(data, receiver.Reliable[0]);
            CollectionAssert.AreEqual(new byte[] { 99 }, receiver.Reliable[1]);
            Assert.AreEqual(0, a.PendingReliablePackets);
            Assert.LessOrEqual(link.A.LargestDatagram, 1200);
        }

        [TestCase(1)]
        [TestCase(17)]
        [TestCase(999)]
        public void LossDuplicationReorderingAndBlackoutRecoverExactlyOnceInBothDirections(int seed)
        {
            RunImpaired(seed, out var first);
            RunImpaired(seed, out var repeated);
            Assert.AreEqual(first, repeated, "The same seed must reproduce the packet trace.");
        }

        private static void RunImpaired(int seed, out ulong trace)
        {
            var link = new TestLink(seed);
            foreach (var endpoint in new[] { link.A, link.B })
            {
                endpoint.Delay = 0.06; endpoint.Jitter = 0.02; endpoint.Loss = 0.1;
                endpoint.Duplication = 0.2; endpoint.Reordering = 0.2;
                endpoint.BlackoutStart = 0.5; endpoint.BlackoutEnd = 1;
            }
            var ra = new RecordingReceiver(); var rb = new RecordingReceiver();
            var a = new PeerConnection(link.A, ra, true, 128);
            var b = new PeerConnection(link.B, rb, false, 128);
            var expected = new List<byte[]>();
            for (var i = 0; i < 180; ++i)
            {
                var payload = Payload(i % 11 * 71, i);
                expected.Add(payload);
                a.QueueReliable(payload); b.QueueReliable(payload);
            }
            Pump(link, a, b, 6000);
            Assert.AreEqual(expected.Count, ra.Reliable.Count);
            Assert.AreEqual(expected.Count, rb.Reliable.Count);
            for (var i = 0; i < expected.Count; ++i)
            {
                CollectionAssert.AreEqual(expected[i], ra.Reliable[i]);
                CollectionAssert.AreEqual(expected[i], rb.Reliable[i]);
            }
            Assert.AreEqual(0, a.PendingReliablePackets);
            Assert.AreEqual(0, b.PendingReliablePackets);
            Assert.AreEqual(0, a.InvalidPackets + b.InvalidPackets);
            Assert.LessOrEqual(link.A.LargestDatagram, 128);
            trace = link.A.Trace ^ link.B.Trace;
        }

        [Test]
        public void LostAcknowledgementsCauseResendWithoutRepeatedDelivery()
        {
            var link = new TestLink();
            link.B.DropKind = PacketKind.Ack; link.B.DropCount = 3;
            var receiver = new CountingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            a.QueueReliable(new byte[] { 1 });
            Pump(link, a, b, 200);
            Assert.AreEqual(1, receiver.ReliableCount);
            Assert.AreEqual(0, a.PendingReliablePackets);
            Assert.AreEqual(4, link.A.ReliableTransmissions);
        }

        [Test]
        public void GapHoldsReliableMessagesButDoesNotBlockInputsOrUnreliableData()
        {
            var link = new TestLink();
            link.A.DropKind = PacketKind.Reliable; link.A.DropCount = 1;
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            a.QueueReliable(new byte[] { 1 }); a.QueueReliable(new byte[] { 2 });
            a.Update(0);
            a.QueueInput(7, 0, new byte[] { 3 }); a.SendInputs(0);
            a.TrySendUnreliable(new byte[] { 4 });
            b.Update(0);
            Assert.AreEqual(0, receiver.Reliable.Count);
            Assert.AreEqual(1, receiver.Inputs.Count);
            Assert.AreEqual(1, receiver.Unreliable.Count);
            Pump(link, a, b, 100);
            Assert.AreEqual(2, receiver.Reliable.Count);
            Assert.AreEqual(1, receiver.Reliable[0][0]);
            Assert.AreEqual(2, receiver.Reliable[1][0]);
            Assert.AreEqual(3, link.A.ReliableTransmissions, "Only the missing sequence is resent.");
        }

        [Test]
        public void SequenceNumbersWrapWithAGapAndMultipleWindows()
        {
            var link = new TestLink();
            link.A.DropKind = PacketKind.Reliable; link.A.DropCount = 1;
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true, 64, uint.MaxValue - 2);
            var b = new PeerConnection(link.B, receiver, false, 64, uint.MaxValue - 2);
            for (var i = 0; i < 150; ++i) a.QueueReliable(new byte[] { (byte)i });
            Pump(link, a, b, 400);
            Assert.AreEqual(150, receiver.Reliable.Count);
            for (var i = 0; i < 150; ++i) Assert.AreEqual(i, receiver.Reliable[i][0]);
            Assert.AreEqual(0, a.PendingReliablePackets);
        }

        [Test]
        public void InputsRepeatCurrentAndThreePreviousTicksAndPreserveOlderArrivals()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            for (var tick = 0; tick < 6; ++tick) a.QueueInput(10, tick, new byte[] { (byte)tick });
            a.SendInputs(5); b.Update(0);
            Assert.AreEqual(4, receiver.Inputs.Count);
            for (var i = 0; i < 4; ++i)
            {
                Assert.AreEqual(5 - i, receiver.Inputs[i].Tick);
                Assert.AreEqual(5 - i, receiver.Inputs[i].Bytes[0]);
            }
            a.SendInputs(6); b.Update(0);
            Assert.AreEqual(7, receiver.Inputs.Count, "No invented input for tick 6; repeats are exposed to Runtime.");
            a.SendInputs(100); b.Update(0);
            Assert.AreEqual(7, receiver.Inputs.Count, "A large tick jump must not replay expired input.");
        }

        [Test]
        public void InputRedundancyRecoversThreeLostTicksAndSplitsAtRecordBoundaries()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true, 64);
            var b = new PeerConnection(link.B, receiver, false, 64);
            link.A.Loss = 1;
            for (var tick = 0; tick < 4; ++tick)
            {
                for (ulong key = 1; key <= 21; ++key) a.QueueInput(key, tick, new byte[20]);
                if (tick == 3) link.A.Loss = 0;
                a.SendInputs(tick);
            }
            b.Update(0);
            Assert.AreEqual(84, receiver.Inputs.Count);
            var unique = new HashSet<string>();
            foreach (var input in receiver.Inputs) unique.Add(input.Key + ":" + input.Tick);
            Assert.AreEqual(84, unique.Count);
            Assert.LessOrEqual(link.A.LargestDatagram, 64);
        }

        [Test]
        public void QueueCopiesBuffersAndReplacesAnInputForTheSameEntityAndTick()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            var buffer = new byte[] { 1 };
            a.QueueReliable(buffer); a.QueueInput(1, 0, buffer);
            buffer[0] = 2; a.QueueInput(1, 0, buffer);
            buffer[0] = 3;
            a.SendInputs(0); Pump(link, a, b, 10);
            Assert.AreEqual(1, receiver.Reliable[0][0]);
            Assert.AreEqual(1, receiver.Inputs.Count);
            Assert.AreEqual(2, receiver.Inputs[0].Bytes[0]);
        }

        [Test]
        public void LocalBackpressureKeepsReliableDataQueued()
        {
            var link = new TestLink();
            link.A.BlockSend = true;
            var receiver = new CountingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            a.QueueReliable(new byte[4000]);
            Pump(link, a, b, 100);
            Assert.AreEqual(0, receiver.ReliableCount);
            Assert.Greater(a.PendingReliablePackets, 0);
            link.A.BlockSend = false;
            Pump(link, a, b, 100);
            Assert.AreEqual(1, receiver.ReliableCount);
            Assert.AreEqual(0, a.PendingReliablePackets);
        }

        [Test]
        public void MalformedPacketsDoNotPartiallyDeliverInputsOrAcknowledgeMessages()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var peer = new PeerConnection(link.B, receiver, true, 64);
            var malformed = new List<byte[]>
            {
                Array.Empty<byte>(), new byte[] { 1, 1, 0 }, new byte[] { 2, 1, 0, 0 },
                new byte[] { 1, 99, 0, 0 }, new byte[] { 1, 1, 1, 0 }, new byte[65],
                new byte[] { 1, 3, 0, 0 }
            };
            var input = new byte[24];
            var writer = PacketFormat.Begin(input, PacketKind.Input, 20);
            writer.WriteUInt64(1); writer.WriteInt64(0); writer.WriteUInt16(0);
            writer.WriteByte(99); writer.WriteByte(99); // Valid record, then truncated record.
            malformed.Add(input);
            foreach (var bytes in malformed) link.B.Inject(bytes, 0);
            peer.QueueReliable(new byte[] { 42 });
            peer.Update(0);
            Assert.AreEqual(malformed.Count, peer.InvalidPackets);
            Assert.AreEqual(0, receiver.Inputs.Count);
            Assert.AreEqual(1, peer.PendingReliablePackets);
        }

        [Test]
        public void InvalidReliableFragmentDoesNotConsumeASequence()
        {
            var link = new TestLink();
            var receiver = new CountingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true, 64);
            var b = new PeerConnection(link.B, receiver, false, 64);
            var bytes = new byte[25];
            var writer = PacketFormat.Begin(bytes, PacketKind.Reliable, 21);
            writer.WriteUInt32(0); writer.WriteUInt32(0); writer.WriteUInt32(0);
            writer.WriteUInt32(2); writer.WriteUInt32(1); writer.WriteByte(9); // Impossible fragment count.
            link.B.Inject(bytes, 0);
            a.QueueReliable(new byte[] { 8 });
            Pump(link, a, b, 100);
            Assert.AreEqual(1, b.InvalidPackets);
            Assert.AreEqual(1, receiver.ReliableCount);
            Assert.AreEqual(0, a.PendingReliablePackets);
        }

        [Test]
        public void InvalidPublicArgumentsFailBeforeSending()
        {
            var link = new TestLink();
            var peer = new PeerConnection(link.A, new CountingReceiver(), true, 64);
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.QueueInput(1, -1, Array.Empty<byte>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.QueueInput(1, 0, new byte[43]));
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.TrySendUnreliable(new byte[61]));
            peer.QueueInput(1, 3, Array.Empty<byte>());
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.QueueInput(1, 2, Array.Empty<byte>()));
            peer.Update(3);
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.Update(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.Update(double.NaN));
        }

        [Test]
        public void SteadyTrafficIncludingRetransmissionAndReassemblyAllocatesNothing()
        {
            var link = new TestLink(31);
            link.A.Loss = link.B.Loss = 0.05;
            link.A.Duplication = link.B.Duplication = 0.1;
            link.A.Reordering = link.B.Reordering = 0.05;
            var ra = new CountingReceiver(); var rb = new CountingReceiver();
            var a = new PeerConnection(link.A, ra, true, 128);
            var b = new PeerConnection(link.B, rb, false, 128);
            var payload = new byte[250];
            // Prime more packets and larger buffers than the measurement window can need.
            for (var i = 0; i < 200; ++i) { a.QueueReliable(payload); b.QueueReliable(payload); }
            Pump(link, a, b, 2000);
            for (var tick = 0; tick < 1000; ++tick) Traffic(link, a, b, payload, tick);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var tick = 1000; tick < 2000; ++tick) Traffic(link, a, b, payload, tick);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
            Assert.Greater(ra.ReliableCount, 200); Assert.Greater(rb.ReliableCount, 200);
            Assert.Greater(ra.InputCount, 0); Assert.Greater(rb.InputCount, 0);
            Assert.Greater(ra.UnreliableCount, 0);
        }

        private static void Traffic(TestLink link, PeerConnection a, PeerConnection b, byte[] payload, int tick)
        {
            a.QueueReliable(payload); b.QueueReliable(payload);
            a.QueueInput(1, tick, payload.AsSpan(0, 20)); b.QueueInput(2, tick, payload.AsSpan(0, 20));
            a.SendInputs(tick); b.SendInputs(tick);
            a.TrySendUnreliable(payload.AsSpan(0, 40)); b.TrySendUnreliable(payload.AsSpan(0, 40));
            Pump(link, a, b, 2);
        }

        internal static void Pump(TestLink link, PeerConnection a, PeerConnection b, int steps)
        {
            for (var i = 0; i < steps; ++i)
            {
                a.Update(link.Now); b.Update(link.Now);
                link.Now += 0.01;
            }
        }

        private static byte[] Payload(int length, int seed)
        {
            var result = new byte[length];
            for (var i = 0; i < length; ++i) result[i] = (byte)(i * 31 + seed);
            return result;
        }
    }
}
