using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    [TestFixture]
    public class SessionCodecTests
    {
        [Test]
        public void EveryRecordKindRoundTripsAndNamesEntitiesByTheirStreamIndex()
        {
            var link = new Link();
            var batch = link.Battle;
            var ship = new EntityId(1234); var drone = new EntityId(5678); var bay = new DroneBayKey(new RosterId(99), 6);
            var pose = new SpawnPose(new Vec2(1.25f, -20.5f), 2.5f, new Vec2(-5, 7), -3);
            var shot = SpawnIdentity.CreateChild(ship, 127, 7, 3, 0);
            var child = SpawnIdentity.CreateChild(shot, 125, 2, 0, 1);
            // Batch tick 125: records 123-127 cover negative, zero and positive tick offsets.
            var operations = new[]
            {
                OwnerOperation.RosterChange(batch, 20, 1, 123, ship,
                    new RosterRecord(new RosterId(99), 4, RosterStatus.Ready, RosterStatus.Active,
                        RosterTransitionReason.Deployed, 2, 5, 22.5f, 9.25f, 0.25f), pose),
                OwnerOperation.DroneLaunch(batch, 20, 2, 124, drone, bay, ship, pose),
                OwnerOperation.BulletFired(batch, 20, 3, 127, shot, ship, 7, 3, 0),
                OwnerOperation.BulletFired(batch, 20, 4, 125, child, shot, 2, 0, 1),
                OwnerOperation.RetreatStarted(batch, 20, 5, 125, ship),
                OwnerOperation.Remove(batch, 20, 6, 125, drone, RemovalReason.Death, bay),
                OwnerOperation.Remove(batch, 20, 7, 126, shot, RemovalReason.Expiry),
            };
            // A removed bullet can still attack in its own batch. An end record is one tick ahead.
            var hits = new[]
            {
                new ResolvedHit(batch, 125, shot, 20, 7, link.Target, 10, 3, HitContactState.Start, HitImpactKind.Energy, 8.125f, 1),
                new ResolvedHit(batch, 126, ship, 20, -1, link.Target, 10, 0, HitContactState.End, HitImpactKind.DamageOverTime, 40, 2),
                new ResolvedHit(batch, 125, child, 20, 2, link.Target, 10, 1, HitContactState.Start, HitImpactKind.Impact, 0, 3),
            };
            var bytes = link.Write(125, operations, hits);
            var scan = new List<SessionCodec.ScannedRecord>();
            var watermark = link.Read(bytes, out var actualOperations, out var actualHits, scan);
            Assert.AreEqual(125, watermark.Tick); Assert.AreEqual(7UL, watermark.OperationSequence);
            Assert.AreEqual(20, watermark.Owner); Assert.AreEqual(batch, watermark.BattleId);
            CollectionAssert.AreEqual(operations, actualOperations);
            CollectionAssert.AreEqual(hits, actualHits);

            // Both peers name the same bodies; the removed bullet has no name after the batch, the child keeps age 0.
            Assert.AreEqual(2, link.ReceiverPeer.Bodies.Count); Assert.AreEqual(bay, link.ReceiverPeer.Bodies[1].Bay);
            Assert.IsFalse(link.ReceiverPeer.TryGetBullet(1, out _)); Assert.IsFalse(link.SenderOwn.TryGetBullet(shot, out _));
            Assert.IsTrue(link.ReceiverPeer.TryGetBullet(0, out var named)); Assert.AreEqual(child, named);

            // Sizes the plan expected: bullet spawn about 5 bytes, removal about 4, hit about 11.
            CollectionAssert.AreEqual(new[] { SessionCodec.RecordKind.Roster, SessionCodec.RecordKind.DroneLaunch,
                SessionCodec.RecordKind.BulletSpawn, SessionCodec.RecordKind.BulletSpawn, SessionCodec.RecordKind.Retreat,
                SessionCodec.RecordKind.Removal, SessionCodec.RecordKind.Removal, SessionCodec.RecordKind.Hit,
                SessionCodec.RecordKind.Hit, SessionCodec.RecordKind.Hit }, scan.ConvertAll(r => r.Kind));
            Assert.LessOrEqual(scan[2].Bits, 40); Assert.LessOrEqual(scan[6].Bits, 32); Assert.LessOrEqual(scan[7].Bits, 88);
            Assert.AreEqual(RemovalReason.Expiry, scan[6].RemovalReason);

            // A later hit cannot name the removed bullet.
            Assert.Throws<InvalidOperationException>(() => link.Write(126, Array.Empty<OwnerOperation>(), new[]
            {
                new ResolvedHit(batch, 126, shot, 20, 7, link.Target, 10, 3, HitContactState.Start, HitImpactKind.Energy, 1, 4),
            }));
        }

        [Test]
        public void BatchesRejectWrongVersionTicksKindsAndUnknownNames()
        {
            var link = new Link();
            // An empty batch is a watermark only.
            var watermark = link.Read(link.Write(long.MaxValue - 6, Array.Empty<OwnerOperation>(), Array.Empty<ResolvedHit>()), out _, out _);
            Assert.AreEqual(long.MaxValue - 6, watermark.Tick); Assert.AreEqual(0UL, watermark.OperationSequence);

            var bytes = new Link().Write(10, new[] { Deploy(link.Battle) }, Array.Empty<ResolvedHit>());
            bytes[0] = 2;
            Assert.Throws<FormatException>(() => new Link().Read(bytes, out _, out _));
            // The header's last sequence must match the records.
            bytes = new Link().Write(10, new[] { Deploy(link.Battle) }, Array.Empty<ResolvedHit>());
            var other = new Link(); other.ReceiverPeer.LastOperation = 1;
            Assert.Throws<FormatException>(() => other.Read(bytes, out _, out _));

            // A record before tick 0, kind 0 and kind 7.
            Assert.Throws<FormatException>(() => new Link().Read(Raw(2, 4, Zigzag(-3)), out _, out _));
            Assert.Throws<FormatException>(() => new Link().Read(Raw(2, 0, 0), out _, out _));
            Assert.Throws<FormatException>(() => new Link().Read(Raw(2, 7, 0), out _, out _));

            // With ship 0 named: a hit on an unknown target index, a NaN amount, a removal of an unknown bullet age,
            // the never-sent reason Unconfirmed, an unknown retreat ship.
            Assert.Throws<FormatException>(() => Deployed().Read(Raw(10, 1, 0, (ref BitWriter w) => Hit(ref w, 5, 1)), out _, out _));
            Assert.Throws<FormatException>(() => Deployed().Read(Raw(10, 1, 0, (ref BitWriter w) => Hit(ref w, 0, float.NaN)), out _, out _));
            Assert.DoesNotThrow(() => Deployed().Read(Raw(10, 1, 0, (ref BitWriter w) => Hit(ref w, 0, 1), 1), out _, out _));
            Assert.Throws<FormatException>(() => Deployed().Read(Raw(10, 4, 0, (ref BitWriter w) =>
            { w.WriteBool(true); w.WriteBits((ulong)RemovalReason.Expiry, 3); w.WriteVarUInt(0); }, 2), out _, out _));
            Assert.Throws<FormatException>(() => Deployed().Read(Raw(10, 4, 0, (ref BitWriter w) =>
            { w.WriteBool(false); w.WriteBits((ulong)RemovalReason.Unconfirmed, 3); w.WriteVarUInt(0); }, 2), out _, out _));
            Assert.Throws<FormatException>(() => Deployed().Read(Raw(10, 6, 0, (ref BitWriter w) => w.WriteVarUInt(1), 2), out _, out _));
            // A roster ship has no bay, so a removal cannot name it.
            Assert.Throws<FormatException>(() => Deployed().Read(Raw(10, 4, 0, (ref BitWriter w) =>
            { w.WriteBool(false); w.WriteBits((ulong)RemovalReason.Death, 3); w.WriteVarUInt(0); }, 2), out _, out _));

            // The sender refuses records its streams cannot name, and entity IDs that are not derived from the record.
            var ship = new EntityId(77);
            Assert.Throws<InvalidOperationException>(() => new Link().Write(10, Array.Empty<OwnerOperation>(), new[]
            { new ResolvedHit(link.Battle, 10, ship, 20, 0, link.Target, 10, 0, HitContactState.Start, HitImpactKind.Impact, 1, 1) }));
            Assert.Throws<InvalidOperationException>(() => new Link().Write(10, new[]
            { OwnerOperation.Remove(link.Battle, 20, 1, 10, ship, RemovalReason.Expiry) }, Array.Empty<ResolvedHit>()));
            var named = new Link(); named.Write(10, new[] { Deploy(link.Battle) }, Array.Empty<ResolvedHit>());
            Assert.Throws<InvalidOperationException>(() => named.Write(11, new[]
            { OwnerOperation.BulletFired(link.Battle, 20, 2, 11, new EntityId(5), ship, 0, 0, 0) }, Array.Empty<ResolvedHit>()));
            Assert.Throws<InvalidOperationException>(() => new Link().Write(10, new[]
            { OwnerOperation.RetreatStarted(link.Battle, 20, 1, 10, ship) }, Array.Empty<ResolvedHit>()));

            Link Deployed()
            {
                var deployed = new Link();
                deployed.Read(deployed.Write(10, new[] { Deploy(link.Battle) }, Array.Empty<ResolvedHit>()), out _, out _);
                return deployed;
            }

            void Hit(ref BitWriter w, ulong target, float amount)
            {
                w.WriteBool(false); w.WriteVarUInt(0); w.WriteVarUInt(target); w.WriteVarUInt(0); w.WriteVarUInt(0); w.WriteBits(0, 4);
                w.WriteBits(unchecked((uint)BitConverter.SingleToInt32Bits(amount)), 32);
            }
        }

        private static OwnerOperation Deploy(BattleId battle) => OwnerOperation.RosterChange(battle, 20, 1, 10, new EntityId(77),
            new RosterRecord(new RosterId(99), 1, RosterStatus.Ready, RosterStatus.Active, RosterTransitionReason.Deployed, 1, 1, 0, 0, 0));

        // Every cut of a batch either throws or reads a prefix of its records; it never misreads one.
        [Test]
        public void TruncatedBatchesNeverMisreadARecord()
        {
            var link = new Link(); var ship = new EntityId(1234);
            var shot = SpawnIdentity.CreateChild(ship, 10, 0, 0, 0);
            var operations = new[]
            {
                OwnerOperation.RosterChange(link.Battle, 20, 1, 10, ship, new RosterRecord(new RosterId(99), 1,
                    RosterStatus.Ready, RosterStatus.Active, RosterTransitionReason.Deployed, 1, 1, 0, 0, 0)),
                OwnerOperation.BulletFired(link.Battle, 20, 2, 10, shot, ship, 0, 0, 0),
            };
            var hits = new[] { new ResolvedHit(link.Battle, 10, shot, 20, 0, link.Target, 10, 0, HitContactState.Start, HitImpactKind.Energy, 3, 1) };
            var bytes = link.Write(10, operations, hits);
            for (var size = 0; size < bytes.Length; ++size)
            {
                var cut = new Link();
                try { cut.Read(bytes.AsSpan(0, size).ToArray(), out var ops, out var read); CollectionAssert.AreEqual(operations, ops); Assert.AreEqual(0, read.Count); }
                catch (FormatException) { }
            }
        }

        // A sender (owner 20) and a receiver (owner 10) with one ship of the receiver's stream deployed on both sides.
        private sealed class Link
        {
            internal readonly BattleId Battle = new BattleId(0x123456789abcdef0);
            internal readonly EntityId Target = new EntityId(42);
            internal readonly StreamNames SenderOwn = Names(), SenderPeer = Names(), ReceiverPeer = Names(), ReceiverOwn = Names();
            private readonly List<OwnerOperation> _operations = new List<OwnerOperation>();
            private readonly List<ResolvedHit> _hits = new List<ResolvedHit>();

            internal Link()
            {
                var deploy = OwnerOperation.RosterChange(Battle, 10, 1, 0, Target, new RosterRecord(new RosterId(1), 1,
                    RosterStatus.Ready, RosterStatus.Active, RosterTransitionReason.Deployed, 1, 1, 0, 0, 0));
                SenderPeer.AddBody(deploy); ReceiverOwn.AddBody(deploy);
            }

            internal byte[] Write(long tick, OwnerOperation[] operations, ResolvedHit[] hits)
            {
                var buffer = new byte[SessionCodec.Capacity(operations.Length + hits.Length)];
                var length = SessionCodec.WriteBatch(buffer, tick, operations, hits, SenderOwn, SenderPeer);
                return buffer.AsSpan(0, length).ToArray();
            }

            internal OwnerWatermark Read(byte[] bytes, out List<OwnerOperation> operations, out List<ResolvedHit> hits,
                List<SessionCodec.ScannedRecord> scan = null)
            {
                operations = _operations; hits = _hits;
                SessionCodec.ReadBatch(bytes, Battle, 20, 10, null, null, null, null, scan);
                return SessionCodec.ReadBatch(bytes, Battle, 20, 10, ReceiverPeer, ReceiverOwn, _operations, _hits);
            }

            private static StreamNames Names() => new StreamNames((in OwnerOperation _) => null);
        }

        private static ulong Zigzag(long offset) => unchecked((ulong)((offset << 1) ^ (offset >> 63)));

        // A hand-written batch: header, then one record of the kind with the tick offset code and fields.
        private static byte[] Raw(long tick, ulong kind, ulong offset, BitAction fields = null, ulong lastOperation = 1)
        {
            var bytes = new byte[64]; var w = new BitWriter(bytes);
            w.WriteBits(SessionCodec.Version, 8); w.WriteVarUInt((ulong)tick); w.WriteVarUInt(lastOperation);
            w.WriteBits(kind, 3); w.WriteVarUInt(offset);
            fields?.Invoke(ref w);
            return bytes.AsSpan(0, w.Finish()).ToArray();
        }

        private delegate void BitAction(ref BitWriter w);

        [Test]
        public void CorrectionsKeepBodiesOnTheirGridsAndEscapeFastVelocities()
        {
            var bodies = CorrectionBodies();
            var batch = new CorrectionBatch(); batch.Begin(new BattleId(0x500000001), 20, 50, 123456);
            batch.Add(bodies[0].Entity, new SpawnPose(new Vec2(199.9999f, 0.3f), -0.25f, new Vec2(2.5f, -3.3f), 1.7f),
                ResourceLevel.Partial(1, false), ResourceLevel.Full, ResourceLevel.Partial(255, true), ReadOnlySpan<PointDefenseTimers>.Empty);
            batch.Add(bodies[2].Entity, new SpawnPose(new Vec2(12.345f, 187.5f), 7f, new Vec2(500f, -127.99f), -40f),
                ResourceLevel.Full, ResourceLevel.Full, ResourceLevel.Partial(0, false),
                new[] { new PointDefenseTimers(3, PointDefenseTimers.Ready), PointDefenseTimers.AllReady });
            var bytes = new byte[100]; var arena = new CorrectionCodec.Arena(200, 200);
            Assert.AreEqual(18, arena.XBits);
            var w = CorrectionCodec.Begin(bytes, batch.BattleId, batch.Tick, batch.AppliedHitSequence);
            var start = w.BitPosition;
            Assert.IsTrue(CorrectionCodec.Write(ref w, arena, 0, batch.Bodies[0], bodies[0].Definition, batch.GetTimers(batch.Bodies[0])));
            Assert.AreEqual(CorrectionCodec.BodyBits(arena, 0, batch.Bodies[0], bodies[0].Definition,
                batch.GetTimers(batch.Bodies[0])), w.BitPosition - start);
            Assert.IsTrue(CorrectionCodec.Write(ref w, arena, 1, batch.Bodies[1], bodies[2].Definition, batch.GetTimers(batch.Bodies[1])));
            var length = w.Finish();

            var actual = new CorrectionBatch(); var timers = Array.Empty<PointDefenseTimers>();
            CorrectionCodec.Read(bytes.AsSpan(0, length), batch.BattleId, 20, arena, bodies, actual, ref timers);
            Assert.AreEqual(batch.BattleId, actual.BattleId); Assert.AreEqual(20, actual.Owner);
            Assert.AreEqual(batch.Tick, actual.Tick); Assert.AreEqual(batch.AppliedHitSequence, actual.AppliedHitSequence);
            Assert.AreEqual(2, actual.Bodies.Length);
            for (var i = 0; i < 2; ++i)
            {
                var expected = batch.Bodies[i]; var body = actual.Bodies[i];
                Assert.AreEqual(expected.EntityId, body.EntityId);
                Assert.AreEqual(expected.Armor, body.Armor); Assert.AreEqual(expected.Shield, body.Shield);
                Assert.AreEqual(expected.Energy, body.Energy);
                CollectionAssert.AreEqual(batch.GetTimers(expected).ToArray(), actual.GetTimers(body).ToArray());
            }
            // The first body wraps to cell 0 and stays inside one grid step; the second escapes vx and spin.
            var first = actual.Bodies[0].Motion;
            Assert.AreEqual(0f, first.Position.X); Assert.AreEqual(0.3f, first.Position.Y, 0.5f / 1024);
            Assert.AreEqual(2 * Math.PI - 0.25, first.Angle, Math.PI / 16384);
            Assert.AreEqual(2.5f, first.LinearVelocity.X, 0.5f / 128); Assert.AreEqual(-3.3f, first.LinearVelocity.Y, 0.5f / 128);
            Assert.AreEqual(1.7f, first.AngularVelocity, 0.5f / 512);
            var second = actual.Bodies[1].Motion;
            Assert.AreEqual(500f, second.LinearVelocity.X); Assert.AreEqual(-40f, second.AngularVelocity);
            Assert.AreEqual(-127.99f, second.LinearVelocity.Y, 0.5f / 128);
            Assert.AreEqual(7 - 2 * Math.PI, second.Angle, Math.PI / 16384);

            // A drone without shield, with energy in use, costs 106 bits (about 13 bytes); the plan expected about 15.
            var drone = new SpawnPose(new Vec2(100, 100), 1, new Vec2(3, 4), 0.5f);
            batch.Begin(batch.BattleId, 20, 50, 0);
            batch.Add(bodies[0].Entity, drone, ResourceLevel.Full, ResourceLevel.Full, ResourceLevel.Partial(200, true),
                ReadOnlySpan<PointDefenseTimers>.Empty);
            Assert.AreEqual(106, CorrectionCodec.BodyBits(arena, 0, batch.Bodies[0], bodies[0].Definition, ReadOnlySpan<PointDefenseTimers>.Empty));
        }

        [Test]
        public void MalformedCorrectionsAreRejectedAndUnknownIndicesEndTheDatagram()
        {
            var bodies = CorrectionBodies(); var arena = new CorrectionCodec.Arena(200, 200);
            var batch = new CorrectionBatch(); batch.Begin(new BattleId(9), 20, 50, 0);
            batch.Add(bodies[0].Entity, new SpawnPose(new Vec2(1, 2), 0, Vec2.Zero), ResourceLevel.Full,
                ResourceLevel.Full, ResourceLevel.Partial(9, true), ReadOnlySpan<PointDefenseTimers>.Empty);
            batch.Add(bodies[2].Entity, new SpawnPose(new Vec2(3, 4), 0, Vec2.Zero), ResourceLevel.Full,
                ResourceLevel.Full, ResourceLevel.Full, new[] { new PointDefenseTimers(1, 2), PointDefenseTimers.AllReady });
            var bytes = new byte[64];
            var w = CorrectionCodec.Begin(bytes, batch.BattleId, 50, 0);
            CorrectionCodec.Write(ref w, arena, 0, batch.Bodies[0], bodies[0].Definition, ReadOnlySpan<PointDefenseTimers>.Empty);
            CorrectionCodec.Write(ref w, arena, 1, batch.Bodies[1], bodies[2].Definition, batch.GetTimers(batch.Bodies[1]));
            var length = w.Finish();
            Assert.Throws<FormatException>(() => Read(bytes, length, new BattleId(10)));
            // A cut that ends in zero bits at a body boundary reads as padding: it may lose bodies, never misread one.
            for (var size = 1; size < length; ++size)
            {
                var into = new CorrectionBatch(); var scratch = Array.Empty<PointDefenseTimers>();
                try { CorrectionCodec.Read(bytes.AsSpan(0, size), new BattleId(9), 20, arena, bodies, into, ref scratch); }
                catch (FormatException) { continue; }
                Assert.Less(into.Bodies.Length, 2, $"length {size}");
                if (into.Bodies.Length == 1) Assert.AreEqual(bodies[0].Entity, into.Bodies[0].EntityId);
            }

            // Index 1 has no spawn record: reading stops after the first body, without an error.
            w = CorrectionCodec.Begin(bytes, batch.BattleId, 50, 0);
            CorrectionCodec.Write(ref w, arena, 0, batch.Bodies[0], bodies[0].Definition, ReadOnlySpan<PointDefenseTimers>.Empty);
            CorrectionCodec.Write(ref w, arena, 0, batch.Bodies[0], bodies[0].Definition, ReadOnlySpan<PointDefenseTimers>.Empty);
            length = w.Finish();
            var actual = new CorrectionBatch(); var timers = Array.Empty<PointDefenseTimers>();
            CorrectionCodec.Read(bytes.AsSpan(0, length), new BattleId(9), 20, arena, bodies, actual, ref timers);
            Assert.AreEqual(1, actual.Bodies.Length);

            // A position cell past the arena.
            w = CorrectionCodec.Begin(bytes, batch.BattleId, 50, 0);
            w.WriteVarUInt(0); w.WriteBits((1UL << 18) - 1, 18); w.WriteBits(0, 18 + 14);
            w.WriteBits(1UL << 14, 15); w.WriteBits(1UL << 14, 15); w.WriteBits(1UL << 13, 14); w.WriteBits(7, 3);
            length = w.Finish();
            Assert.Throws<FormatException>(() => Read(bytes, length, new BattleId(9)));
            bytes[0] = 2;
            Assert.Throws<FormatException>(() => Read(bytes, length, new BattleId(9)));

            void Read(byte[] data, int size, BattleId battle)
            {
                var into = new CorrectionBatch(); var scratch = Array.Empty<PointDefenseTimers>();
                CorrectionCodec.Read(data.AsSpan(0, size), battle, 20, arena, bodies, into, ref scratch);
            }
        }

        // Index 0: shield 0 and energy without recharge, no devices. Index 1: none. Index 2: two point-defense devices.
        private static List<ControlledBody> CorrectionBodies()
        {
            var engine = new EngineDefinition(20, 10, 5, 2);
            var plain = new ShipDefinition(1, 1, engine, 1000, new ResourceDefinition(0), new ResourceDefinition(100, 5, 1));
            var defended = new ShipDefinition(1, 1, engine, 1000, new ResourceDefinition(500, 10, 1), new ResourceDefinition(300),
                devices: new DeviceDefinition[] { new PointDefenseDefinition(3, 0.2f, 0), new PointDefenseDefinition(3, 0.2f, 0) });
            return new List<ControlledBody>
            {
                new ControlledBody(new EntityId(11), plain), new ControlledBody(new EntityId(12), null),
                new ControlledBody(new EntityId(13), defended),
            };
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
    }
}
