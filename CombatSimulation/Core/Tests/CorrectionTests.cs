using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Network plan, step 5: owner corrections move a remote copy toward the owner's state.
    public class CorrectionTests
    {
        [Test]
        public void MovementErrorUsesTheMinimumImage()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(1f, 50f), new Vec2(2f, 0f));
            p.B.Place(101, new Vec2(199f, 50f), new Vec2(2f, 0f));
            p.Step();
            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            AssertSameMotion(p, 101);
            Assert.Less(p.B.Battle.World.GetPosition(p.B.Ship(101).Body).X, 2f);
        }

        [Test]
        public void HistoryFollowsTheCorrectionSoAnErrorAppliesOnce()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), new Vec2(2f, 0f));
            p.B.Place(101, new Vec2(50f, 52f), new Vec2(2f, 0f));
            p.Step();
            var first = Write(p.A);
            p.Step();
            var second = Write(p.A);
            p.Step();

            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, first));
            p.Step();
            AssertSameMotion(p, 101);
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, second));
            p.Step();
            AssertSameMotion(p, 101);
        }

        [Test]
        public void OnlyTheNewestCorrectionApplies()
        {
            var p = new NetPair();
            p.B.Place(101, new Vec2(50f, 52f), Vec2.Zero);
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.Step();
            var first = Write(p.A);
            p.Step();
            var second = Write(p.A);

            // Received together: the older one is dropped.
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, second));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, first));
            p.Step();
            Assert.AreEqual(second.Tick, p.B.Ship(101).LastCorrectionTick);
            AssertSameMotion(p, 101);

            // Received after a newer one applied.
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, first));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, second));
        }

        [Test]
        public void CorrectionForATickNotYetRunIsHeld()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.B.Place(101, new Vec2(50f, 52f), Vec2.Zero);
            p.A.Step();
            var ahead = Write(p.A);
            Assert.AreEqual(p.B.Tick, ahead.Tick);
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, ahead));

            p.B.Step();
            Assert.AreEqual(ShipEntity.NoCorrection, p.B.Ship(101).LastCorrectionTick);
            p.Step();
            Assert.AreEqual(ahead.Tick, p.B.Ship(101).LastCorrectionTick);
            AssertSameMotion(p, 101);
        }

        [Test]
        public void CorrectionOlderThanTheHistorySetsOnlyGameplayState()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.B.Place(101, new Vec2(50f, 52f), Vec2.Zero);
            p.A.Ship(101).Armor.Value = 700f;
            p.Step();
            var old = Write(p.A);
            p.Step(ShipEntity.BodyHistoryLength + 2);

            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, old));
            p.Step();
            Assert.AreEqual(old.Tick, p.B.Ship(101).LastCorrectionTick);
            Assert.AreEqual(700f, p.B.Ship(101).Armor.Value);
            Assert.AreEqual(2f, Offset(p, 101).Y, 1e-4f);
        }

        [Test]
        public void GameplayStateIsSetExactlyThenRecharged()
        {
            var p = new NetPair();
            var owner = p.A.Ship(101);
            owner.Energy.Value = 10f;
            owner.Energy.DelayRemaining = 0.1f;
            owner.Shield.Value = 100f;
            owner.Shield.DelayRemaining = 0.5f;
            p.Step();
            var correction = Write(p.A);
            p.Step(8);
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, correction));

            // A remote shield does not recharge between corrections, so it matches the tick before.
            var shield = owner.Shield.Value;
            p.Step();
            var copy = p.B.Ship(101);
            Assert.AreEqual(owner.Energy.Value, copy.Energy.Value, 1e-5f);
            Assert.AreEqual(owner.Energy.DelayRemaining, copy.Energy.DelayRemaining, 1e-6f);
            Assert.AreEqual(shield, copy.Shield.Value, 1e-5f);
            Assert.Greater(copy.Energy.Value, 10f);
            Assert.Greater(copy.Shield.DelayRemaining, 0f);
        }

        [Test]
        public void RetreatFromACorrectionStopsTheCopy()
        {
            var p = new NetPair(ship101: Beam());
            p.A.Control(101, throttle: 1f, systems: 1);
            p.B.Control(101, throttle: 1f, systems: 1);
            p.Step(3);
            Assert.IsTrue(Device<BeamDevice>(p.B, 0).TryGetBeam(out _));

            p.A.Retreat(p.A.Entity(101));
            p.Step();
            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step(2);

            Assert.AreEqual(p.A.Ship(101).RetreatCompletionTick, p.B.Ship(101).RetreatCompletionTick);
            Assert.IsFalse(Device<BeamDevice>(p.B, 0).TryGetBeam(out _));
            Assert.AreEqual(0, p.B.Battle.Snapshot.Bullets.Length);
            AssertSameMotion(p, 101);
        }

        [Test]
        public void DeviceTimersFollowTheOwner()
        {
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(100000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new CannonDefinition(0, 0.1f, 0f, 0f, Fixtures.Bolt),
                    new BeamDefinition(0, 0.2f, 0f, 0f, 10f, 8f),
                    new PointDefenseDefinition(5f, 0.1f, 0f),
                });
            var p = new NetPair(ship101: ship);
            p.A.Control(101, systems: 0b111);
            p.Step(5);
            p.A.Control(101, systems: 0b101);
            p.Step(3);
            Assert.AreEqual(1, p.Correct(p.A, p.B));

            // From here both copies run the same input.
            p.A.Control(101, systems: 0b101);
            p.B.Control(101, systems: 0b101);
            p.Step();
            for (var d = 0; d < 3; ++d)
                Assert.AreEqual(p.A.Ship(101).GetDevice(d).Export(), p.B.Ship(101).GetDevice(d).Export(), $"device {d}");
            Assert.AreEqual(p.A.Ship(101).GetBarrelShotTick(0), p.B.Ship(101).GetBarrelShotTick(0));
            Assert.IsTrue(((PointDefenseDevice)p.B.Ship(101).GetDevice(2)).Enabled);

            var bay = DroneBayTests.Carrier().Devices[0].CreateDevice(0);
            Assert.AreEqual(default(DeviceState), bay.Export());
        }

        [Test]
        public void LocalEntitiesAreNeverCorrected()
        {
            var p = new NetPair();
            var batch = new CorrectionBatch();
            p.B.Battle.WriteCorrections(NetPair.OwnerB, NetPair.OwnerA, batch);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerB, batch));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, batch));

            // A record that claims the other owner for this device's ship.
            var body = batch.Bodies[0];
            var forged = new CorrectionBatch();
            forged.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            forged.Add(body.EntityId, body.Motion, body.Armor, body.Shield, body.Energy, body.RetreatCompletionTick,
                batch.GetDevices(body), batch.GetBarrelShotTicks(body));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, forged));

            Assert.Throws<ArgumentException>(() => p.B.Battle.WriteCorrections(NetPair.OwnerA, NetPair.OwnerB, batch));
        }

        [Test]
        public void InvalidCorrectionsAreRejected()
        {
            var p = new NetPair();
            var batch = Write(p.A);
            var body = batch.Bodies[0];
            var bad = new CorrectionBatch();

            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, 1001f, body.Shield, body.Energy, -1, default, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, body.Armor, new RechargeState(1f, 5f), body.Energy, -1, default, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, new SpawnPose(new Vec2(float.NaN, 0f), 0f, Vec2.Zero), body.Armor, body.Shield,
                body.Energy, -1, default, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, body.Armor, body.Shield, body.Energy, -1, new DeviceState[1], default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            bad.Begin(new BattleId(5), NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, body.Armor, body.Shield, body.Energy, -1, default, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, batch));
        }

        [Test]
        public void LateInputDriftIsRepairedByOneCorrection()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.B.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.A.Control(101, course: 1f, throttle: 1f);
            p.Step(3);
            p.B.Control(101, course: 1f, throttle: 1f);
            p.Step(5);
            Assert.Greater(Offset(p, 101).Length, 0.05f);

            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            AssertSameMotion(p, 101, 1e-3f);
        }

        [Test]
        public void CorrectionChurnAllocatesNothingAfterWarmup()
        {
            var p = new NetPair();
            p.A.Control(101, course: 1f, throttle: 1f);
            p.B.Control(101, course: 2f, throttle: 1f);
            for (var i = 0; i < 40; ++i) ChurnTick(p, i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 40; i < 140; ++i) ChurnTick(p, i);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes);
        }

        private static void ChurnTick(NetPair p, int step)
        {
            p.Step();
            if (step % 5 != 0) return;
            if (p.Correct(p.A, p.B) != 1 || p.Correct(p.B, p.A) != 1) throw new InvalidOperationException("Rejected correction.");
        }

        internal static ShipDefinition Beam() => Fixtures.ShipWith(
            barrels: new[] { new BarrelDefinition(Vec2.Zero) },
            devices: new[] { new BeamDefinition(0, 0f, 0f, 0f, 10f, 8f) });

        private static T Device<T>(Driver driver, int index) where T : ShipDevice => (T)driver.Ship(101).GetDevice(index);

        private static CorrectionBatch Write(Driver owner)
        {
            var batch = new CorrectionBatch();
            owner.Battle.WriteCorrections(NetPair.OwnerA, NetPair.OwnerB, batch);
            return batch;
        }

        // The receiver's position minus the owner's, across the wrap.
        private static Vec2 Offset(NetPair p, uint rosterId) => p.A.Battle.World.Torus.Delta(
            p.A.Battle.World.GetPosition(p.A.Ship(rosterId).Body), p.B.Battle.World.GetPosition(p.B.Ship(rosterId).Body));

        private static void AssertSameMotion(NetPair p, uint rosterId, float tolerance = 1e-4f)
        {
            var a = p.A.Ship(rosterId).Body;
            var b = p.B.Ship(rosterId).Body;
            var worldA = p.A.Battle.World;
            var worldB = p.B.Battle.World;
            Assert.Less(Offset(p, rosterId).Length, tolerance, "position");
            Assert.AreEqual(worldA.GetAngle(a), worldB.GetAngle(b), tolerance, "angle");
            Assert.Less((worldA.GetLinearVelocity(a) - worldB.GetLinearVelocity(b)).Length, tolerance, "velocity");
            Assert.AreEqual(worldA.GetAngularVelocity(a), worldB.GetAngularVelocity(b), tolerance, "angular velocity");
        }
    }

    // Two cores in one process. A owns slot 101 (owner 20); B owns slot 1 (owner 10). Each step runs both,
    // then delivers operations, hits and a watermark each way before the next tick.
    internal sealed class NetPair
    {
        public const int OwnerA = 20;
        public const int OwnerB = 10;

        public NetPair(ShipDefinition ship1 = null, ShipDefinition ship101 = null, int delayB = 0, int delayA = 0)
        {
            A = new Driver(Setup(OwnerA, ship1, ship101, delayB, delayA));
            B = new Driver(Setup(OwnerB, ship1, ship101, delayB, delayA));
            A.Deploy(101);
            B.Deploy(1);
            // Each deployment reaches the other core in the next tick.
            Step(2);
            A.Place(1, new Vec2(100f, 150f), Vec2.Zero);
            B.Place(1, new Vec2(100f, 150f), Vec2.Zero);
            A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            B.Place(101, new Vec2(50f, 50f), Vec2.Zero);
        }

        public Driver A { get; }
        public Driver B { get; }

        // B's outgoing hits wait here instead of reaching A.
        public bool HoldHitsFromB { get; set; }
        public readonly List<ResolvedHit> HeldHits = new List<ResolvedHit>(64);

        public void Step(int count = 1)
        {
            for (var i = 0; i < count; ++i)
            {
                A.Step();
                B.Step();
                Relay(A, B, OwnerA, false);
                Relay(B, A, OwnerB, HoldHitsFromB);
            }
        }

        public void ReleaseHits()
        {
            foreach (var hit in HeldHits)
                if (!A.Battle.Submit(new InboundHit(OwnerB, hit))) throw new InvalidOperationException("Rejected hit.");
            HeldHits.Clear();
        }

        // The sender's corrections for its last tick; returns how many the receiver holds.
        public int Correct(Driver from, Driver to)
        {
            var sender = from == A ? OwnerA : OwnerB;
            from.Battle.WriteCorrections(sender, from == A ? OwnerB : OwnerA, _batch);
            return to.Battle.Submit(sender, _batch);
        }

        private void Relay(Driver from, Driver to, int sender, bool holdHits)
        {
            var snapshot = from.Battle.Snapshot;
            foreach (var operation in snapshot.OutgoingOperations)
            {
                if (!to.Battle.Submit(sender, operation)) throw new InvalidOperationException("Rejected record.");
                if (sender == OwnerA) _lastA = operation.Sequence;
                else _lastB = operation.Sequence;
            }
            if (!to.Battle.Submit(sender, new OwnerWatermark(to.BattleId, sender, snapshot.Tick, sender == OwnerA ? _lastA : _lastB)))
                throw new InvalidOperationException("Rejected watermark.");
            foreach (var hit in snapshot.OutgoingHits)
            {
                if (holdHits) HeldHits.Add(hit);
                else if (!to.Battle.Submit(new InboundHit(sender, hit))) throw new InvalidOperationException("Rejected hit.");
            }
        }

        private static BattleSetup Setup(int localOwner, ShipDefinition ship1, ShipDefinition ship101, int delayB,
            int delayA) => new BattleSetup(new BattleId(77), 13, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, ship1 ?? Fixtures.Ship, OwnerB),
                new RosterSlotSetup(new RosterId(101), 1, ship101 ?? Fixtures.Ship, OwnerA),
            }, localOwners: new[] { localOwner },
            ownerDelays: new Dictionary<int, int> { [OwnerB] = delayB, [OwnerA] = delayA });

        private readonly CorrectionBatch _batch = new CorrectionBatch();
        private ulong _lastA;
        private ulong _lastB;
    }
}
