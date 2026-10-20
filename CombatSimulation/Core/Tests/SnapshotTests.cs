using System;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class SnapshotTests
    {
        [Test]
        public void ConstructorPublishesTickMinusOneWithReadySlots()
        {
            var battle = new Battle(Fixtures.Setup(2, 1, battleId: 9));
            var snapshot = battle.Snapshot;

            Assert.AreEqual(new BattleId(9), snapshot.BattleId);
            Assert.AreEqual(-1L, snapshot.Tick);
            Assert.AreEqual(0, snapshot.Ships.Length);
            Assert.AreEqual(0, snapshot.Events.Length);
            Assert.IsFalse(snapshot.IsCompleted);
            Assert.AreEqual(3, snapshot.Roster.Length);
            Assert.AreEqual(new RosterId(1), snapshot.Roster[0].RosterId);
            Assert.AreEqual(new RosterId(2), snapshot.Roster[1].RosterId);
            Assert.AreEqual(new RosterId(101), snapshot.Roster[2].RosterId);
            foreach (var slot in snapshot.Roster)
            {
                Assert.AreEqual(RosterStatus.Ready, slot.Status);
                Assert.IsTrue(slot.ActiveEntity.IsNull);
            }
        }

        [Test]
        public void DeployPublishesShipAndSlotState()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Step();

            var snapshot = driver.Battle.Snapshot;
            var entity = driver.Ship(1);
            var world = driver.Battle.World;
            Assert.AreEqual(0L, snapshot.Tick);
            Assert.AreEqual(1, snapshot.Ships.Length);

            var ship = snapshot.Ships[0];
            Assert.AreEqual(entity.Id, ship.Id);
            Assert.AreEqual(new RosterId(1), ship.RosterId);
            Assert.AreEqual(0, ship.Side);
            Assert.AreEqual(0, ship.Owner);
            Assert.AreSame(Fixtures.Ship, ship.Definition);
            Assert.AreEqual(world.GetPosition(entity.Body), ship.Position);
            Assert.AreEqual(world.GetAngle(entity.Body), ship.Angle);
            Assert.AreEqual(world.GetLinearVelocity(entity.Body), ship.LinearVelocity);
            Assert.AreEqual(world.GetAngularVelocity(entity.Body), ship.AngularVelocity);
            Assert.AreEqual(1000f, ship.Armor.Value);
            Assert.AreEqual(1000f, ship.Armor.Max);
            Assert.AreEqual(500f, ship.Shield.Max);
            Assert.AreEqual(300f, ship.Energy.Value);
            Assert.IsTrue(ship.Energy.Exists);
            Assert.IsFalse(ship.IsRetreating);
            Assert.IsTrue(snapshot.TryGetShip(entity.Id, out var found));
            Assert.AreEqual(entity.Id, found.Id);
            Assert.IsFalse(snapshot.TryGetShip(new EntityId(12345), out _));

            var slot = snapshot.Roster[0];
            Assert.AreEqual(RosterStatus.Active, slot.Status);
            Assert.AreEqual(1u, slot.DeploymentCount);
            Assert.AreEqual(1L, slot.DeploymentOrder);
            Assert.AreEqual(1, slot.Revision);
            Assert.AreEqual(entity.Id, slot.ActiveEntity);
            Assert.AreEqual(RosterStatus.Ready, snapshot.Roster[1].Status);
            Assert.IsTrue(snapshot.Roster[1].ActiveEntity.IsNull);
        }

        [Test]
        public void ControlsAndRetreatArePublished()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Step();
            driver.Control(1, 0.5f, 0.25f, 0b101);
            driver.Retreat(driver.Entity(1));
            driver.Step();

            var ship = driver.Battle.Snapshot.Ships[0];
            Assert.AreEqual(0.25f, ship.Throttle);
            Assert.IsTrue(ship.HasCourse);
            Assert.AreEqual(0.5f, ship.Course);
            Assert.AreEqual(0b101UL, ship.Systems);
            Assert.IsTrue(ship.IsRetreating);
            Assert.AreEqual(1L + Battle.RetreatTicks, ship.RetreatCompletionTick);
        }

        [Test]
        public void SnapshotStaysIntactWhileTheNextTickRuns()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Control(1, null, 1f);
            driver.Step();

            var snapshot = driver.Battle.Snapshot;
            var position = snapshot.Ships[0].Position;
            var velocity = snapshot.Ships[0].LinearVelocity;
            driver.Step();

            Assert.AreNotSame(snapshot, driver.Battle.Snapshot);
            Assert.AreEqual(1L, snapshot.Tick);
            Assert.AreEqual(position, snapshot.Ships[0].Position);
            Assert.AreEqual(velocity, snapshot.Ships[0].LinearVelocity);
            Assert.AreNotEqual(position, driver.Battle.Snapshot.Ships[0].Position, "The ship moved in tick 2.");
        }

        [Test]
        public void TimerAndResultArePublishedInTheirTick()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(3);
            Assert.AreEqual(3L, driver.Battle.Snapshot.TimerElapsedTicks);
            Assert.AreEqual(driver.Battle.TimerElapsedTicks, driver.Battle.Snapshot.TimerElapsedTicks);

            driver.Surrender(1);
            driver.Step();

            var snapshot = driver.Battle.Snapshot;
            Assert.AreEqual(3L, snapshot.Tick);
            Assert.IsTrue(snapshot.IsCompleted);
            Assert.AreEqual(BattleOutcome.Victory, snapshot.Result.Outcome);
            Assert.AreEqual(0, snapshot.Result.WinningSide);
            Assert.AreEqual(3L, snapshot.Result.Tick);
            Assert.AreEqual(1, snapshot.Ships.Length);
            Assert.AreEqual(RosterStatus.Destroyed, snapshot.Roster[1].Status);
        }

        [Test]
        public void StepWithShipsAllocatesNothingAfterWarmUp()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Control(1, 1f, 1f, 1);
            driver.Control(101, -1f, 0.5f);
            driver.Step(20);

            // NUnit asserts allocate, so the loop only counts.
            var found = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i)
            {
                driver.Battle.Step();
                var snapshot = driver.Battle.Snapshot;
                if (snapshot.TryGetShip(snapshot.Ships[1].Id, out _)) ++found;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(100, found);
            Assert.AreEqual(0L, allocated);
        }
    }
}
