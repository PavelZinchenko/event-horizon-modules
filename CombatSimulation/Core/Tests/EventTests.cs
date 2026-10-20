using System.Collections.Generic;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class EventTests
    {
        [Test]
        public void DeployEmitsSpawnThenTransition()
        {
            var driver = new Driver(Fixtures.Setup(1, 1, battleId: 5));
            driver.Deploy(1);
            driver.Step();

            var events = driver.Battle.Snapshot.Events;
            var entity = driver.Entity(1);
            Assert.AreEqual(2, events.Length);

            var spawn = events[0];
            Assert.AreEqual(BattleEventKind.Spawn, spawn.Kind);
            Assert.AreEqual(new BattleId(5), spawn.BattleId);
            Assert.AreEqual(0L, spawn.Tick);
            Assert.AreEqual(0, spawn.Sequence);
            Assert.AreEqual(entity, spawn.EntityId);
            Assert.AreEqual(new RosterId(1), spawn.RosterId);
            Assert.AreEqual(0, spawn.Side);
            Assert.AreSame(Fixtures.Ship, spawn.Definition);

            var transition = events[1];
            Assert.AreEqual(BattleEventKind.RosterTransition, transition.Kind);
            Assert.AreEqual(1, transition.Sequence);
            Assert.AreEqual(new RosterId(1), transition.RosterId);
            Assert.AreEqual(RosterStatus.Ready, transition.PreviousStatus);
            Assert.AreEqual(RosterStatus.Active, transition.Status);
            Assert.AreEqual(RosterTransitionReason.Deployed, transition.TransitionReason);
            Assert.AreEqual(1, transition.Revision);
            Assert.AreEqual(entity, transition.EntityId);

            driver.Step();
            Assert.AreEqual(0, driver.Battle.Snapshot.Events.Length, "A quiet tick has an empty batch.");
        }

        [Test]
        public void DeathEmitsRemovalThenKilled()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var entity = driver.Entity(1);
            driver.Kill(1);
            driver.Step();

            var events = driver.Battle.Snapshot.Events;
            Assert.AreEqual(2, events.Length);
            Assert.AreEqual(BattleEventKind.Removal, events[0].Kind);
            Assert.AreEqual(RemovalReason.Death, events[0].RemovalReason);
            Assert.AreEqual(entity, events[0].EntityId);
            Assert.AreEqual(1L, events[0].Tick);
            Assert.AreEqual(BattleEventKind.RosterTransition, events[1].Kind);
            Assert.AreEqual(RosterStatus.Active, events[1].PreviousStatus);
            Assert.AreEqual(RosterStatus.Destroyed, events[1].Status);
            Assert.AreEqual(RosterTransitionReason.Killed, events[1].TransitionReason);
            Assert.AreEqual(entity, events[1].EntityId);
            Assert.AreEqual(2, events[1].Revision);
        }

        [Test]
        public void RetreatEmitsRemovalThenRetreatedWithDeficits()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var entity = driver.Entity(1);
            driver.Ship(1).Armor.Consume(100f);
            driver.Ship(1).Shield.Consume(500f);
            driver.Retreat(entity);
            driver.StepUntil(1 + Battle.RetreatTicks + 1);

            var events = driver.Battle.Snapshot.Events;
            Assert.AreEqual(1L + Battle.RetreatTicks, driver.Battle.Snapshot.Tick);
            Assert.AreEqual(2, events.Length);
            Assert.AreEqual(RemovalReason.Retreat, events[0].RemovalReason);
            Assert.AreEqual(entity, events[0].EntityId);
            Assert.AreEqual(RosterTransitionReason.Retreated, events[1].TransitionReason);
            Assert.AreEqual(RosterStatus.Ready, events[1].Status);
            Assert.AreEqual(entity, events[1].EntityId);
            Assert.AreEqual(100f, events[1].ArmorDeficit);
            Assert.AreEqual(driver.Slot(1).ShieldDeficit, events[1].ShieldDeficit);
            Assert.Greater(events[1].ShieldDeficit, 0f);
        }

        [Test]
        public void SurrenderRemovesActiveShipAndDestroysReserveWithoutEntity()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var entity = driver.Entity(1);
            driver.Surrender(0);
            driver.Step();

            var events = driver.Battle.Snapshot.Events;
            Assert.AreEqual(4, events.Length);
            Assert.AreEqual(BattleEventKind.Removal, events[0].Kind);
            Assert.AreEqual(entity, events[0].EntityId);
            Assert.AreEqual(RosterTransitionReason.ForcedDestruction, events[1].TransitionReason);
            Assert.AreEqual(new RosterId(1), events[1].RosterId);
            Assert.AreEqual(RosterStatus.Active, events[1].PreviousStatus);
            Assert.AreEqual(entity, events[1].EntityId);
            Assert.AreEqual(RosterTransitionReason.ForcedDestruction, events[2].TransitionReason);
            Assert.AreEqual(new RosterId(2), events[2].RosterId);
            Assert.AreEqual(RosterStatus.Ready, events[2].PreviousStatus);
            Assert.IsTrue(events[2].EntityId.IsNull, "A ready reserve has no entity.");
            Assert.AreEqual(BattleEventKind.Result, events[3].Kind);
            Assert.AreEqual(BattleOutcome.Victory, events[3].Result.Outcome);
            Assert.AreEqual(1, events[3].Result.WinningSide);
            for (var i = 0; i < events.Length; ++i) Assert.AreEqual(i, events[i].Sequence);
        }

        [Test]
        public void DeathAndSurrenderInOneTickGiveOneTransitionPerSlot()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Kill(1);
            driver.Surrender(0);
            driver.Step();

            var transitions = new List<BattleEvent>();
            foreach (var item in driver.Battle.Snapshot.Events)
                if (item.Kind == BattleEventKind.RosterTransition) transitions.Add(item);

            Assert.AreEqual(2, transitions.Count);
            Assert.AreEqual(new RosterId(1), transitions[0].RosterId);
            Assert.AreEqual(RosterTransitionReason.Killed, transitions[0].TransitionReason);
            Assert.AreEqual(new RosterId(2), transitions[1].RosterId);
            Assert.AreEqual(RosterTransitionReason.ForcedDestruction, transitions[1].TransitionReason);
        }

        [Test]
        public void TwoRunsGiveTheSameEventLog()
        {
            var first = Run();
            var second = Run();

            Assert.Greater(first.Count, 8);
            CollectionAssert.AreEqual(first, second);
        }

        private static List<string> Run()
        {
            var log = new List<string>();
            var driver = new Driver(Fixtures.Setup(2, 2));
            driver.Deploy(1);
            driver.Deploy(101);
            Collect(driver, log);
            driver.Retreat(driver.Entity(1));
            driver.Kill(101);
            Collect(driver, log);
            driver.Deploy(102);
            while (driver.Status(1) != RosterStatus.Ready) Collect(driver, log);
            driver.Deploy(1);
            Collect(driver, log);
            driver.KillAll(0);
            Collect(driver, log);
            return log;
        }

        private static void Collect(Driver driver, List<string> log)
        {
            driver.Step();
            foreach (var item in driver.Battle.Snapshot.Events) log.Add(item.ToString());
        }
    }
}
