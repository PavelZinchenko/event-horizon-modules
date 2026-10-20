using System;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Roster commands. Control frame tests come with the ship entity (step 3).
    public class CommandTests
    {
        [Test]
        public void SubmitRejectsWrongBattlePastTickInvalidSideAndUnknownKind()
        {
            var driver = new Driver(Fixtures.Setup());
            var battle = driver.Battle;
            driver.Step(3);

            Assert.IsFalse(battle.Submit(RosterCommand.Deploy(new BattleId(2), 1, 3, new RosterId(1))));
            Assert.IsFalse(battle.Submit(RosterCommand.Deploy(battle.BattleId, 2, 2, new RosterId(1))));
            Assert.IsFalse(battle.Submit(RosterCommand.Surrender(battle.BattleId, 3, 3, 2)));
            Assert.IsFalse(battle.Submit(RosterCommand.KillAll(battle.BattleId, 4, 3, -1)));
            Assert.IsFalse(battle.Submit(default(RosterCommand)));
            Assert.AreEqual(5, battle.RejectedCommandCount);

            Assert.IsTrue(battle.Submit(RosterCommand.Deploy(battle.BattleId, 6, 3, new RosterId(1))));
        }

        [Test]
        public void SubmitRejectsADuplicatePendingCommandId()
        {
            var driver = new Driver(Fixtures.Setup());
            var battle = driver.Battle;

            Assert.IsTrue(battle.Submit(RosterCommand.Deploy(battle.BattleId, 9, 5, new RosterId(1))));
            Assert.IsFalse(battle.Submit(RosterCommand.Deploy(battle.BattleId, 9, 7, new RosterId(101))));
            Assert.AreEqual(1, battle.RejectedCommandCount);

            driver.StepUntil(6);
            Assert.IsTrue(battle.Submit(RosterCommand.Deploy(battle.BattleId, 9, 7, new RosterId(101))),
                "An applied command no longer reserves its ID.");
        }

        [Test]
        public void ACommandAppliesAtItsTargetTick()
        {
            var driver = new Driver(Fixtures.Setup());
            Assert.IsTrue(driver.Deploy(1, 10));

            driver.StepUntil(10);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(SpawnIdentity.CreateRoot(driver.BattleId, new RosterId(1), 1, 10), driver.Entity(1));
        }

        [Test]
        public void CommandsOfOneTickApplyInCommandIdOrder()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            var battle = driver.Battle;

            // Submitted in the opposite order; the side allows one active ship.
            Assert.IsTrue(battle.Submit(RosterCommand.Deploy(battle.BattleId, 5, 0, new RosterId(2))));
            Assert.IsTrue(battle.Submit(RosterCommand.Deploy(battle.BattleId, 3, 0, new RosterId(1))));
            driver.Step();

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(RosterStatus.Ready, driver.Status(2));
            Assert.AreEqual(1, battle.RejectedCommandCount);
        }

        [Test]
        public void ASecondDeployOfOneSlotInOneTickIsRejected()
        {
            var driver = new Driver(Fixtures.Setup(1, 1, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(1);
            driver.Step();

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(1u, driver.Slot(1).DeploymentCount);
            Assert.AreEqual(1, driver.Battle.RejectedCommandCount);
        }

        [Test]
        public void TheSideLimitIsEnforced()
        {
            var limited = new Driver(Fixtures.Setup(2, 1));
            limited.Deploy(1);
            limited.Deploy(2);
            limited.Step();
            Assert.AreEqual(RosterStatus.Ready, limited.Status(2));

            var wide = new Driver(Fixtures.Setup(2, 1, maxActiveShipsPerSide: 2));
            wide.Deploy(1);
            wide.Deploy(2);
            wide.Step();
            Assert.AreEqual(RosterStatus.Active, wide.Status(2));
        }

        [Test]
        public void InvalidRetreatsAreRejected()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var entity = driver.Entity(1);

            driver.Retreat(entity);
            driver.Step();
            Assert.AreEqual(1 + Battle.RetreatTicks, driver.Slot(1).ActiveShip.RetreatCompletionTick);

            driver.Retreat(entity);
            driver.Retreat(new EntityId(12345));
            driver.Step();

            Assert.AreEqual(2, driver.Battle.RejectedCommandCount);
            Assert.AreEqual(1 + Battle.RetreatTicks, driver.Slot(1).ActiveShip.RetreatCompletionTick,
                "A second retreat does not restart the charge.");
        }

        [Test]
        public void ABattleWithAResultAcceptsNoCommandsAndNoSteps()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Surrender(0);
            driver.Step();

            Assert.IsTrue(driver.Battle.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => driver.Deploy(101));
            Assert.Throws<InvalidOperationException>(() => driver.Battle.Step());
            Assert.AreEqual(1L, driver.Tick);
        }
    }
}
