using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class BattleResultTests
    {
        [Test]
        public void TheSideThatLosesItsLastSlotFirstLoses()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(10);

            driver.Kill(101);
            driver.Step();

            var result = driver.Battle.Result;
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(0, result.WinningSide);
            Assert.AreEqual(BattleEndReason.Elimination, result.Reason);
            Assert.AreEqual(10L, result.Tick);
            Assert.IsTrue(driver.Battle.IsCompleted);
        }

        [Test]
        public void LastSlotsOfBothSidesInOneTickGiveADraw()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();

            driver.Kill(1);
            driver.Kill(101);
            driver.Step();

            Assert.AreEqual(BattleOutcome.Draw, driver.Battle.Result.Outcome);
            Assert.AreEqual(BattleEndReason.Elimination, driver.Battle.Result.Reason);
            Assert.AreEqual(1L, driver.Battle.Result.Tick);
        }

        [Test]
        public void AReadyReservePreventsAResult()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();

            driver.Kill(1);
            driver.Step(100);

            Assert.IsFalse(driver.Battle.IsCompleted);
            Assert.AreEqual(BattleOutcome.None, driver.Battle.Result.Outcome);
        }

        [Test]
        public void UndeployedFleetsGiveNoResultAndNoTime()
        {
            var driver = new Driver(Fixtures.Setup(timeLimitTicks: 10));
            driver.Step(100);

            Assert.IsFalse(driver.Battle.IsCompleted);
            Assert.AreEqual(0L, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void TimeoutGivesADraw()
        {
            var driver = new Driver(Fixtures.Setup(timeLimitTicks: 50));
            driver.Deploy(1);
            driver.Deploy(101);

            driver.Step(49);
            Assert.IsFalse(driver.Battle.IsCompleted);
            Assert.AreEqual(49L, driver.Battle.TimerElapsedTicks);

            driver.Step();
            Assert.AreEqual(BattleOutcome.Draw, driver.Battle.Result.Outcome);
            Assert.AreEqual(BattleEndReason.Timeout, driver.Battle.Result.Reason);
            Assert.AreEqual(49L, driver.Battle.Result.Tick, "The deployment tick counts as the first elapsed tick.");
        }

        [Test]
        public void EliminationInTheTimeoutTickWins()
        {
            var driver = new Driver(Fixtures.Setup(timeLimitTicks: 50));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(49);

            driver.Kill(101);
            driver.Step();

            Assert.AreEqual(BattleOutcome.Victory, driver.Battle.Result.Outcome);
            Assert.AreEqual(BattleEndReason.Elimination, driver.Battle.Result.Reason);
        }

        [Test]
        public void TimerPausesWhileASideHasNoActiveShip()
        {
            // B5 with symmetric resets: the removal resets the timer, and it stays at zero until the redeployment.
            var driver = new Driver(Fixtures.Setup(2, 1, timeLimitTicks: 1000));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(30);
            Assert.AreEqual(30L, driver.Battle.TimerElapsedTicks);

            driver.Kill(1);
            driver.Step(200);
            Assert.AreEqual(0L, driver.Battle.TimerElapsedTicks);

            driver.Deploy(2);
            driver.Step(5);
            Assert.AreEqual(5L, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void TimerDoesNotRunBeforeBothSidesDeploy()
        {
            var driver = new Driver(Fixtures.Setup(timeLimitTicks: 1000));
            driver.Deploy(1);
            driver.Step(40);
            Assert.AreEqual(0L, driver.Battle.TimerElapsedTicks);

            driver.Deploy(101);
            driver.Step(3);
            Assert.AreEqual(3L, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void DeploymentAndRemovalOnEitherSideResetTheTimer()
        {
            var driver = new Driver(Fixtures.Setup(2, 2, timeLimitTicks: 1000, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(20);

            driver.Deploy(102);
            driver.Step();
            Assert.AreEqual(1L, driver.Battle.TimerElapsedTicks, "Enemy deployment resets.");

            driver.Step(20);
            driver.Deploy(2);
            driver.Step();
            Assert.AreEqual(1L, driver.Battle.TimerElapsedTicks, "Deployment on the other side resets too.");

            driver.Step(20);
            driver.Kill(102);
            driver.Step();
            Assert.AreEqual(1L, driver.Battle.TimerElapsedTicks, "A removal resets; both sides still have a ship.");

            driver.Step(20);
            driver.Retreat(driver.Entity(2));
            driver.Step(Battle.RetreatTicks);
            Assert.AreEqual(21 + Battle.RetreatTicks, driver.Battle.TimerElapsedTicks, "A running retreat does not reset.");
            driver.Step();
            Assert.AreEqual(1L, driver.Battle.TimerElapsedTicks, "Retreat completion is a removal.");
        }
    }
}
