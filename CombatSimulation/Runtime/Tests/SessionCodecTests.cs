using System;
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
            var bytes = new byte[160];
            var count = SessionCodec.Input(bytes, new BattleId(1), 20, 5, PilotControls.Create(0, float.NaN, 0));
            Assert.Throws<FormatException>(() => ReadInput(bytes, count));
            count = SessionCodec.Input(bytes, new BattleId(1), 20, 5, PilotControls.Create(0, 1, 0));
            Assert.Throws<FormatException>(() => ReadInput(bytes, count + 1));
        }

        private static void ReadOperation(byte[] bytes, int length)
        { var r = new SessionCodec.Reader(bytes.AsSpan(0, length)); SessionCodec.ReadOperation(ref r); r.End(); }
        private static void ReadInput(byte[] bytes, int length)
        { var r = new SessionCodec.Reader(bytes.AsSpan(0, length)); SessionCodec.ReadInput(ref r); r.End(); }
    }
}
