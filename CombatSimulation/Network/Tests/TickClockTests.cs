using System;
using NUnit.Framework;

namespace CombatSimulation.Network.Tests
{
    public class TickClockTests
    {
        [Test]
        public void FourTimestampsRemoveRemoteProcessingTime()
        {
            var clock = new TickClock(false);
            Assert.IsFalse(clock.HasEstimate);
            Assert.AreEqual(0.2, clock.ResendSeconds);
            Assert.IsTrue(clock.Observe(1, 11.03, 11.05, 1.08));
            Assert.AreEqual(0.06, clock.RoundTripSeconds, 1e-12);
            Assert.AreEqual(10, clock.OffsetSeconds, 1e-12);
            Assert.AreEqual(0.12, clock.ResendSeconds, 1e-12);
            Assert.IsTrue(clock.HasEstimate);
        }

        [Test]
        public void LowestRoundTripSampleExpiresAfterSixteenSamples()
        {
            var clock = new TickClock(false);
            clock.Observe(0, 5.01, 5.01, 0.02);
            for (var i = 1; i < 16; ++i) clock.Observe(i, i + 7.1, i + 7.1, i + 0.2);
            Assert.AreEqual(5, clock.OffsetSeconds, 1e-12);
            clock.Observe(16, 23.1, 23.1, 16.2);
            Assert.AreEqual(7, clock.OffsetSeconds, 1e-12);
        }

        [TestCase(double.NaN, 1, 1, 2)]
        [TestCase(0, double.PositiveInfinity, 1, 2)]
        [TestCase(2, 1, 1, 0)]
        [TestCase(0, 2, 1, 3)]
        [TestCase(0, 1, 5, 1)]
        public void InvalidSamplesLeaveTheEstimateUnchanged(double t1, double t2, double t3, double t4)
        {
            var clock = new TickClock(false);
            clock.Observe(0, 1.1, 1.1, 0.2);
            var offset = clock.OffsetSeconds;
            var roundTrip = clock.RoundTripSeconds;
            Assert.IsFalse(clock.Observe(t1, t2, t3, t4));
            Assert.AreEqual(offset, clock.OffsetSeconds);
            Assert.AreEqual(roundTrip, clock.RoundTripSeconds);
        }

        [Test]
        public void StartAlignsDifferentClockOriginsAndSupportsAFutureEpoch()
        {
            var reference = new TickClock(true);
            var follower = new TickClock(false);
            follower.Observe(100, 0.04, 0.04, 100.08);
            reference.Start(1, 2);
            follower.Start(101, 2);
            Assert.AreEqual(-50, reference.TickPosition);
            Assert.AreEqual(reference.TickPosition, follower.TickPosition, 1e-9);
            Assert.IsFalse(follower.IsTickDue(0));
            reference.Advance(2); follower.Advance(102);
            Assert.IsTrue(reference.IsTickDue(0));
            Assert.IsTrue(follower.IsTickDue(0));
            Assert.IsFalse(follower.IsTickDue(1));
            Assert.Throws<InvalidOperationException>(() => follower.Start(102, 2));
        }

        [Test]
        public void RunningClockNeverSnapsOrRewindsAndPacesWithinTwoPercent()
        {
            var clock = new TickClock(false);
            clock.Observe(0, 0.1, 0.1, 0.2);
            clock.Start(1, 1);
            var previous = clock.TickPosition;
            // A new low-latency estimate moves the target ahead by ten seconds.
            clock.Observe(1, 11.01, 11.01, 1.02);
            Assert.AreEqual(previous, clock.TickPosition);
            clock.Advance(1.02);
            Assert.AreEqual(1, clock.TickPosition, 1e-9);
            Assert.AreEqual(1.02, clock.Rate);
            for (var i = 1; i <= 100; ++i)
            {
                previous = clock.TickPosition;
                clock.Advance(1.02 + i * 0.02);
                Assert.Greater(clock.TickPosition, previous);
                Assert.That(clock.Rate, Is.InRange(0.98, 1.02));
            }
            // Replace the sample window with a target behind the running simulation.
            for (var i = 0; i < 16; ++i) clock.Observe(4 + i, -6 + i, -6 + i, 4.02 + i);
            previous = clock.TickPosition;
            clock.Advance(3.04);
            Assert.Greater(clock.TickPosition, previous);
            Assert.AreEqual(0.98, clock.Rate);
        }

