using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Network plan, step 4: the receiver's bullet set converges on the owner's set.
    public class FireConfirmationTests
    {
        private const int Owner = 20;

        private static readonly ShipDefinition Cannon = Fixtures.CannonShip(energy: new ResourceDefinition(100000f));
        private static readonly ShipDefinition Beam = Fixtures.ShipWith(energy: new ResourceDefinition(100000f),
            barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
            devices: new[] { new BeamDefinition(0, 0f, 1f, 1f, 4f, 8f) });

        private static BattleSetup Setup(int localOwner, ShipDefinition ship) => new BattleSetup(new BattleId(77), 13,
            new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, ship, Owner),
            }, localOwners: new[] { localOwner });

        // The owner deploys slot 101; the receiver applies the deployment in the same tick; both then reach tick 2.
        private static (Driver owner, Driver remote, Link link) Pair(ShipDefinition ship)
        {
            var owner = new Driver(Setup(Owner, ship));
            var remote = new Driver(Setup(10, ship));
            var link = new Link();
            owner.Deploy(101);
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();

            // The barrel is ready 3 ticks after the tick before deployment: the first shot can fire at tick 2.
            Tick(owner, remote, link);
            return (owner, remote, link);
        }

        // One tick on both devices. A delivered record reaches the receiver before it runs the same tick.
        private static void Tick(Driver owner, Driver remote, Link link, bool deliver = true)
        {
            owner.Step();
            link.Capture(owner);
            if (deliver) link.Deliver(remote);
            remote.Step();
        }

        private static int Find(Driver driver, EntityId id)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && bullets[i].Id == id) return i;
            return -1;
        }

        private static bool Has(Driver driver, EntityId id) => Find(driver, id) >= 0;

        private static Vec2 Position(Driver driver, EntityId id) =>
            driver.Battle.World.GetBulletPosition(driver.Battle.Bullets[Find(driver, id)].Body);

        private static EntityId[] Ids(Driver driver) =>
            driver.Battle.Snapshot.Bullets.ToArray().Select(b => b.Id).OrderBy(id => id.Value).ToArray();

        private static EntityId Shot(Driver owner, long tick, int device = 0) =>
            SpawnIdentity.CreateChild(owner.Entity(101), tick, (uint)device, 0, 0);

        private static BattleEvent[] Events(Driver driver, EntityId id) =>
            driver.Battle.Snapshot.Events.ToArray().Where(e => e.EntityId == id).ToArray();

        [Test]
        public void MatchingShotsAreConfirmedWithoutChanges()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1);
            var removals = 0;
            var spawns = 0;
            for (var i = 0; i < 30; ++i)
            {
                Tick(owner, remote, link);
                foreach (var e in remote.Battle.Snapshot.Events)
                    if (e.Kind == BattleEventKind.Removal && e.RemovalReason == RemovalReason.Unconfirmed) ++removals;
                foreach (var o in remote.Battle.Snapshot.AppliedOperations)
                    if (o.Kind == OwnerOperationKind.BulletSpawn) ++spawns;
            }

            Assert.Greater(spawns, 2);
            Assert.AreEqual(0, removals);
            CollectionAssert.AreEqual(Ids(owner), Ids(remote));
            foreach (var id in Ids(remote))
            {
                Assert.IsTrue(remote.Battle.Bullets[Find(remote, id)].Confirmed);
                Assert.AreEqual(Position(owner, id), Position(remote, id));
            }
        }

        [Test]
        public void OwnerCapturesEachShotAndSinglePlayerCapturesNothing()
        {
            var owner = new Driver(Setup(Owner, Cannon));
            owner.Deploy(101);
            owner.Step();
            owner.Step();
            owner.Control(101, systems: 1);
            owner.Step();
            var fired = owner.Battle.Snapshot.OutgoingOperations.ToArray().Single();
            Assert.AreEqual(OwnerOperationKind.BulletSpawn, fired.Kind);
            Assert.AreEqual(Shot(owner, 2), fired.EntityId);
            Assert.AreEqual(owner.Entity(101), fired.ParentId);
            Assert.AreEqual(2UL, fired.Sequence);

            var single = new Driver(Fixtures.Setup(Fixtures.CannonShip()));
            single.Deploy(1);
            single.Step();
            single.Step();
            single.Control(1, systems: 1);
            single.Step();
            Assert.AreEqual(1, single.Battle.Snapshot.Bullets.Length);
            Assert.IsEmpty(single.Battle.Snapshot.OutgoingOperations.ToArray());
        }

        [Test]
        public void ShotFiredOnlyOnTheReceiverIsRemovedWhenTheWatermarkCoversIt()
        {
            var (owner, remote, link) = Pair(Cannon);
            remote.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            var shot = Shot(owner, 2);
            Assert.IsTrue(Has(remote, shot));
            Assert.IsFalse(Has(owner, shot));

            Tick(owner, remote, link, deliver: false);
            Assert.IsTrue(Has(remote, shot), "No watermark yet.");
            link.Deliver(remote);
            remote.Step();
            Assert.IsFalse(Has(remote, shot));
            var removal = Events(remote, shot).Single();
            Assert.AreEqual(RemovalReason.Unconfirmed, removal.RemovalReason);
            Assert.AreEqual(BattleEventAuthority.Predicted, removal.Authority);
            Assert.AreEqual(3, remote.Battle.ConfirmedThrough(Owner));
        }

        [Test]
        public void UnconfirmedShotLeavesInItsSpawnTickWhenTheRecordsAreOnTime()
        {
            var (owner, remote, link) = Pair(Cannon);
            remote.Control(101, systems: 1);
            Tick(owner, remote, link);
            var shot = Shot(owner, 2);
            var events = Events(remote, shot);
            Assert.AreEqual(2, events.Length);
            Assert.AreEqual(BattleEventKind.Spawn, events[0].Kind);
            Assert.AreEqual(RemovalReason.Unconfirmed, events[1].RemovalReason);
            Assert.IsFalse(Has(remote, shot));
            Assert.AreEqual(0, remote.Battle.PredictedRemovalCount);
        }

        [Test]
        public void MissedShotIsAddedAtTheOwnersSpawnPoseAndAdvanced()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Place(101, new Vec2(40f, 60f), new Vec2(5f, -3f), 0.7f);
            remote.Place(101, new Vec2(40f, 60f), new Vec2(5f, -3f), 0.7f);
            owner.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            owner.Control(101, systems: 0);
            for (var i = 0; i < 6; ++i) Tick(owner, remote, link, deliver: false);

            var shot = Shot(owner, 2);
            Assert.IsFalse(Has(remote, shot));
            link.Deliver(remote);
            owner.Step();
            remote.Step();
            Assert.IsTrue(Has(remote, shot));
            Assert.IsTrue(remote.Battle.Bullets[Find(remote, shot)].Confirmed);
            Assert.AreEqual(Position(owner, shot), Position(remote, shot));
            var spawn = Events(remote, shot).Single();
            Assert.AreEqual(BattleEventAuthority.Committed, spawn.Authority);
            Assert.AreEqual(9, spawn.Tick);
        }

        // P1: the receiver's copy of the shooter aims 0.05 rad off; the owner's launch moves the copy onto the owner's line.
        [Test]
        public void PredictedShotIsReaimedOnTheOwnersLaunch()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Place(101, new Vec2(40f, 60f), new Vec2(5f, -3f), 0.7f);
            remote.Place(101, new Vec2(40f, 60f), new Vec2(5f, -3f), 0.75f);
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            owner.Control(101, systems: 0);
            remote.Control(101, systems: 0);
            for (var i = 0; i < 6; ++i) Tick(owner, remote, link, deliver: false);

            var shot = Shot(owner, 2);
            var fired = link.Held.Single(o => o.Kind == OwnerOperationKind.BulletSpawn);
            Assert.IsTrue(fired.HasLaunchPose);
            Assert.AreNotEqual(Position(owner, shot), Position(remote, shot));
            link.Deliver(remote);
            owner.Step();
            remote.Step();
            Assert.AreEqual(Position(owner, shot), Position(remote, shot));
            var world = remote.Battle.World;
            Assert.AreEqual(owner.Battle.World.GetBulletVelocity(owner.Battle.Bullets[Find(owner, shot)].Body),
                world.GetBulletVelocity(remote.Battle.Bullets[Find(remote, shot)].Body));
            Assert.IsEmpty(Events(remote, shot), "The re-aim adds no event.");
        }

        [Test]
        public void SpawnOlderThanTheHistoryUsesTheOldestSample()
        {
            var (owner, remote, link) = Pair(Cannon);
            for (var i = 0; i < 20; ++i) Tick(owner, remote, link);
            owner.Place(101, new Vec2(40f, 60f), new Vec2(5f, 0f));
            remote.Place(101, new Vec2(40f, 60f), new Vec2(5f, 0f));
            owner.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            var spawnTick = owner.Tick - 1;
            owner.Control(101, systems: 0);
            while (owner.Tick < spawnTick + 20) Tick(owner, remote, link, deliver: false);

            // Stage 1 of tick S + 20 holds samples S + 4 .. S + 19: the muzzle is 4 ticks of ship motion ahead. Only a
            // record without a launch pose (a homing projectile) reads the muzzle from the history, so drop the pose.
            for (var i = 0; i < link.Held.Count; ++i)
            {
                var o = link.Held[i];
                if (o.Kind == OwnerOperationKind.BulletSpawn)
                    link.Held[i] = OwnerOperation.BulletFired(o.BattleId, o.Owner, o.Sequence, o.Tick, o.EntityId, o.ParentId,
                        o.DeviceIndex, o.BarrelIndex, o.SpawnSequence);
            }
            link.Deliver(remote);
            owner.Step();
            remote.Step();
            var shot = Shot(owner, spawnTick);
            var offset = Position(remote, shot) - Position(owner, shot);
            Assert.AreEqual(4 * Battle.TickDuration * 5f, offset.X, 1e-3f);
            Assert.AreEqual(0f, offset.Y, 1e-4f);
        }

        [Test]
        public void ShotThatSpawnsAndDiesBetweenDeliveriesIsNeverCreated()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            owner.Control(101, systems: 0);
            var shot = Shot(owner, 2);
            owner.Battle.Bullets.MarkRemoval(owner.Battle.Bullets.GetHandle(Find(owner, shot)), RemovalReason.Disposal);
            Tick(owner, remote, link, deliver: false);
            Tick(owner, remote, link, deliver: false);

            link.Deliver(remote);
            remote.Step();
            Assert.IsFalse(Has(remote, shot));
            Assert.IsEmpty(Events(remote, shot));
            var applied = remote.Battle.Snapshot.AppliedOperations.ToArray().Where(o => o.EntityId == shot).ToArray();
            Assert.AreEqual(2, applied.Length);
        }

        [Test]
        public void PredictedRemovalIsNotUndoneByALaterConfirmation()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1);
            remote.Step();
            var shot = Shot(owner, 2);
            remote.Battle.Bullets.MarkRemoval(remote.Battle.Bullets.GetHandle(Find(remote, shot)), RemovalReason.Death);
            remote.Control(101, systems: 0);
            remote.Step();
            Assert.IsFalse(Has(remote, shot));
            Assert.AreEqual(1, remote.Battle.PredictedRemovalCount);

            owner.Step();
            link.Capture(owner);
            owner.Control(101, systems: 0);
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();
            Assert.IsFalse(Has(remote, shot));
            Assert.IsEmpty(Events(remote, shot));
            Assert.AreEqual(0, remote.Battle.PredictedRemovalCount);
        }

        [Test]
        public void ExpiredShotRecordAddsNothing()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            var fired = owner.Battle.Snapshot.OutgoingOperations[0];
            owner.Control(101, systems: 0);
            link.Held.Clear();
            remote.Step(Fixtures.Bolt.LifetimeTicks + 1);

            Assert.IsTrue(remote.Battle.Submit(Owner, fired));
            remote.Step();
            Assert.IsFalse(Has(remote, fired.EntityId));
            Assert.IsEmpty(Events(remote, fired.EntityId));
            Assert.AreEqual(fired.Sequence, remote.Battle.Snapshot.AppliedOperations[0].Sequence);
        }

        [Test]
        public void WatermarkWaitsForItsMissingOperations()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1);
            owner.Step();
            var fired = owner.Battle.Snapshot.OutgoingOperations.ToArray().Single();
            var watermark = new OwnerWatermark(owner.BattleId, Owner, owner.Battle.Snapshot.Tick, fired.Sequence);
            Assert.IsTrue(remote.Battle.Submit(Owner, watermark));
            Assert.IsFalse(remote.Battle.Submit(Owner, watermark));
            remote.Step();
            Assert.IsTrue(Has(remote, fired.EntityId));
            Assert.AreEqual(1, remote.Battle.ConfirmedThrough(Owner));

            Assert.IsTrue(remote.Battle.Submit(Owner, fired));
            remote.Step();
            Assert.IsTrue(Has(remote, fired.EntityId));
            Assert.IsTrue(remote.Battle.Bullets[Find(remote, fired.EntityId)].Confirmed);
            Assert.AreEqual(2, remote.Battle.ConfirmedThrough(Owner));
        }

        [Test]
        public void InvalidRecordsAreRejected()
        {
            var (owner, remote, _) = Pair(Cannon);
            var parent = owner.Entity(101);
            var good = SpawnIdentity.CreateChild(parent, 1, 0, 0, 0);
            Assert.IsFalse(remote.Battle.Submit(Owner, OwnerOperation.BulletFired(remote.BattleId, Owner, 5, 1, good, parent, 0, 0, 1)));
            Assert.IsFalse(remote.Battle.Submit(Owner, OwnerOperation.BulletFired(remote.BattleId, Owner, 5, 1, good, default, 0, 0, 0)));
            Assert.IsFalse(remote.Battle.Submit(10, OwnerOperation.BulletFired(remote.BattleId, 10, 5, 1, good, parent, 0, 0, 0)));
            Assert.IsFalse(remote.Battle.Submit(Owner, new OwnerWatermark(remote.BattleId, 10, 1, 0)));
            Assert.IsFalse(remote.Battle.Submit(Owner, new OwnerWatermark(new BattleId(1), Owner, 1, 0)));

            // A barrel that does not belong to the device fails when the record applies.
            var wrongBarrel = SpawnIdentity.CreateChild(parent, 1, 0, 1, 0);
            var record = OwnerOperation.BulletFired(remote.BattleId, Owner, 5, 1, wrongBarrel, parent, 0, 1, 0);
            Assert.IsTrue(remote.Battle.Submit(Owner, record));
            remote.Step();
            Assert.IsFalse(Has(remote, wrongBarrel));
            Assert.IsEmpty(remote.Battle.Snapshot.AppliedOperations.ToArray());
        }

        // P2: an overdue fire input fires the copy 1 tick late with another ID. The confirmation renames the copy and
        // moves it onto the owner's launch; no bullet is removed or added.
        [Test]
        public void PredictedShotFiredLateIsAdopted()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Place(101, new Vec2(40f, 60f), new Vec2(5f, -3f), 0.7f);
            remote.Place(101, new Vec2(40f, 60f), new Vec2(5f, -3f), 0.7f);
            owner.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            owner.Control(101, systems: 0);
            remote.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            remote.Control(101, systems: 0);
            Tick(owner, remote, link, deliver: false);

            var shot = Shot(owner, 2);
            var copy = Shot(owner, 3);
            Assert.IsTrue(Has(remote, copy));
            var body = remote.Battle.Bullets[Find(remote, copy)].Body;
            link.Deliver(remote);
            owner.Step();
            remote.Step();

            Assert.IsFalse(Has(remote, copy));
            var index = Find(remote, shot);
            ref readonly var adopted = ref remote.Battle.Bullets[index];
            Assert.AreEqual(body, adopted.Body);
            Assert.IsTrue(adopted.Confirmed);
            Assert.AreEqual(2, adopted.SpawnTick);
            Assert.AreEqual(owner.Battle.Bullets[Find(owner, shot)].ExpiryTick, adopted.ExpiryTick);
            Assert.AreEqual(Position(owner, shot), Position(remote, shot));
            Assert.IsEmpty(Events(remote, copy));
            var rename = Events(remote, shot).Single();
            Assert.AreEqual(BattleEventKind.Rename, rename.Kind);
            Assert.AreEqual(copy, rename.PreviousId);
            Assert.AreEqual(BattleEventAuthority.Committed, rename.Authority);
            CollectionAssert.AreEqual(Ids(owner), Ids(remote));
        }

        // A copy outside the window (shorter than the shortest spacing of two shots) may be another shot: it is not adopted.
        [Test]
        public void ShotOutsideTheWindowIsNotAdopted()
        {
            var (owner, remote, link) = Pair(Cannon);
            owner.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            owner.Control(101, systems: 0);
            for (var i = 0; i < 4; ++i) Tick(owner, remote, link, deliver: false);
            remote.Control(101, systems: 1);
            Tick(owner, remote, link, deliver: false);
            remote.Control(101, systems: 0);

            var shot = Shot(owner, 2);
            var copy = Shot(owner, 7);
            Assert.IsTrue(Has(remote, copy));
            link.Deliver(remote);
            owner.Step();
            remote.Step();
            Assert.IsTrue(Has(remote, shot));
            Assert.IsFalse(Has(remote, copy));
            Assert.AreEqual(BattleEventKind.Spawn, Events(remote, shot).Single().Kind);
            Assert.AreEqual(RemovalReason.Unconfirmed, Events(remote, copy).Single().RemovalReason);
        }

        // P2 for a beam: the device keeps its attached beam, now under the owner's ID, and the owner's removal stops it.
        // The beam's start tick may be any distance from the owner's: the device holds one beam.
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(-4)]
        public void PredictedBeamIsAdopted(int late)
        {
            var (owner, remote, link) = Pair(Beam);
            for (var tick = 2; tick <= 2 + Math.Abs(late); ++tick)
            {
                if (tick == 2) (late > 0 ? owner : remote).Control(101, systems: 1);
                if (tick == 2 + Math.Abs(late)) (late > 0 ? remote : owner).Control(101, systems: 1);
                Tick(owner, remote, link, deliver: false);
            }

            var fired = Shot(owner, late > 0 ? 2 : 2 - late);
            var copy = Shot(owner, late > 0 ? 2 + late : 2);
            var device = (BeamDevice)remote.Ship(101).GetDevice(0);
            Assert.IsTrue(device.TryGetBeam(out var before));
            link.Deliver(remote);
            owner.Step();
            link.Capture(owner);
            remote.Step();

            Assert.IsTrue(device.TryGetBeam(out var after));
            Assert.AreEqual(before, after);
            Assert.AreEqual(fired, remote.Battle.Bullets[after.Index].Id);
            Assert.IsFalse(Has(remote, copy));
            Assert.AreEqual(copy, Events(remote, fired).Single(e => e.Kind == BattleEventKind.Rename).PreviousId);

            owner.Control(101, systems: 0);
            Tick(owner, remote, link);
            Assert.IsFalse(Has(remote, fired));
            Assert.IsFalse(device.TryGetBeam(out _));
            Assert.AreEqual(BattleEventAuthority.Committed, Events(remote, fired).Single().Authority);
        }

        [Test]
        public void ConfirmationChurnAllocatesNothingAfterWarmup()
        {
            var (owner, remote, link) = Pair(Cannon);
            remote.Control(101, systems: 1);
            for (var i = 0; i < 100; ++i) ChurnTick(owner, remote, link, i);
            var removals = 0;
            var confirmations = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 100; i < 300; ++i)
            {
                ChurnTick(owner, remote, link, i);
                foreach (var e in remote.Battle.Snapshot.Events)
                    if (e.RemovalReason == RemovalReason.Unconfirmed) ++removals;
                foreach (var o in remote.Battle.Snapshot.AppliedOperations)
                    if (o.Kind == OwnerOperationKind.BulletSpawn) ++confirmations;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Greater(removals, 0);
            Assert.Greater(confirmations, 0);
            Assert.AreEqual(0, allocated);
        }

        // The owner fires in alternate 20-tick periods; the receiver's copy fires throughout.
        private static void ChurnTick(Driver owner, Driver remote, Link link, int step)
        {
            if (step % 20 == 0) owner.Control(101, systems: step % 40 == 0 ? 1UL : 0UL);
            Tick(owner, remote, link, deliver: step % 3 == 2);
        }

        // Owner records on the reliable ordered stream, followed by a watermark.
        private sealed class Link
        {
            public readonly List<OwnerOperation> Held = new List<OwnerOperation>(256);
            private ulong _lastSequence;
            private long _through = -1;

            public void Capture(Driver owner)
            {
                foreach (var operation in owner.Battle.Snapshot.OutgoingOperations)
                {
                    Held.Add(operation);
                    _lastSequence = operation.Sequence;
                }
                _through = owner.Battle.Snapshot.Tick;
            }

            public void Deliver(Driver remote)
            {
                foreach (var operation in Held)
                    if (!remote.Battle.Submit(operation.Owner, operation)) throw new InvalidOperationException("Rejected record.");
                Held.Clear();
                if (!remote.Battle.Submit(Owner, new OwnerWatermark(remote.BattleId, Owner, _through, _lastSequence)))
                    throw new InvalidOperationException("Rejected watermark.");
            }
        }
    }
}
