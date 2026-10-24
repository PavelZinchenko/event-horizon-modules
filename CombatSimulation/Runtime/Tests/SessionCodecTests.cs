using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    [TestFixture]
    public class SessionCodecTests
    {
        [Test]
        public void EveryOwnerOperationPreservesItsFullPayload()
        {
            var battle = new BattleId(0x123456789abcdef0); var entity = new EntityId(1234);
            var pose = new SpawnPose(new Vec2(1.25f, -20.5f), 2.5f, new Vec2(-5, 7), -3);
            var bay = new DroneBayKey(new RosterId(99), 6);
            var operations = new[]
            {
                OwnerOperation.RosterChange(battle, 20, 77, 123, entity,
                    new RosterRecord(new RosterId(99), 4, RosterStatus.Ready, RosterStatus.Active,
                        RosterTransitionReason.Deployed, 2, 5, 22.5f, 9.25f, 0.25f), pose),
                OwnerOperation.DroneLaunch(battle, 20, 78, 124, entity, bay, new EntityId(15), pose),
                OwnerOperation.Remove(battle, 20, 79, 125, entity, RemovalReason.Death, bay),
                OwnerOperation.Remove(battle, 20, 80, 126, entity, RemovalReason.Expiry),
                OwnerOperation.BulletFired(battle, 20, 81, 127, entity, new EntityId(15), 7, 3, 5),
            };
            var bytes = new byte[SessionCodec.RecordCapacity];
            foreach (var expected in operations)
            {
                var count = SessionCodec.Operation(bytes, expected);
                var r = new SessionCodec.Reader(bytes.AsSpan(0, count));
                var actual = SessionCodec.ReadOperation(ref r); r.End();
                Assert.AreEqual(expected, actual);
                for (var size = 0; size < count; ++size)
                {
                    var length = size;
                    Assert.Throws<FormatException>(() => ReadOperation(bytes, length));
                }
            }
        }

        [TestCase(HitImpactKind.Impact)] [TestCase(HitImpactKind.Energy)] [TestCase(HitImpactKind.DamageOverTime)]
        public void HitsPreserveDamageAuthorityAndSequence(HitImpactKind kind)
        {
            var expected = new ResolvedHit(new BattleId(99), 123, new EntityId(4321), 20, -1, new EntityId(5678),
                10, 3, HitContactState.End, kind, 8.125f, 987);
            var bytes = new byte[160]; var count = SessionCodec.Hit(bytes, expected);
            var r = new SessionCodec.Reader(bytes.AsSpan(0, count));
            var actual = SessionCodec.ReadHit(ref r); r.End();
            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void CorrectionsPreserveAllBodiesResourcesDevicesAndBarrels()
        {
            var batch = new CorrectionBatch(); batch.Begin(new BattleId(1), 20, 50, 123456);
            for (var i = 0; i < 3; ++i)
                batch.Add(new EntityId((ulong)(i + 1)), new SpawnPose(new Vec2(i, -i), 0.5f, new Vec2(2, 3), -2),
                    50.25f, new RechargeState(42, 0.75f), new RechargeState(18, 0.25f), i == 0 ? -1 : 500,
                    new[] { new DeviceState(-1, 0, true, false), new DeviceState(42, 51, false, true) }, new long[] { -1, 45 });
            var bytes = new byte[SessionCodec.CorrectionSize(batch)];
            Assert.AreEqual(bytes.Length, SessionCodec.Correction(bytes, batch));
            var r = new SessionCodec.Reader(bytes); var actual = new CorrectionBatch();
            var devices = Array.Empty<DeviceState>(); var barrels = Array.Empty<long>();
            SessionCodec.ReadCorrection(ref r, actual, ref devices, ref barrels); r.End();
            Assert.AreEqual(batch.BattleId, actual.BattleId); Assert.AreEqual(batch.Owner, actual.Owner);
            Assert.AreEqual(batch.Tick, actual.Tick); Assert.AreEqual(batch.AppliedHitSequence, actual.AppliedHitSequence);
            CollectionAssert.AreEqual(batch.Bodies.ToArray(), actual.Bodies.ToArray());
            CollectionAssert.AreEqual(batch.Devices.ToArray(), actual.Devices.ToArray());
            CollectionAssert.AreEqual(batch.BarrelShotTicks.ToArray(), actual.BarrelShotTicks.ToArray());
        }

        [Test]
        public void InvalidFloatAndTrailingDataAreRejected()
        {
            var hit = new ResolvedHit(new BattleId(1), 5, new EntityId(1), 20, 0, new EntityId(2), 10, 0,
                HitContactState.Start, HitImpactKind.Impact, float.NaN, 1);
            var bytes = new byte[160]; var count = SessionCodec.Hit(bytes, hit);
            Assert.Throws<FormatException>(() => ReadHit(bytes, count));
            hit = new ResolvedHit(new BattleId(1), 5, new EntityId(1), 20, 0, new EntityId(2), 10, 0,
                HitContactState.Start, HitImpactKind.Impact, 1, 1);
            count = SessionCodec.Hit(bytes, hit);
            Assert.Throws<FormatException>(() => ReadHit(bytes, count + 1));
        }

        [Test]
        public void QuantizedCoursesUseWholeDegreesWithHysteresis()
        {
            Assert.AreEqual(10, Course(10.4)); Assert.AreEqual(11, Course(10.6));
            Assert.AreEqual(0, Course(359.6)); Assert.AreEqual(0, Course(-0.4)); Assert.AreEqual(359, Course(-1));
            Assert.AreEqual(QuantizedControls.NoCourse, QuantizedControls.From(PilotControls.Create(null, 0, 0), default, false).Course);
            Assert.AreEqual(10, Course(10.74, 10)); Assert.AreEqual(11, Course(10.76, 10));
            Assert.AreEqual(10, Course(9.26, 10)); Assert.AreEqual(9, Course(9.24, 10));
            Assert.AreEqual(0, Course(359.5, 0)); Assert.AreEqual(359, Course(359.2, 0));
            Assert.AreEqual(95, QuantizedControls.QuantizeThrottle(0.75f)); Assert.AreEqual(127, QuantizedControls.QuantizeThrottle(1));
            var controls = new QuantizedControls(90, 127, 5).ToControls();
            Assert.AreEqual(90 * (float)(Math.PI / 180.0), controls.Course); Assert.AreEqual(1, controls.Throttle);
            Assert.AreEqual(5UL, controls.Systems);
        }

        [Test]
        public void InputRecordsRoundTripWithLargeIndicesAgesAndSystems()
        {
            const long baseTick = 9500;
            var expected = new[]
            {
                new ControlRecord(0, baseTick, new QuantizedControls(0, 0, 0)),
                new ControlRecord(70000, baseTick - 5000, new QuantizedControls(359, 127, ulong.MaxValue)),
                new ControlRecord(5, baseTick - 1, new QuantizedControls(QuantizedControls.NoCourse, 64, 3)),
                new ControlRecord(uint.MaxValue, 0, new QuantizedControls(180, 1, 1UL << 63)),
            };
            var bytes = new byte[64]; var w = InputCodec.Begin(bytes, new BattleId(0x1234567890), baseTick);
            foreach (var record in expected) Assert.IsTrue(InputCodec.Write(ref w, baseTick, record));
            var records = new List<ControlRecord>();
            InputCodec.Read(bytes.AsSpan(0, w.Finish()), new BattleId(0x1234567890), records);
            Assert.AreEqual(expected.Length, records.Count);
            for (var i = 0; i < expected.Length; ++i)
            {
                Assert.AreEqual(expected[i].Index, records[i].Index); Assert.AreEqual(expected[i].Tick, records[i].Tick);
                Assert.AreEqual(expected[i].Controls, records[i].Controls);
            }
        }

        [Test]
        public void InvalidRecordsRejectTheWholeDatagram()
        {
            var records = new List<ControlRecord>();
            foreach (var bad in new[] { new QuantizedControls(360, 0, 0), new QuantizedControls(510, 0, 0) })
            {
                var bytes = new byte[64]; var w = InputCodec.Begin(bytes, new BattleId(1), 10);
                InputCodec.Write(ref w, 10, new ControlRecord(1, 10, new QuantizedControls(5, 5, 0)));
                InputCodec.Write(ref w, 10, new ControlRecord(2, 10, bad));
                var length = w.Finish();
                Assert.Throws<FormatException>(() => InputCodec.Read(bytes.AsSpan(0, length), new BattleId(1), records));
                Assert.AreEqual(0, records.Count);
            }
            var future = new byte[64]; var writer = InputCodec.Begin(future, new BattleId(1), 10);
            InputCodec.Write(ref writer, 10, new ControlRecord(1, -1, new QuantizedControls(5, 5, 0))); // age 11 > base tick
            var size = writer.Finish();
            Assert.Throws<FormatException>(() => InputCodec.Read(future.AsSpan(0, size), new BattleId(1), records));
        }

        private static int Course(double degrees, int? held = null) => QuantizedControls.From(
            PilotControls.Create((float)(degrees * Math.PI / 180), 0, 0),
            new QuantizedControls(held ?? 0, 0, 0), held.HasValue).Course;

        private static void ReadOperation(byte[] bytes, int length)
        { var r = new SessionCodec.Reader(bytes.AsSpan(0, length)); SessionCodec.ReadOperation(ref r); r.End(); }
        private static void ReadHit(byte[] bytes, int length)
        { var r = new SessionCodec.Reader(bytes.AsSpan(0, length)); SessionCodec.ReadHit(ref r); r.End(); }
    }
}