        [TestCase(-0.001)]
        [TestCase(0.001)]
        public void PacingTracksOscillatorDrift(double drift)
        {
            var clock = new TickClock(false);
            clock.Observe(0, 0, 0, 0);
            clock.Start(0, 0);
            var peakAfterStartup = 0.0;
            for (var step = 1; step <= 6000; ++step)
            {
                var realTime = step * 0.01;
                var localTime = realTime * (1 + drift);
                if (step % 25 == 0)
                    clock.Observe(localTime, realTime, realTime, localTime);
                clock.Advance(localTime);
                Assert.That(clock.Rate, Is.InRange(0.98, 1.02));
                if (realTime >= 2) peakAfterStartup = Math.Max(peakAfterStartup, Math.Abs(clock.TickPosition - realTime * 50));
            }
            Assert.Less(peakAfterStartup, 1);
        }

        [Test]
        public void PeerProbesSynchronizeDistinctOriginsOverJitterAndLoss()
        {
            var link = new TestLink(9);
            link.A.Delay = link.B.Delay = 0.06;
            link.A.Jitter = link.B.Jitter = 0.02;
            link.A.Loss = link.B.Loss = 0.05;
            var a = new PeerConnection(link.A, new CountingReceiver(), true);
            var b = new PeerConnection(link.B, new CountingReceiver(), false);
            for (var i = 0; i < 4000; ++i)
            {
                link.Now = i * 0.001;
                a.Update(link.Now); b.Update(link.Now + 100);
            }
            Assert.IsTrue(b.Clock.HasEstimate);
            Assert.AreEqual(-100, b.Clock.OffsetSeconds, 0.02);
            Assert.That(b.Clock.RoundTripSeconds, Is.InRange(0.08, 0.17));
            Assert.AreEqual(0, a.Clock.OffsetSeconds);
            a.Clock.Start(4, 4); b.Clock.Start(104, 4);
            var maxDifference = 0.0;
            for (var i = 4000; i < 10000; ++i)
            {
                link.Now = i * 0.001;
                a.Update(link.Now); b.Update(link.Now + 100);
                if (i >= 6000) maxDifference = Math.Max(maxDifference, Math.Abs(a.Clock.TickPosition - b.Clock.TickPosition));
            }
            Assert.LessOrEqual(maxDifference, 1);
        }

        [Test]
        public void AsymmetricDelayExposesTwoTickBiasAgainstActualReferenceTime()
        {
            var clock = new TickClock(false);
            // Follower -> reference: 40 ms. Reference -> follower: 120 ms.
            clock.Observe(0, 0.04, 0.04, 0.16);
            Assert.AreEqual(-0.04, clock.OffsetSeconds, 1e-12);
            clock.Start(1, 1);
            for (var i = 1; i <= 500; ++i) clock.Advance(1 + i * 0.01);
            var actualReferenceTick = 5 * TickClock.TicksPerSecond;
            var actualDifference = Math.Abs(actualReferenceTick - clock.TickPosition);
            Assert.AreEqual(2, actualDifference, 1e-9);
            Assert.Greater(actualDifference, 1, "The gate must report this failure, not compare against the biased estimate.");
            TestContext.WriteLine($"Asymmetric 40/120 ms: actual tick difference {actualDifference:F3}; one-tick gate fails.");
        }

        [Test]
        public void DuplicateProbeRepliesDoNotRefreshTheSampleWindow()
        {
            var link = new TestLink();
            link.B.Duplication = 1;
            var a = new PeerConnection(link.A, new CountingReceiver(), false);
            var b = new PeerConnection(link.B, new CountingReceiver(), true);
            PeerConnectionTests.Pump(link, a, b, 10);
            Assert.AreEqual(0, a.InvalidPackets);
            Assert.AreEqual(0, a.Clock.RoundTripSeconds, 0.02);
            Assert.IsTrue(a.Clock.HasEstimate);
        }
    }
}
