using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 4 step 9: legacy PlayerShipSelectionMode.OnlyOneShip (SS2, SS3). Side 0 is the player.
    public class OnlyOneShipTests
    {
        private static Driver Started(int playerSlots, int enemySlots, bool onlyOneShip = true,
            TimerMode mode = TimerMode.CallNextEnemy, long limit = 0)
        {
            var setup = new BattleSetup(new BattleId(1), 7, Fixtures.Roster(playerSlots, enemySlots), timeLimitTicks: limit,
                timerMode: mode, onlyOneShip: onlyOneShip);
            var driver = new Driver(setup);
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(10);
            return driver;
        }

        [Test]
        public void DeathEndsTheBattleThreeSecondsLaterWithReservesReady()
        {
            var driver = Started(2, 1);
            var deathTick = driver.Tick;
            driver.Kill(1);
            driver.Step(Battle.ShipLostTicks);
            Assert.IsFalse(driver.Battle.IsCompleted);

            driver.Step();
            var result = driver.Battle.Result;
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(1, result.WinningSide);
            Assert.AreEqual(BattleEndReason.ShipLost, result.Reason);
            Assert.AreEqual(deathTick + Battle.ShipLostTicks, result.Tick);
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.AreEqual(RosterStatus.Ready, driver.Status(2), "No synthetic reserve death.");
            Assert.AreEqual(RosterStatus.Active, driver.Status(101));
        }

        [Test]
        public void EnemyEliminationInsideTheWindowIsAPlayerVictory()
        {
            var driver = Started(2, 1);
            driver.Kill(1);
            driver.Step(100);

            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(BattleOutcome.Victory, driver.Battle.Result.Outcome);
            Assert.AreEqual(0, driver.Battle.Result.WinningSide);
            Assert.AreEqual(BattleEndReason.Elimination, driver.Battle.Result.Reason);
        }

        [Test]
        public void EliminationAtTheDeadlineComesFirst()
        {
            var driver = Started(2, 1);
            var deathTick = driver.Tick;
            driver.Kill(1);
            driver.StepUntil(deathTick + Battle.ShipLostTicks);

            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(BattleEndReason.Elimination, driver.Battle.Result.Reason);
            Assert.AreEqual(0, driver.Battle.Result.WinningSide);
            Assert.AreEqual(deathTick + Battle.ShipLostTicks, driver.Battle.Result.Tick);
        }

        [Test]
        public void LastShipsDyingTogetherStayADrawByElimination()
        {
            var driver = Started(1, 1);
            driver.Kill(1);
            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(BattleOutcome.Draw, driver.Battle.Result.Outcome);
            Assert.AreEqual(BattleEndReason.Elimination, driver.Battle.Result.Reason);
        }

        [Test]
        public void PlayerDeploymentAfterTheLossIsRejected()
        {
            var driver = Started(2, 2);
            driver.Kill(1);
            driver.Step();

            driver.Deploy(2);
            driver.Step();
            Assert.AreEqual(RosterStatus.Ready, driver.Status(2));
            Assert.AreEqual(1, driver.Battle.RejectedCommandCount);
        }

        [Test]
        public void EnemyDeploymentAfterTheLossIsAccepted()
        {
            var driver = Started(2, 2);
            driver.Kill(1);
            driver.Kill(101);
            driver.Step();

            driver.Deploy(102);
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(102));
            Assert.IsFalse(driver.Battle.IsCompleted);
        }

        [Test]
        public void RetreatDoesNotEndTheBattle()
        {
            var driver = Started(2, 1);
            driver.Retreat(driver.Entity(1));
            driver.Step(Battle.RetreatTicks + Battle.ShipLostTicks + 10);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            Assert.IsFalse(driver.Battle.IsCompleted);

            driver.Deploy(2);
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(2));
        }

        [Test]
        public void DrainDeathEndsTheBattle()
        {
            var driver = Started(2, 1, mode: TimerMode.DrainPlayerHp, limit: 100);
            while (!driver.Battle.IsCompleted && driver.Tick < 10000) driver.Step();

            Assert.AreEqual(BattleEndReason.ShipLost, driver.Battle.Result.Reason);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(2));
        }

        [Test]
        public void WithoutTheFlagTheReserveDeploys()
        {
            var driver = Started(2, 1, onlyOneShip: false);
            driver.Kill(1);
            driver.Step(Battle.ShipLostTicks + 10);
            Assert.IsFalse(driver.Battle.IsCompleted);

            driver.Deploy(2);
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(2));
        }
    }
}
