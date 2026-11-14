using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CombatSimulation.Core;
using CombatSimulation.Network;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    // Phase 7, step 5: two sessions, each on its own thread, in real time over an in-memory link.
    public class SessionThreadTests
    {
        private const double TimeoutSeconds = 10;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SessionThread> _threads = new List<SessionThread>();
        private Link _linkA, _linkB;

        [TearDown]
        public void TearDown()
        {
            foreach (var thread in _threads) thread.Dispose();
            _threads.Clear();
        }

        [Test]
        public void EveryTickIsPublishedOnceAndInOrder()
        {
            var (a, b) = StartPair();
            var readers = new[] { new Reader(a), new Reader(b) };
            RunUntil(() =>
            {
                foreach (var reader in readers) reader.TakeAll();
                return readers[0].NextTick > 150 && readers[1].NextTick > 150;
            });
            foreach (var reader in readers)
            {
                CollectionAssert.IsSubsetOf(new[] { new RosterId(1), new RosterId(101) }, reader.Deployed);
                Assert.IsNull(reader.Thread.Error);
                Assert.IsTrue(reader.Thread.Status.IsRunning);
                var status = reader.Thread.Status;
                TestContext.WriteLine($"Iterations {status.Iterations}, catch-up ticks {status.CatchUpTicks}, " +
                    $"max gap {status.MaxGap:F1} ms, max late {status.MaxLate:F1} ms, round trip {status.RoundTrip:F1} ms");
            }
        }

        // The main thread stalls while it holds a copy: the copy stays intact, and no tick is lost.
        [Test]
        public void StalledReaderGetsIntactCopiesAndEveryTick()
        {
            var (a, b) = StartPair();
            var readers = new[] { new Reader(a), new Reader(b) };
            for (var stall = 0; stall < 3; ++stall)
            {
                RunUntil(() =>
                {
                    foreach (var reader in readers) reader.TakeAll();
                    return readers[0].NextTick > 50 * (stall + 1) && readers[1].NextTick > 50 * (stall + 1);
                });
                BattleSnapshot held = null;
                RunUntil(() => a.TryTake(out held));
                var before = Fingerprint(held);
                Thread.Sleep(300);
                Assert.GreaterOrEqual(a.Status.QueuedSnapshots, 5, "The worker kept publishing during the stall.");
                Assert.AreEqual(before, Fingerprint(held), "A held copy changed.");
                readers[0].Check(held);
                a.Release(held);
            }
            foreach (var reader in readers)
            {
                reader.TakeAll();
                Assert.IsNull(reader.Thread.Error);
                Assert.Greater(reader.Reused, 0, "Released copies are reused.");
            }
        }

        [Test]
        public void ReaderTooFarBehindStopsTheThread()
        {
            var (a, _) = StartPair(maxQueued: 20);
            RunUntil(() => !a.Status.IsRunning);
            StringAssert.Contains("20 ticks behind", a.Error?.Message);
        }

        [Test]
        public void InvalidControlsAreCountedAndTheThreadKeepsRunning()
        {
            var (a, _) = StartPair();
            a.Post(SessionCommand.Submit(default, PilotControls.Create(0f, 0.5f, 0)));
            RunUntil(() => a.Status.RejectedCommands == 1);
            var tick = a.Status.Tick;
            RunUntil(() => a.Status.Tick > tick + 5);
            Assert.IsNull(a.Error);
        }

        // Phase 7, step 6: beforeStep sees each tick before it runs, and each copy carries the time it was queued.
        [Test]
        public void BeforeStepRunsBeforeEachTickAndCopiesCarryTheirPublishTime()
        {
            var before = new ConcurrentQueue<long>();
            var (a, _) = StartPair(beforeStep: before.Enqueue);
            var taken = new List<(long Tick, double Time)>();
            RunUntil(() =>
            {
                while (a.TryTake(out var snapshot, out var time))
                {
                    taken.Add((snapshot.Tick, time));
                    a.Release(snapshot);
                }
                return taken.Count > 50;
            });
            var ticks = before.ToArray();
            for (var i = 0; i < taken.Count; ++i)
            {
                Assert.AreEqual(ticks[i], taken[i].Tick, "beforeStep gets the tick about to run");
                if (i > 0) Assert.GreaterOrEqual(taken[i].Time, taken[i - 1].Time);
            }
            Assert.LessOrEqual(taken[taken.Count - 1].Time, Now());
        }

        // Phase 7, step 8: a cut link ends both threads after the silence limit, without an error.
        [Test]
        public void CutLinkEndsBothThreadsWithSilence()
        {
            var (a, b) = StartPair(linkTimeout: 0.5);
            var readers = new[] { new Reader(a), new Reader(b) };
            RunUntil(() =>
            {
                foreach (var reader in readers) reader.TakeAll();
                return readers[0].NextTick > 50 && readers[1].NextTick > 50;
            });
            Assert.AreEqual(SessionLinkLoss.None, a.Status.LinkLoss, "Normal traffic is never silent for 0.5 s.");
            _linkA.Cut = _linkB.Cut = true;
            RunUntil(() =>
            {
                foreach (var reader in readers) reader.TakeAll();
                return !a.Status.IsRunning && !b.Status.IsRunning;
            });
            foreach (var thread in new[] { a, b })
            {
                Assert.AreEqual(SessionLinkLoss.Silence, thread.Status.LinkLoss);
                Assert.IsFalse(thread.Status.IsCompleted);
                Assert.IsNull(thread.Error);
            }
        }

        [Test]
        public void ClosedTransportEndsTheThread()
        {
            var (a, b) = StartPair();
            var readers = new[] { new Reader(a), new Reader(b) };
            RunUntil(() =>
            {
                foreach (var reader in readers) reader.TakeAll();
                return readers[0].NextTick > 20;
            });
            _linkA.Closed = true;
            RunUntil(() =>
            {
                foreach (var reader in readers) reader.TakeAll();
                return !a.Status.IsRunning;
            });
            Assert.AreEqual(SessionLinkLoss.TransportClosed, a.Status.LinkLoss);
            Assert.IsNull(a.Error);
        }

        // The reader's limit counts from its first TryTake, so a scene load before it does not end the match.
        [Test]
        public void StalledReaderEndsTheThreadOnlyAfterItsFirstTake()
        {
            var (a, b) = StartPair(linkTimeout: 0.5);
            var readerB = new Reader(b);
            var start = Now();
            RunUntil(() =>
            {
                readerB.TakeAll();
                return Now() - start > 1;
            });
            Assert.IsTrue(a.Status.IsRunning);
            Assert.AreEqual(SessionLinkLoss.None, a.Status.LinkLoss);
            if (a.TryTake(out var snapshot)) a.Release(snapshot);
            RunUntil(() =>
            {
                readerB.TakeAll();
                return !a.Status.IsRunning;
            });
            Assert.AreEqual(SessionLinkLoss.ReaderStalled, a.Status.LinkLoss);
            Assert.IsNull(a.Error);
        }

        // A one-way link: A receives B's data, so A is never silent, but nothing A sends is acknowledged.
        [Test]
        public void OneWayLinkEndsWithTheBacklogLimit()
        {
            var (a, b) = StartPair(linkTimeout: 30, maxBacklog: 200);
            var readers = new[] { new Reader(a), new Reader(b) };
            RunUntil(() =>
            {
                foreach (var reader in readers) reader.TakeAll();
                return readers[0].NextTick > 20;
            });
            _linkA.Cut = true;
            RunUntil(() =>
            {
                foreach (var reader in readers) reader.TakeAll();
                return !a.Status.IsRunning;
            });
            Assert.AreEqual(SessionLinkLoss.Backlog, a.Status.LinkLoss);
            Assert.IsNull(a.Error);
        }

        // Both threads start with one deployment each and a clock epoch 0.1 s ahead. beforeStep goes to the first.
        private (SessionThread a, SessionThread b) StartPair(int maxQueued = SessionThread.MaxQueuedSnapshots,
            Action<long> beforeStep = null, double linkTimeout = MatchHandshake.LinkTimeoutSeconds,
            long maxBacklog = SessionThread.MaxReliableBacklogBytes)
        {
            var linkA = _linkA = new Link();
            var linkB = _linkB = new Link();
            linkA.Other = linkB; linkB.Other = linkA;
            var pilots = new SessionFixtures.Pilots { Enabled = true, Throttle = 1, AimAtEnemy = true };
            var a = new NetworkSession(SessionFixtures.Setup(10), pilots, linkA, new NetworkSessionOptions(10, 20, true));
            var b = new NetworkSession(SessionFixtures.Setup(20), pilots, linkB, new NetworkSessionOptions(20, 10, false));
            RunUntil(() =>
            {
                a.Pump(Now()); b.Pump(Now());
                return b.Clock.HasEstimate;
            });
            var epoch = Now() + 0.1;
            a.StartClock(Now(), epoch);
            b.StartClock(Now(), epoch);
            var threadA = new SessionThread(a, linkA, Now, maxQueued, beforeStep: beforeStep, linkTimeoutSeconds: linkTimeout,
                maxReliableBacklogBytes: maxBacklog);
            var threadB = new SessionThread(b, linkB, Now, maxQueued, linkTimeoutSeconds: linkTimeout,
                maxReliableBacklogBytes: maxBacklog);
            _threads.Add(threadA); _threads.Add(threadB);
            threadA.Post(SessionCommand.Deploy(new RosterId(1)));
            threadB.Post(SessionCommand.Deploy(new RosterId(101)));
            threadA.Start();
            threadB.Start();
            return (threadA, threadB);
        }

        private double Now() => _clock.Elapsed.TotalSeconds;

        private static void RunUntil(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > TimeoutSeconds) Assert.Fail("Timed out");
                Thread.Sleep(1);
            }
        }

        private static long Fingerprint(BattleSnapshot snapshot)
        {
            unchecked
            {
                var hash = snapshot.Tick * 31 + snapshot.TimerElapsedTicks;
                foreach (var ship in snapshot.Ships)
                    hash = hash * 31 + (long)ship.Id.GetHashCode() * 17 + BitConverter.DoubleToInt64Bits(
                        ship.Position.X * 3.0 + ship.Position.Y * 7.0 + ship.Angle + ship.Armor.Value + ship.Energy.Value);
                foreach (var slot in snapshot.Roster) hash = hash * 31 + (long)slot.Status;
                foreach (var item in snapshot.Events) hash = hash * 31 + (long)item.Kind * 7 + item.Tick;
                return hash;
            }
        }

        private sealed class Reader
        {
            internal readonly SessionThread Thread;
            internal readonly List<RosterId> Deployed = new List<RosterId>();
            internal long NextTick;
            internal int Reused;
            private readonly HashSet<BattleSnapshot> _seen = new HashSet<BattleSnapshot>();

            internal Reader(SessionThread thread) { Thread = thread; }

            internal void TakeAll()
            {
                while (Thread.TryTake(out var snapshot))
                {
                    Check(snapshot);
                    if (!_seen.Add(snapshot)) ++Reused;
                    Thread.Release(snapshot);
                }
            }

            // Ticks arrive one by one, and a copy holds only its own tick's events.
            internal void Check(BattleSnapshot snapshot)
            {
                Assert.AreEqual(NextTick++, snapshot.Tick, "Snapshot order");
                foreach (var item in snapshot.Events)
                {
                    Assert.AreEqual(snapshot.Tick, item.Tick, "Event of another tick");
                    if (item.Kind == BattleEventKind.Spawn && !item.IsBullet && !item.RosterId.Equals(default(RosterId)))
                        Deployed.Add(item.RosterId);
                }
            }
        }

        // Thread-safe; Update and Flush do nothing, as the link delivers at once. Cut drops this side's sends.
        private sealed class Link : IPumpedTransport
        {
            internal Link Other;
            internal volatile bool Cut, Closed;
            private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();

            public bool IsClosed => Closed;

            public bool TrySend(ReadOnlySpan<byte> datagram)
            {
                if (!Cut) Other._inbox.Enqueue(datagram.ToArray());
                return true;
            }

            public bool TryReceive(Span<byte> buffer, out int length)
            {
                length = 0;
                if (!_inbox.TryDequeue(out var datagram)) return false;
                length = datagram.Length;
                datagram.AsSpan(0, Math.Min(length, buffer.Length)).CopyTo(buffer);
                return true;
            }

            public void Update() { }
            public void Flush() { }
        }
    }
}
