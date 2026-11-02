using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CombatSimulation.Network.Tests
{
    public class PeerConnectionTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1191)] // With its 2-byte prefix, fills one datagram exactly.
        [TestCase(1192)]
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
            a.QueueReliable(new byte[] { 1 }); a.Update(0);
            a.QueueReliable(new byte[] { 2 }); a.Update(0);
            a.TrySendInput(new byte[] { 3 });
            a.TrySendUnreliable(new byte[] { 4 });
            a.Update(0);
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
            // One message per update: one datagram each, so 150 sequences wrap and span several windows.
            for (var i = 0; i < 150; ++i) { a.QueueReliable(new byte[] { (byte)i }); Pump(link, a, b, 1); }
            Pump(link, a, b, 400);
            Assert.AreEqual(150, receiver.Reliable.Count);
            for (var i = 0; i < 150; ++i) Assert.AreEqual(i, receiver.Reliable[i][0]);
            Assert.AreEqual(0, a.PendingReliablePackets);
        }

        [Test]
        public void MessagesQueuedBetweenUpdatesShareOneDatagram()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            for (var i = 0; i < 10; ++i) a.QueueReliable(Payload(i * 7, i));
            Pump(link, a, b, 20);
            Assert.AreEqual(1, link.A.ReliableTransmissions);
            Assert.AreEqual(10, receiver.Reliable.Count);
            for (var i = 0; i < 10; ++i) CollectionAssert.AreEqual(Payload(i * 7, i), receiver.Reliable[i]);
        }

        [Test]
        public void UnsentTailKeepsFillingWhileSendingIsBlocked()
        {
            var link = new TestLink();
            link.A.BlockSend = true;
            var receiver = new CountingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            for (var i = 0; i < 100; ++i) { a.QueueReliable(new byte[10]); Pump(link, a, b, 1); }
            Assert.AreEqual(1, a.PendingReliablePackets, "100 messages of 11 bytes fit one 1,200-byte datagram.");
            link.A.BlockSend = false;
            Pump(link, a, b, 20);
            Assert.AreEqual(1, link.A.ReliableTransmissions);
            Assert.AreEqual(100, receiver.ReliableCount);
        }

        [Test]
        public void MissingPacketResendsAfterOneRoundTripWhenALaterPacketIsAcknowledged()
        {
            var link = new TestLink();
            link.A.Delay = link.B.Delay = 0.05;
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            Pump(link, a, b, 100); // Clock probes measure the 0.1 s round trip.
            Assert.Less(a.Clock.FastResendSeconds + 0.05, a.Clock.ResendSeconds);
            link.A.DropKind = PacketKind.Reliable; link.A.DropCount = 1;
            var start = link.Now;
            a.QueueReliable(new byte[] { 1 }); Pump(link, a, b, 1);
            a.QueueReliable(new byte[] { 2 });
            while (receiver.Reliable.Count == 0) Pump(link, a, b, 1);
            // Timeout resend: 2 round trips + one transit, about 0.29 s. Acknowledged gap: about 0.21 s.
            Assert.Less(link.Now - start, 0.25);
            Assert.AreEqual(2, receiver.Reliable.Count);
            Assert.AreEqual(1, receiver.Reliable[0][0]);
        }

        [Test]
        public void InputSectionsArriveWholeOnTheirOwnChannel()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true, 64);
            var b = new PeerConnection(link.B, receiver, false, 64);
            Assert.AreEqual(61, a.MaxInputPayload); // 64 bytes: version byte, 2-byte section header.
            var payload = Payload(61, 5);
            // The second section does not fit, so the first datagram leaves before it is queued.
            Assert.IsTrue(a.TrySendInput(payload)); Assert.IsTrue(a.TrySendInput(Array.Empty<byte>()));
            Assert.AreEqual(64, link.A.LargestDatagram);
            a.Update(0);
            b.Update(0);
            Assert.AreEqual(2, receiver.Inputs.Count);
            CollectionAssert.AreEqual(payload, receiver.Inputs[0]);
            Assert.AreEqual(0, receiver.Inputs[1].Length);
            Assert.AreEqual(0, receiver.Unreliable.Count + receiver.Reliable.Count);
        }

        [Test]
        public void QueueCopiesReliableBuffers()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            var buffer = new byte[] { 1 };
            a.QueueReliable(buffer);
            buffer[0] = 2;
            Pump(link, a, b, 10);
            Assert.AreEqual(1, receiver.Reliable[0][0]);
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
                Array.Empty<byte>(), new byte[] { 3 }, new byte[] { 2, 9, 7 }, // No section; version 2.
                new byte[] { 3, 8, 7 }, new byte[] { 3, 15, 7 }, // Kinds 0 and 7.
                new byte[] { 3, 17, 7 }, new byte[] { 3, 0x81, 0 }, new byte[65], // Truncated; non-minimal header; oversized.
                new byte[] { 3, 9, 7, 17, 7 }, // A valid input section, then a truncated one: neither is delivered.
                // Framing is valid, contents are not: reliable sequence without data, ack without mask, short probe.
                new byte[] { 3, 34, 0, 0, 0, 0 }, new byte[] { 3, 35, 0, 0, 0, 0 }, new byte[] { 3, 20, 0, 0 },
            };
            foreach (var bytes in malformed) link.B.Inject(bytes, 0);
            peer.QueueReliable(new byte[] { 42 });
            peer.Update(0);
            Assert.AreEqual(malformed.Count, peer.InvalidPackets);
            Assert.AreEqual(0, receiver.Inputs.Count);
            Assert.AreEqual(1, peer.PendingReliablePackets);
        }

        [Test]
        public void SectionsOfOneUpdateShareOneDatagram()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            Pump(link, a, b, 10);
            var sent = link.A.Datagrams;
            a.TrySendInput(new byte[] { 1 }); a.TrySendUnreliable(new byte[] { 2 }); a.QueueReliable(new byte[] { 3 });
            Pump(link, a, b, 1);
            Assert.AreEqual(1, link.A.Datagrams - sent, "Input, unreliable and reliable data share one datagram.");
            Assert.AreEqual(1, receiver.Inputs.Count); Assert.AreEqual(1, receiver.Unreliable.Count); Assert.AreEqual(1, receiver.Reliable.Count);
        }

        [Test]
        public void AcknowledgementWaitsForData()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, receiver, false);
            Pump(link, a, b, 10);
            a.QueueReliable(new byte[] { 1 }); a.Update(link.Now);
            var sent = link.B.Datagrams;
            b.Update(link.Now);
            Assert.AreEqual(sent, link.B.Datagrams, "An acknowledgement alone is held.");
            b.TrySendInput(new byte[] { 2 }); b.Update(link.Now);
            Assert.AreEqual(sent + 1, link.B.Datagrams);
            a.Update(link.Now);
            Assert.AreEqual(0, a.PendingReliablePackets, "The acknowledgement travelled with the input.");
            a.QueueReliable(new byte[] { 3 }); a.Update(link.Now); b.Update(link.Now);
            Assert.AreEqual(sent + 1, link.B.Datagrams);
            link.Now += 0.02; b.Update(link.Now);
            Assert.AreEqual(sent + 1, link.B.Datagrams);
            link.Now += 0.02; b.Update(link.Now);
            Assert.AreEqual(sent + 2, link.B.Datagrams, "A held acknowledgement leaves after 1.5 ticks.");
        }

        [Test]
        public void RefusedDatagramReturnsNewPacketsToTheUnsentStream()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true, 64);
            var b = new PeerConnection(link.B, receiver, false, 64);
            Pump(link, a, b, 10);
            link.A.BlockSend = true;
            a.QueueReliable(new byte[] { 1 }); Pump(link, a, b, 1);
            a.QueueReliable(new byte[] { 2 }); Pump(link, a, b, 1);
            Assert.AreEqual(1, a.PendingReliablePackets, "No sequence for bytes that never left.");
            link.A.BlockSend = false;
            Pump(link, a, b, 10);
            Assert.AreEqual(1, link.A.ReliableTransmissions);
            Assert.AreEqual(2, receiver.Reliable.Count);
        }

        [Test]
        public void InvalidReliablePacketDoesNotConsumeASequence()
        {
            var link = new TestLink();
            var receiver = new CountingReceiver();
            var a = new PeerConnection(link.A, new CountingReceiver(), true, 64);
            var b = new PeerConnection(link.B, receiver, false, 64);
            var datagram = new OutgoingDatagram(64);
            new ByteWriter(datagram.Add(PacketKind.Reliable, 4, true)).WriteUInt32(0); // A sequence without data.
            link.B.Inject(datagram.Bytes, 0);
            a.QueueReliable(new byte[] { 8 });
            Pump(link, a, b, 100);
            Assert.AreEqual(1, b.InvalidPackets);
            Assert.AreEqual(1, receiver.ReliableCount);
            Assert.AreEqual(0, a.PendingReliablePackets);
        }

        [Test]
        public void UnparsableAcknowledgedStreamFaultsTheConnection()
        {
            var link = new TestLink();
            var receiver = new RecordingReceiver();
            var b = new PeerConnection(link.B, receiver, false);
            var datagram = new OutgoingDatagram(64);
            var writer = new ByteWriter(datagram.Add(PacketKind.Reliable, 6, true));
            writer.WriteUInt32(0); writer.WriteByte(0x80); writer.WriteByte(0); // Non-minimal length prefix.
            link.B.Inject(datagram.Bytes, 0);
            b.Update(0);
            Assert.IsTrue(b.IsFaulted);
            Assert.AreEqual(0, receiver.Reliable.Count);
            Assert.Throws<InvalidOperationException>(() => b.Update(1));
        }

        [Test]
        public void InvalidPublicArgumentsFailBeforeSending()
        {
            var link = new TestLink();
            var peer = new PeerConnection(link.A, new CountingReceiver(), true, 64);
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.TrySendInput(new byte[62]));
            Assert.Throws<ArgumentOutOfRangeException>(() => peer.TrySendUnreliable(new byte[62]));
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
            a.TrySendInput(payload.AsSpan(0, 20)); b.TrySendInput(payload.AsSpan(0, 20));
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
