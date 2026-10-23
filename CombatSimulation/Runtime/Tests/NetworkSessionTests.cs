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
                Assert.AreEqual(i == delay ? 0.75f : 0, SessionFixtures.Owned(pair.A, 10).Throttle);
                Assert.AreEqual(i == delay ? 0.75f : 0, SessionFixtures.Owned(pair.B, 10).Throttle);
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
            var entity = SessionFixtures.Owned(pair.B, 10).Id;
            Inject(pair.B, entity, 4, 0.4f); Inject(pair.B, entity, 2, 0.2f); Inject(pair.B, entity, 3, 0.3f);
            pair.Run(3); // tick 4 applies original tick 2, despite tick 4 arriving first.
            Assert.AreEqual(0.2f, SessionFixtures.Owned(pair.B, 10).Throttle);
            Inject(pair.B, entity, 2, 0.9f); // duplicate/stale
            pair.Step(); Assert.AreEqual(0.3f, SessionFixtures.Owned(pair.B, 10).Throttle);
            pair.Step(); Assert.AreEqual(0.4f, SessionFixtures.Owned(pair.B, 10).Throttle);
            pair.Run(3); // present tick 10
            Inject(pair.B, entity, 6, 1); Inject(pair.B, entity, 7, 0); // overdue press and release
            Inject(pair.B, entity, 8, 0.8f); // exactly present
            pair.Step(); Assert.AreEqual(0.8f, SessionFixtures.Owned(pair.B, 10).Throttle);
            Inject(pair.B, entity, 5, 1); pair.Step();
            Assert.AreEqual(0.8f, SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        [Test]
        public void OnlyTheNewestOverdueFrameApplies()
        {
            var pair = new SessionFixtures.Pair(); pair.Start(); pair.Run(8);
            var entity = SessionFixtures.Owned(pair.B, 10).Id;
            Inject(pair.B, entity, 4, 1); Inject(pair.B, entity, 5, 0);
            pair.Step(); Assert.AreEqual(0, SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        [Test]
        public void WrongOwnerBattleVersionAndMalformedInputDoNotControlAShip()
        {
            var pair = new SessionFixtures.Pair(); pair.Start();
            var own = SessionFixtures.Owned(pair.B, 20).Id;
            Inject(pair.B, own, pair.B.Tick, 1); // A cannot control B's ship.
            var bytes = new byte[160]; var input = PilotControls.Create(0, 1, 0);
            var length = SessionCodec.Input(bytes, pair.B.BattleId, 20, pair.B.Tick, input);
            var remote = SessionFixtures.Owned(pair.B, 10).Id;
            ((IPeerReceiver)pair.B).OnInput(remote.Value, pair.B.Tick, bytes.AsSpan(0, length));
            length = SessionCodec.Input(bytes, new BattleId(999), 10, pair.B.Tick, input);
            ((IPeerReceiver)pair.B).OnInput(remote.Value, pair.B.Tick, bytes.AsSpan(0, length));
            length = SessionCodec.Input(bytes, pair.B.BattleId, 10, pair.B.Tick, input);
            bytes[0] = 99;
            ((IPeerReceiver)pair.B).OnInput(remote.Value, pair.B.Tick, bytes.AsSpan(0, length));
            bytes[0] = 1;
            ((IPeerReceiver)pair.B).OnInput(remote.Value, pair.B.Tick, bytes.AsSpan(0, length - 1));
            pair.Step();
            Assert.AreEqual(4, pair.B.InvalidMessages);
            Assert.AreEqual(0, SessionFixtures.Owned(pair.B, 20).Throttle);
            Assert.AreEqual(0, SessionFixtures.Owned(pair.B, 10).Throttle);
        }

        [TestCase(100, 10)] [TestCase(150, 6)] [TestCase(200, 5)]
        public void CorrectionsUseElapsedSimulationTime(int interval, int count)
        {
            var pair = new SessionFixtures.Pair(interval: interval); pair.Start(); pair.Run(48);
            Assert.AreEqual(count, pair.A.SentCorrectionBatches);
            pair.Step(); Assert.AreEqual(count, pair.B.ReceivedCorrectionBatches);
        }

        [Test]
        public void FragmentedCorrectionsRecoverAfterUnreliableLoss()
        {
            var devices = new DeviceDefinition[64];
            for (var i = 0; i < devices.Length; ++i) devices[i] = new CannonDefinition(0, 1, 1, 0, new BulletDefinition(0.1f, 10, 10, 1));
            var ship = new ShipDefinition(1, 1, new EngineDefinition(20, 10, 5, 2), 1000,
                new ResourceDefinition(0, 0, 0), new ResourceDefinition(100, 1, 0),
                barrels: new[] { new BarrelDefinition(Vec2.Zero) }, devices: devices);
            var pair = new SessionFixtures.Pair(ship: ship, datagram: 100); pair.Start();
            pair.TA.DropUnreliable = true; pair.Run(10);
            Assert.AreEqual(0, pair.B.ReceivedCorrectionBatches);
            pair.TA.DropUnreliable = false; pair.Run(10);
            Assert.Greater(pair.B.ReceivedCorrectionBatches, 0);
            Assert.AreEqual(0, pair.B.InvalidMessages);
        }

        [Test]
        public void NewerFragmentedBatchReplacesIncompleteOlderBatchAndIgnoresDuplicates()
        {
            var pair = new SessionFixtures.Pair(datagram: 100); pair.Start();
            var ship = SessionFixtures.Owned(pair.B, 10);
            var old = Correction(0, 111); var current = Correction(1, 222);
            Fragment(old, 0, 0); Fragment(current, 1, 67);
            Fragment(old, 0, 67); Fragment(current, 1, 67); // stale and duplicate
            pair.Step();
            Assert.AreEqual(0, pair.B.ReceivedCorrectionBatches);
            Assert.AreEqual(1000, SessionFixtures.Owned(pair.B, 10).Armor.Value);
            Fragment(current, 1, 0); pair.Step();
            Assert.AreEqual(1, pair.B.ReceivedCorrectionBatches);
            Assert.AreEqual(222, SessionFixtures.Owned(pair.B, 10).Armor.Value);
            Assert.AreEqual(0, pair.B.InvalidMessages);

            byte[] Correction(long tick, float armor)
            {
                var batch = new CorrectionBatch(); batch.Begin(pair.B.BattleId, 10, tick, 0);
                batch.Add(ship.Id, new SpawnPose(ship.Position, ship.Angle, ship.LinearVelocity, ship.AngularVelocity),
                    armor, new RechargeState(200, 0), new RechargeState(100, 0), -1,
                    ReadOnlySpan<DeviceState>.Empty, ReadOnlySpan<long>.Empty);
                var bytes = new byte[SessionCodec.CorrectionSize(batch)]; SessionCodec.Correction(bytes, batch); return bytes;
            }
            void Fragment(byte[] bytes, long tick, int offset)
            {
                var packet = new byte[96]; var writer = new ByteWriter(packet);
                writer.WriteByte(1); writer.WriteUInt64(pair.B.BattleId.Value); writer.WriteUInt32(10);
                writer.WriteInt64(tick); writer.WriteUInt32((uint)bytes.Length); writer.WriteUInt32((uint)offset);
                writer.WriteBytes(bytes.AsSpan(offset, Math.Min(67, bytes.Length - offset)));
                ((IPeerReceiver)pair.B).OnUnreliable(packet.AsSpan(0, writer.Position));
            }
        }

        [Test]
        public void InputCanApplyWithAnInboundDeploymentInThePresentTick()
        {
            var pair = new SessionFixtures.Pair();
            pair.A.Deploy(new RosterId(1), 0);
            pair.A.Step(0);
            var entity = SessionFixtures.Owned(pair.A, 10).Id;
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
            pair.A.Pump(0); pair.B.Pump(0); pair.A.Pump(0); pair.B.Pump(0);
            Assert.IsTrue(pair.B.Clock.HasEstimate);
            pair.A.StartClock(0, 0.1); pair.B.StartClock(0, 0.1);
            pair.A.Update(0.08); pair.B.Update(0.08);
            Assert.AreEqual(0, pair.A.Tick);
            pair.A.Update(0.2); pair.B.Update(0.2);
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

        private static void Inject(NetworkSession session, EntityId entity, long tick, float throttle)
        {
            var bytes = new byte[160];
            var length = SessionCodec.Input(bytes, session.BattleId, 10, tick, PilotControls.Create(0, throttle, 0));
            ((IPeerReceiver)session).OnInput(entity.Value, tick, bytes.AsSpan(0, length));
        }
    }
}
