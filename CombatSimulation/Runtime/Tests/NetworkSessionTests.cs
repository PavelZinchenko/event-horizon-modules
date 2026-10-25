using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    [TestFixture]
    public class NetworkSessionTests
    {
        [Test]
        public void ObserverCanBeAttachedAndDetachedThroughSession()
        {
            var pair = new SessionFixtures.Pair(); var observer = new ObservationCounter();
            pair.A.Observer = observer; Assert.AreSame(observer, pair.A.Observer);
            pair.Start(); pair.Run(30); Assert.Greater(observer.Count, 0);
            pair.A.Observer = null; var count = observer.Count; pair.Run(30);
            Assert.AreEqual(count, observer.Count);
        }

        private sealed class ObservationCounter : IBattleObserver
        {
            internal int Count;
            public void Correction(in CorrectionObservation value) { ++Count; }
            public void Contact(in ContactObservation value) { }
            public void Projectile(in ProjectileObservation value) { }
            public void HitProcessed(in HitObservation value) { }
            public void BeamDamage(in BeamDamageObservation value) { }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void LocalControlsWaitForSenderDelayAndRemoteUsesTheSameTick(int delay)
        {
            var pair = new SessionFixtures.Pair(delay, 5 - delay); pair.Start();
            var entity = SessionFixtures.Owned(pair.A, 10).Id;
            var original = pair.A.Tick;
            pair.A.SubmitControls(entity, original, PilotControls.Create(0.5f, 0.75f, 0));
            for (var i = 0; i <= delay; ++i)
            {
                pair.Step();
                var expected = i == delay ? SessionFixtures.Quantized(0.75f) : 0;
                Assert.AreEqual(expected, SessionFixtures.Owned(pair.A, 10).Throttle);
                Assert.AreEqual(expected, SessionFixtures.Owned(pair.B, 10).Throttle);
            }
        }

        [Test]
        public void PilotDelayRemainsTwoTicksBeforeNetworkDelay()
        {
            var pair = new SessionFixtures.Pair(3, 1, pilots: new SessionFixtures.Pilots { Enabled = true, Throttle = 1 });
            pair.Start(); // First decisions read tick 0. They are sent at tick 2, applied at tick 5 / 3.
            while (pair.A.Tick <= 5)
            {
                var tick = pair.A.Tick; pair.Step();
                Assert.AreEqual(tick >= 5 ? 1 : 0, SessionFixtures.Owned(pair.A, 10).Throttle);
                Assert.AreEqual(tick >= 3 ? 1 : 0, SessionFixtures.Owned(pair.B, 20).Throttle);
            }
        }

        [Test]
        public void ReorderedFutureFramesWaitAndOverdueFramesNeverReplaceThePresentFrame()
        {
            var pair = new SessionFixtures.Pair(2); pair.Start();
            var entity = Index(pair.A, SessionFixtures.Owned(pair.A, 10).Id);
            Inject(pair.B, entity, 4, 0.4f); Inject(pair.B, entity, 2, 0.2f); Inject(pair.B, entity, 3, 0.3f);
            pair.Run(3); // tick 4 applies original tick 2, despite tick 4 arriving first.
            Assert.AreEqual(SessionFixtures.Quantized(0.2f), SessionFixtures.Owned(pair.B, 10).Throttle);
            Inject(pair.B, entity, 2, 0.9f); // duplicate/stale
            pair.Step(); Assert.AreEqual(SessionFixtures.Quantized(0.3f), SessionFixtures.Owned(pair.B, 10).Throttle);
            pair.Step(); Assert.AreEqual(SessionFixtures.Quantized(0.4f), SessionFixtures.Owned(pair.B, 10).Throttle);
            pair.Run(3); // present tick 10
            Inject(pair.B, entity, 6, 1); Inject(pair.B, entity, 7, 0); // overdue press and release
            Inject(pair.B, entity, 8, 0.8f); // exactly present
            pair.Step(); Assert.AreEqual(SessionFixtures.Quantized(0.8f), SessionFixtures.Owned(pair.B, 10).Throttle);
            Inject(pair.B, entity, 5, 1); pair.Step();
            Assert.AreEqual(SessionFixtures.Quantized(0.8f), SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        [Test]
        public void OnlyTheNewestOverdueFrameApplies()
        {
            var pair = new SessionFixtures.Pair(); pair.Start(); pair.Run(8);
            var entity = Index(pair.A, SessionFixtures.Owned(pair.A, 10).Id);
            Inject(pair.B, entity, 4, 1); Inject(pair.B, entity, 5, 0);
            pair.Step(); Assert.AreEqual(0, SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        [Test]
        public void UnknownIndexWrongBattleVersionAndMalformedInputDoNotControlAShip()
        {
            var pair = new SessionFixtures.Pair(); pair.Start();
            var remote = Index(pair.A, SessionFixtures.Owned(pair.A, 10).Id);
            // Indices name only the sender's spawns: no index can reach B's own ship.
            Inject(pair.B, remote + 1, pair.B.Tick, 1);
            var length = Packet(pair.B.BattleId, remote, pair.B.Tick, 1, out var bytes);
            bytes[0] = 99; Deliver(pair.B, bytes, length); // version
            length = Packet(new BattleId(999), remote, pair.B.Tick, 1, out bytes); Deliver(pair.B, bytes, length);
            length = Packet(pair.B.BattleId, remote, pair.B.Tick, 1, out bytes); Deliver(pair.B, bytes, length - 1);
            length = Packet(pair.B.BattleId, remote, pair.B.Tick, 1, out bytes);
            bytes[length - 1] |= 0x80; Deliver(pair.B, bytes, length); // nonzero padding
            pair.Step();
            Assert.AreEqual(4, pair.B.InvalidMessages);
            Assert.AreEqual(0, SessionFixtures.Owned(pair.B, 20).Throttle);
            Assert.AreEqual(0, SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        // A malformed batch submits nothing. The stream names entities by count, so every later batch is dropped too.
        [Test]
        public void AMalformedBatchStopsTheReliableStream()
        {
            var pair = new SessionFixtures.Pair(); pair.Start();
            ((IPeerReceiver)pair.B).OnReliable(new byte[] { SessionCodec.Version, 0, 0, 0xff }); // record kind 0
            pair.Step();
            Assert.AreEqual(1, pair.B.InvalidMessages);
            pair.Run(10);
            Assert.Greater(pair.B.InvalidMessages, 1);
        }

        [Test]
        public void BothCopiesFlyTheSameQuantizedFrames()
        {
            var pair = new SessionFixtures.Pair(2, 3,
                pilots: new SessionFixtures.Pilots { Enabled = true, AimAtEnemy = true, Throttle = 0.37f });
            pair.Start(); pair.Run(200);
            foreach (var owner in new[] { 10, 20 })
            {
                var a = SessionFixtures.Owned(pair.A, owner); var b = SessionFixtures.Owned(pair.B, owner);
                Assert.IsTrue(a.HasCourse); Assert.AreEqual(a.Course, b.Course); Assert.AreEqual(a.Throttle, b.Throttle);
                Assert.AreEqual(SessionFixtures.Quantized(0.37f), a.Throttle);
                var degrees = a.Course * 180 / Math.PI;
                Assert.AreEqual(Math.Round(degrees), degrees, 1e-4, "Course is a whole degree.");
            }
        }

        [Test]
        public void UnchangedControlsSendOnlyRefreshes()
        {
            var pair = new SessionFixtures.Pair(pilots: new SessionFixtures.Pilots { Enabled = true, Throttle = 0.5f });
            pair.Start(); pair.Run(50);
            pair.TA.InputSections = 0; pair.Run(100);
            Assert.AreEqual(100 / ControlSender.RefreshTicks, pair.TA.InputSections);
            Assert.AreEqual(SessionFixtures.Quantized(0.5f), SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        [Test]
        public void AChangeUsesThreeDatagramsAndARefreshRepairsALostChange()
        {
            var pair = new SessionFixtures.Pair(); pair.Start(); pair.Run(3);
            var entity = SessionFixtures.Owned(pair.A, 10).Id;
            pair.TA.InputSections = 0;
            pair.A.SubmitControls(entity, pair.A.Tick, PilotControls.Create(0.25f, 0.5f, 1));
            pair.Run(10);
            Assert.AreEqual(ControlSender.Copies, pair.TA.InputSections);
            Assert.AreEqual(SessionFixtures.Quantized(0.5f), SessionFixtures.Owned(pair.B, 10).Throttle);

            pair.TA.DropInput = true;
            pair.A.SubmitControls(entity, pair.A.Tick, PilotControls.Create(1.5f, 1, 0));
            pair.Run(ControlSender.Copies + 1);
            pair.TA.DropInput = false;
            Assert.AreEqual(SessionFixtures.Quantized(0.5f), SessionFixtures.Owned(pair.B, 10).Throttle);
            pair.Run(ControlSender.RefreshTicks);
            var a = SessionFixtures.Owned(pair.A, 10); var b = SessionFixtures.Owned(pair.B, 10);
            Assert.AreEqual(1, b.Throttle); Assert.AreEqual(a.Course, b.Course); Assert.AreEqual(a.Systems, b.Systems);
        }

        [TestCase(100, 10)] [TestCase(150, 6)] [TestCase(200, 5)]
        public void CorrectionsUseElapsedSimulationTime(int interval, int count)
        {
            var pair = new SessionFixtures.Pair(interval: interval); pair.Start(); pair.Run(48);
            Assert.AreEqual(count, pair.A.SentCorrectionBatches);
            pair.Step(); Assert.AreEqual(count, pair.B.ReceivedCorrectionBatches);
        }

        [Test]
        public void CorrectionsRecoverAfterUnreliableLoss()
        {
            var pair = new SessionFixtures.Pair(); pair.Start();
            pair.TA.DropUnreliable = true; pair.Run(10);
            Assert.AreEqual(0, pair.B.ReceivedCorrectionBatches);
            pair.TA.DropUnreliable = false; pair.Run(10);
            Assert.Greater(pair.B.ReceivedCorrectionBatches, 0);
            Assert.AreEqual(0, pair.B.InvalidMessages);
        }

        // Datagram 80 holds five resting bodies, so six ships need two self-contained datagrams per batch.
        [Test]
        public void ABatchLargerThanOneDatagramContinuesInMoreDatagrams()
        {
            var pair = new SessionFixtures.Pair(datagram: 80, slots: 6, maxActive: 6); var observer = new ObservationCounter();
            pair.B.Observer = observer;
            for (var i = 0; i < 6; ++i) { pair.A.Deploy(new RosterId((uint)(1 + i)), 0); pair.B.Deploy(new RosterId((uint)(101 + i)), 0); }
            pair.Run(6);
            Assert.AreEqual(1, pair.A.SentCorrectionBatches);
            Assert.AreEqual(2, pair.TA.UnreliableSections);
            Assert.AreEqual(6, observer.Count);
            Assert.AreEqual(1, pair.B.ReceivedCorrectionBatches);
            Assert.AreEqual(0, pair.B.InvalidMessages);
        }

        // Control indices arrive in spawn order, so the first unknown index ends the datagram without an error.
        [Test]
        public void ACorrectionStopsAtTheFirstBodyWithoutASpawnRecord()
        {
            var pair = new SessionFixtures.Pair(); pair.Start(); var observer = new ObservationCounter();
            pair.B.Observer = observer;
            var ship = SessionFixtures.Owned(pair.B, 10);
            var batch = new CorrectionBatch(); batch.Begin(pair.B.BattleId, 10, pair.B.Tick - 1, 0);
            batch.Add(ship.Id, new SpawnPose(ship.Position, ship.Angle, ship.LinearVelocity, ship.AngularVelocity),
                ResourceLevel.Partial(64, false), ResourceLevel.Full, ResourceLevel.Full, ReadOnlySpan<PointDefenseTimers>.Empty);
            var body = batch.Bodies[0];
            var bytes = new byte[64]; var arena = new CorrectionCodec.Arena(200, 200);
            var w = CorrectionCodec.Begin(bytes, pair.B.BattleId, batch.Tick, 0);
            Assert.IsTrue(CorrectionCodec.Write(ref w, arena, 0, body, SessionFixtures.Ship, ReadOnlySpan<PointDefenseTimers>.Empty));
            Assert.IsTrue(CorrectionCodec.Write(ref w, arena, 4, body, SessionFixtures.Ship, ReadOnlySpan<PointDefenseTimers>.Empty));
            ((IPeerReceiver)pair.B).OnUnreliable(bytes.AsSpan(0, w.Finish()));
            pair.Step();
            Assert.AreEqual(1, observer.Count);
            Assert.AreEqual(250, SessionFixtures.Owned(pair.B, 10).Armor.Value);
            Assert.AreEqual(0, pair.B.InvalidMessages);
        }

        [Test]
        public void InputCanApplyWithAnInboundDeploymentInThePresentTick()
        {
            var pair = new SessionFixtures.Pair();
            pair.A.Deploy(new RosterId(1), 0);
            pair.A.Step(0);
            var entity = Index(pair.A, SessionFixtures.Owned(pair.A, 10).Id);
            Inject(pair.B, entity, 0, 1);
            pair.B.Step(0);
            Assert.AreEqual(1, SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        [TestCase(false)] [TestCase(true)]
        public void BothPeersAgreeOnSurrenderAndMutualElimination(bool mutual)
        {
            var pair = new SessionFixtures.Pair(); pair.Start();
            pair.A.Surrender(0, 10);
            if (mutual) pair.B.Surrender(1, 10);
            pair.Run(20);
            Assert.IsTrue(pair.A.IsCompleted); Assert.IsTrue(pair.B.IsCompleted);
            Assert.AreEqual(pair.A.Snapshot.Result, pair.B.Snapshot.Result);
            Assert.AreEqual(10, pair.A.Snapshot.Result.Tick);
            Assert.AreEqual(mutual ? BattleOutcome.Draw : BattleOutcome.Victory, pair.A.Snapshot.Result.Outcome);
            pair.Run(20); // Final reliable messages and acknowledgements drain after freezing.
            Assert.AreEqual(0, pair.A.PendingReliablePackets); Assert.AreEqual(0, pair.B.PendingReliablePackets);
        }

        [Test]
        public void TicksWithoutRecordsSendOnlyAHeartbeatBatch()
        {
            var pair = new SessionFixtures.Pair(); pair.Start(); pair.Run(10);
            var before = pair.TA.ReliablePackets;
            pair.Run(50);
            Assert.AreEqual(10, pair.TA.ReliablePackets - before);
            // A pending result sends a batch every tick, so it never waits for a heartbeat.
            pair.TA.Hold = true; pair.A.Surrender(0, pair.A.Tick + 1); // A never sees B's watermark.
            before = pair.TA.ReliablePackets;
            pair.Run(10);
            Assert.IsTrue(pair.A.Snapshot.CandidateResult.IsCompleted); Assert.IsFalse(pair.A.IsCompleted);
            Assert.GreaterOrEqual(pair.TA.ReliablePackets - before, 9, "Heartbeats alone would send 2-3.");
        }

        [Test]
        public void SurrenderBeforeDeploymentReplicatesAllReserveTransitions()
        {
            var pair = new SessionFixtures.Pair(slots: 3);
            pair.B.Surrender(1, 0); pair.Run(10);
            Assert.AreEqual(pair.A.Snapshot.Result, pair.B.Snapshot.Result);
            Assert.AreEqual(0, pair.A.Snapshot.Result.Tick);
            foreach (var slot in pair.A.Snapshot.Roster)
                if (slot.Side == 1) Assert.AreEqual(RosterStatus.Destroyed, slot.Status);
            Assert.AreEqual(0, pair.A.Snapshot.Ships.Length);
        }

        [Test]
        public void BlackoutHoldsCandidateUntilRecordsArrive()
        {
            var pair = new SessionFixtures.Pair(); pair.Start();
            pair.TA.Hold = pair.TB.Hold = true; pair.A.Surrender(0, 3); pair.Run(10);
            Assert.IsFalse(pair.A.IsCompleted); Assert.IsFalse(pair.B.IsCompleted);
            Assert.IsTrue(pair.A.Snapshot.CandidateResult.IsCompleted);
            pair.TA.Hold = pair.TB.Hold = false; pair.Run(20);
            Assert.AreEqual(pair.A.Snapshot.Result, pair.B.Snapshot.Result);
            Assert.AreEqual(3, pair.A.Snapshot.Result.Tick);
        }

        [Test]
        public void TimeoutUsesOriginalDeployTicksOnBothPeers()
        {
            var pair = new SessionFixtures.Pair(timeout: 10); pair.Start(); pair.Run(20);
            Assert.AreEqual(pair.A.Snapshot.Result, pair.B.Snapshot.Result);
            Assert.AreEqual(BattleEndReason.Timeout, pair.A.Snapshot.Result.Reason);
            Assert.AreEqual(9, pair.A.Snapshot.Result.Tick);
        }

        [Test]
        public void ClockDrivenUpdatesUseTheExistingPacedClock()
        {
            var pair = new SessionFixtures.Pair();
            // Probes and replies alone wait 1.5 ticks for data, so the exchange ends near 0.06. 1 ms steps keep both
            // transit times under 1 ms, so the offset estimate stays far below a tick.
            for (var i = 0; i <= 70; ++i) { pair.A.Pump(0.001 * i); pair.B.Pump(0.001 * i); }
            Assert.IsTrue(pair.B.Clock.HasEstimate);
            pair.A.StartClock(0.07, 0.1); pair.B.StartClock(0.07, 0.1);
            pair.A.Update(0.08); pair.B.Update(0.08);
            Assert.AreEqual(0, pair.A.Tick);
            pair.A.Update(0.21); pair.B.Update(0.21); // Half a tick past tick 5, away from rounding at the boundary.
            Assert.AreEqual(6, pair.A.Tick); Assert.AreEqual(6, pair.B.Tick);
            Assert.That(pair.B.Clock.Rate, Is.InRange(0.98, 1.02));
        }

        [Test]
        public void WarmedSessionsWithInputsAndCorrectionsAllocateNothing()
        {
            var pair = new SessionFixtures.Pair(2, 3, pilots: new SessionFixtures.Pilots { Enabled = true, Throttle = 0.1f });
            pair.Start(); pair.Run(300);
            var before = GC.GetAllocatedBytesForCurrentThread(); pair.Run(200);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
        }

        [Test]
        public void CombatTrafficAgreesOnCommittedRosterAndDroneLifecycle()
        {
            var engine = new EngineDefinition(20, 10, 5, 2);
            var bolt = new BulletDefinition(0.1f, 100, 150, 1);
            var missile = new BulletDefinition(0.2f, 50, 150, 1, hitPoints: 2, homing: true);
            var drone = new ShipDefinition(0.5f, 1, engine, 1000, new ResourceDefinition(0), new ResourceDefinition(10000),
                barrels: new[] { new BarrelDefinition(new Vec2(1, 0)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0.1f, 1, 0, bolt) });
            var ship = new ShipDefinition(1, 1, engine, 10000, new ResourceDefinition(0), new ResourceDefinition(100000),
                barrels: new[] { new BarrelDefinition(new Vec2(1, 0)), new BarrelDefinition(new Vec2(1, 0)),
                    new BarrelDefinition(new Vec2(1, 0)) },
                devices: new DeviceDefinition[]
                {
                    new CannonDefinition(0, 0.08f, 1, 0, bolt), new BeamDefinition(1, 0.1f, 0, 0, 150, 2),
                    new DroneBayDefinition(2, 1, drone), new PointDefenseDefinition(3, 0.2f, 0, droneDamage: 1),
                    new CannonDefinition(2, 0.5f, 1, 0, missile),
                });
            var pair = new SessionFixtures.Pair(2, 3, ship: ship,
                pilots: new SessionFixtures.Pilots { Enabled = true, AimAtEnemy = true, Throttle = 0.5f, Systems = 31 });
            pair.A.Deploy(new RosterId(1), 0); pair.B.Deploy(new RosterId(101), 0);
            var sent = new Dictionary<(int, ulong), OwnerOperation>();
            var applied = new Dictionary<(int, ulong), OwnerOperation>();
            var hits = 0; var drones = 0; var droneDeaths = 0; var bullets = 0;
            for (var tick = 0; tick < 320; ++tick)
            {
                if (tick == 40)
                    foreach (var session in new[] { pair.A, pair.B })
                        foreach (var body in session.Snapshot.Ships)
                            if (body.IsDrone && body.Owner == (session == pair.A ? 10 : 20))
                            { session.SelfDestruct(body.Id, session.Tick); break; }
                if (tick == 300) pair.A.Surrender(0, 300);
                var aTick = pair.A.Tick; var bTick = pair.B.Tick;
                pair.Step();
                Capture(pair.A, aTick); Capture(pair.B, bTick);
            }
            Assert.AreEqual(pair.A.Snapshot.Result, pair.B.Snapshot.Result);
            Assert.AreEqual(300, pair.A.Snapshot.Result.Tick);
            Assert.Greater(hits, 0); Assert.Greater(drones, 2); Assert.GreaterOrEqual(droneDeaths, 2); Assert.Greater(bullets, 10);
            foreach (var item in sent)
            {
                if (item.Value.Tick > 300) continue;
                Assert.IsTrue(applied.TryGetValue(item.Key, out var copy), $"Missing {item.Value.Kind} {item.Key}");
                Assert.AreEqual(item.Value, copy);
            }
            Assert.AreEqual(0, pair.A.InvalidMessages); Assert.AreEqual(0, pair.B.InvalidMessages);

            void Capture(NetworkSession session, long before)
            {
                if (session.Tick == before) return;
                hits += session.Snapshot.OutgoingHits.Length;
                foreach (var op in session.Snapshot.OutgoingOperations)
                {
                    sent.Add((op.Owner, op.Sequence), op);
                    if (op.Kind == OwnerOperationKind.DroneSpawn) ++drones;
                    if (op.Kind == OwnerOperationKind.Removal && op.BayKey.HasValue) ++droneDeaths;
                    if (op.Kind == OwnerOperationKind.BulletSpawn) ++bullets;
                }
                foreach (var op in session.Snapshot.AppliedOperations) applied.Add((op.Owner, op.Sequence), op);
            }
        }

        private static uint Index(NetworkSession owner, EntityId entity)
        {
            Assert.IsTrue(owner.TryGetLocalControlIndex(entity, out var index));
            return index;
        }

        private static void Inject(NetworkSession session, uint index, long tick, float throttle)
        {
            var length = Packet(session.BattleId, index, tick, throttle, out var bytes);
            Deliver(session, bytes, length);
        }

        private static void Deliver(NetworkSession session, byte[] bytes, int length) =>
            ((IPeerReceiver)session).OnInput(bytes.AsSpan(0, length));

        private static int Packet(BattleId battle, uint index, long tick, float throttle, out byte[] bytes)
        {
            bytes = new byte[64];
            var w = InputCodec.Begin(bytes, battle, tick);
            InputCodec.Write(ref w, tick, new ControlRecord(index, tick,
                QuantizedControls.From(PilotControls.Create(0, throttle, 0), default, false)));
            return w.Finish();
        }
    }
}
