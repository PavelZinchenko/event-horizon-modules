using System;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Roster commands and control frames.
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

        [Test]
        public void SubmitRejectsInvalidControlFrames()
        {
            var driver = new Driver(Fixtures.Setup());
            var battle = driver.Battle;
            var id = new EntityId(42);
            driver.Step(3);

            Assert.IsFalse(battle.Submit(ControlFrame.Create(new BattleId(2), 1, 3, id, null, 0f, 0)), "wrong battle");
            Assert.IsFalse(driver.Control(id, tick: 2), "past tick");
            Assert.IsFalse(driver.Control(default(EntityId)), "no entity");
            Assert.IsFalse(driver.Control(id, throttle: -0.1f));
            Assert.IsFalse(driver.Control(id, throttle: 1.1f));
            Assert.IsFalse(driver.Control(id, throttle: float.NaN));
            Assert.IsFalse(driver.Control(id, course: float.NaN));
            Assert.IsFalse(driver.Control(id, course: float.PositiveInfinity));
            Assert.AreEqual(8, battle.RejectedCommandCount);

            Assert.IsTrue(driver.Control(id, course: 7f, throttle: 1f));
        }

        [Test]
        public void ASecondFrameForTheSameEntityAndTickIsRejected()
        {
            var driver = new Driver(Fixtures.Setup());
            var id = new EntityId(42);

            Assert.IsTrue(driver.Control(id, throttle: 1f, tick: 5));
            Assert.IsFalse(driver.Control(id, throttle: 0.5f, tick: 5));
            Assert.IsTrue(driver.Control(id, throttle: 0.5f, tick: 6), "Another tick is a separate frame.");
            Assert.IsTrue(driver.Control(new EntityId(43), throttle: 0.5f, tick: 5), "Another entity is a separate frame.");
            Assert.AreEqual(1, driver.Battle.RejectedCommandCount);
        }

        [Test]
        public void FramesAndRosterCommandsShareCommandIds()
        {
            var driver = new Driver(Fixtures.Setup());
            var battle = driver.Battle;

            Assert.IsTrue(battle.Submit(RosterCommand.Deploy(battle.BattleId, 9, 5, new RosterId(1))));
            Assert.IsFalse(battle.Submit(ControlFrame.Create(battle.BattleId, 9, 5, new EntityId(42), null, 0f, 0)));
            Assert.IsTrue(battle.Submit(ControlFrame.Create(battle.BattleId, 10, 5, new EntityId(42), null, 0f, 0)));
            Assert.IsFalse(battle.Submit(RosterCommand.Deploy(battle.BattleId, 10, 5, new RosterId(101))));
            Assert.AreEqual(2, battle.RejectedCommandCount);
        }

        [Test]
        public void AFrameAppliesAtItsTargetTickAndStaysInForce()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Step();

            Assert.IsTrue(driver.Control(1, course: 0.5f, throttle: 0.75f, systems: 0b101, tick: 4));
            driver.StepUntil(4);
            Assert.AreEqual(0f, driver.Ship(1).Throttle, "Tick 3 has run; the frame is still pending.");

            driver.Step(10);
            var ship = driver.Ship(1);
            Assert.AreEqual(0.75f, ship.Throttle);
            Assert.IsTrue(ship.HasCourse);
            Assert.AreEqual(0.5f, ship.Course);
            Assert.AreEqual(0b101UL, ship.Systems);

            driver.Control(1);
            driver.Step();
            Assert.AreEqual(0f, ship.Throttle);
            Assert.IsFalse(ship.HasCourse);
            Assert.AreEqual(0UL, ship.Systems);
        }

        [Test]
        public void AFrameForAShipDeployedInTheSameTickApplies()
        {
            var driver = new Driver(Fixtures.Setup());
            var id = SpawnIdentity.CreateRoot(driver.BattleId, new RosterId(1), 1, 0);

            driver.Control(id, throttle: 1f);
            driver.Deploy(1);
            driver.Step();

            Assert.AreEqual(id, driver.Entity(1));
            Assert.AreEqual(1f, driver.Ship(1).Throttle, "Roster commands apply before control frames.");
            Assert.AreEqual(0, driver.Battle.RejectedCommandCount);
        }

        [Test]
        public void AFrameForARemovedEntityIsDropped()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var id = driver.Entity(1);

            driver.Kill(1);
            driver.Step();
            driver.Control(id, throttle: 1f);
            driver.Deploy(2);
            driver.Step();

            Assert.AreEqual(1, driver.Battle.RejectedCommandCount);
            Assert.AreEqual(0f, driver.Ship(2).Throttle, "The frame does not reach another ship.");
        }

        [Test]
        public void HeldControlsAreClearedOnRedeployment()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Control(1, course: 1f, throttle: 1f, systems: 1);
            driver.Retreat(driver.Entity(1));
            driver.StepUntil(driver.Tick + Battle.RetreatTicks + 1);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));

            driver.Deploy(1);
            driver.Step();
            var ship = driver.Ship(1);
            Assert.AreEqual(0f, ship.Throttle);
            Assert.IsFalse(ship.HasCourse);
            Assert.AreEqual(0UL, ship.Systems);
            Assert.AreEqual(0UL, ship.PreviousSystems);
        }

        [Test]
        public void TheCoreDetectsBitEdgesBetweenTicks()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Step();
            var ship = driver.Ship(1);

            driver.Control(1, systems: 0b011);
            driver.Step();
            Assert.AreEqual(0b011UL, ship.PressedSystems);
            Assert.AreEqual(0UL, ship.ReleasedSystems);

            driver.Step();
            Assert.AreEqual(0UL, ship.PressedSystems, "A held bit is pressed only in its first tick.");

            driver.Control(1, systems: 0b110);
            driver.Step();
            Assert.AreEqual(0b100UL, ship.PressedSystems);
            Assert.AreEqual(0b001UL, ship.ReleasedSystems);
        }
    }
}
