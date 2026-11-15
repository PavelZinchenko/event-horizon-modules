using System;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 4 step 8: the legacy TimeOutMode rules (TM1-TM3) and the B5 examples. Side 0 is the player.
    public class TimerModeTests
    {
        private const long Limit = 50;

        private static BattleSetup Setup(TimerMode mode, int playerSlots, int enemySlots, long limit = Limit,
            int maxEnemies = 1) =>
            new BattleSetup(new BattleId(1), 7, Fixtures.Roster(playerSlots, enemySlots), timeLimitTicks: limit,
                timerMode: mode, maxActiveShipsBySide: new[] { 1, maxEnemies });

        private static Driver Started(TimerMode mode, int playerSlots, int enemySlots, long limit = Limit,
            int maxEnemies = 1)
        {
            var driver = new Driver(Setup(mode, playerSlots, enemySlots, limit, maxEnemies));
            driver.Deploy(1);
            driver.Deploy(101);
            return driver;
        }

        [Test]
        public void CallNextEnemyHoldsAtTheLimitWithoutADraw()
        {
            var driver = Started(TimerMode.CallNextEnemy, 1, 2, maxEnemies: 2);
            driver.Step(Limit * 4);

            Assert.IsFalse(driver.Battle.IsCompleted);
            Assert.AreEqual(Limit * 4, driver.Battle.TimerElapsedTicks, "The timer keeps counting past the limit.");

            driver.Deploy(102);
            driver.Step();
            Assert.AreEqual(1L, driver.Battle.TimerElapsedTicks, "An enemy deployment resets.");
        }

        [Test]
        public void PlayerRemovalKeepsTheTimeLeftInCallNextEnemy()
        {
            // B5 example 1: the time at the removal stays through the selection pause; the redeployment does not reset.
            var driver = Started(TimerMode.CallNextEnemy, 2, 1);
            driver.Step(40);

            driver.Kill(1);
            driver.Step(500);
            Assert.AreEqual(40L, driver.Battle.TimerElapsedTicks);

            driver.Deploy(2);
            driver.Step(5);
            Assert.AreEqual(45L, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void RetreatCompletionIsAPlayerRemovalWithoutReset()
        {
            var driver = Started(TimerMode.CallNextEnemy, 1, 1, limit: 1000);
            driver.Step(20);
            driver.Retreat(driver.Entity(1));
            driver.Step(Battle.RetreatTicks + 1);

            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            Assert.AreEqual(20 + Battle.RetreatTicks, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void EnemyRemovalResetsInCallNextEnemy()
        {
            var driver = Started(TimerMode.CallNextEnemy, 1, 2, maxEnemies: 2);
            driver.Deploy(102);
            driver.Step(30);

            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(1L, driver.Battle.TimerElapsedTicks, "An enemy is still active, so the reset tick counts.");
        }

        [Test]
        public void PlayerRemovalResetsInDrainPlayerHp()
        {
            // B5 example 3.
            var driver = Started(TimerMode.DrainPlayerHp, 2, 1);
            driver.Step(40);

            driver.Kill(1);
            driver.Step(100);
            Assert.AreEqual(0L, driver.Battle.TimerElapsedTicks);

            driver.Deploy(2);
            driver.Step(5);
            Assert.AreEqual(5L, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void EnemyRemovalResetsInDrainPlayerHp()
        {
            var driver = Started(TimerMode.DrainPlayerHp, 1, 2, maxEnemies: 2);
            driver.Deploy(102);
            driver.Step(30);

            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(1L, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void CallNextEnemyOrDrawHoldsWhileAnEnemyIsReady()
        {
            var driver = Started(TimerMode.CallNextEnemyOrDraw, 1, 2);
            driver.Step(Limit * 5);

            Assert.IsFalse(driver.Battle.IsCompleted);
            Assert.AreEqual(Limit * 5, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void CallNextEnemyOrDrawWithoutReservesDrawsAtThreeTimesTheLimit()
        {
            var driver = Started(TimerMode.CallNextEnemyOrDraw, 1, 1);
            driver.Step(3 * Limit - 1);
            Assert.IsFalse(driver.Battle.IsCompleted);

            driver.Step();
            Assert.AreEqual(BattleOutcome.Draw, driver.Battle.Result.Outcome);
            Assert.AreEqual(BattleEndReason.Timeout, driver.Battle.Result.Reason);
            Assert.AreEqual(3 * Limit - 1, driver.Battle.Result.Tick);
        }

        [Test]
        public void DrawCountdownResumesAfterAPlayerDeath()
        {
            // B5 example 2: no enemy reserve; the player ship dies, the next deploys, the countdown goes on.
            var driver = Started(TimerMode.CallNextEnemyOrDraw, 2, 1);
            driver.Step(100);

            driver.Kill(1);
            driver.Step(30);
            Assert.AreEqual(100L, driver.Battle.TimerElapsedTicks);

            driver.Deploy(2);
            var deployTick = driver.Tick;
            driver.Step(3 * Limit - 100 - 1);
            Assert.IsFalse(driver.Battle.IsCompleted);
            driver.Step();
            Assert.AreEqual(BattleEndReason.Timeout, driver.Battle.Result.Reason);
            Assert.AreEqual(deployTick + 3 * Limit - 100 - 1, driver.Battle.Result.Tick);
        }

        [Test]
        public void EnemyRemovalWithoutAReadyEnemyDoesNotResetTheDrawCountdown()
        {
            var driver = Started(TimerMode.CallNextEnemyOrDraw, 1, 2, maxEnemies: 2);
            driver.Deploy(102);
            driver.Step(30);

            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(31L, driver.Battle.TimerElapsedTicks);
        }

        [Test]
        public void LastEnemyDeploymentStartsTheDrawCountdown()
        {
            var driver = Started(TimerMode.CallNextEnemyOrDraw, 1, 2, maxEnemies: 2);
            driver.Step(Limit * 2);

            driver.Deploy(102);
            driver.Step(3 * Limit - 1);
            Assert.IsFalse(driver.Battle.IsCompleted);
            driver.Step();
            Assert.AreEqual(BattleEndReason.Timeout, driver.Battle.Result.Reason);
        }

        [Test]
        public void DrainStartsOneSecondAfterTheLimitAndGrowsEachSecond()
        {
            // Plan example: k % per second after k whole overtime seconds; the first overtime second drains nothing.
            const long limit = 100;
            var driver = Started(TimerMode.DrainPlayerHp, 1, 1, limit);
            driver.Step(limit + 50);
            var ship = driver.Ship(1);
            Assert.AreEqual(1000f, ship.Armor.Value);
            Assert.AreEqual(500f, ship.Shield.Value);

            driver.Step(50);
            Assert.AreEqual(990f, ship.Armor.Value, 0.01f, "1 % in the second overtime second.");
            Assert.AreEqual(495f, ship.Shield.Value, 0.01f);

            driver.Step(50);
            Assert.AreEqual(970f, ship.Armor.Value, 0.02f, "2 % in the third.");
            Assert.AreEqual(485f, ship.Shield.Value, 0.01f);
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value, "The enemy is not drained.");
        }

        [Test]
        public void DrainKillsThePlayerDuringTheFifteenthOvertimeSecond()
        {
            const long limit = 100;
            var driver = Started(TimerMode.DrainPlayerHp, 1, 1, limit);
            while (!driver.Battle.IsCompleted && driver.Tick < 10000) driver.Step();

            Assert.AreEqual(BattleOutcome.Victory, driver.Battle.Result.Outcome);
            Assert.AreEqual(1, driver.Battle.Result.WinningSide);
            var overtime = driver.Battle.Result.Tick - limit;
            Assert.That(overtime, Is.InRange(14 * 50L, 15 * 50L + 1), $"Death after {overtime} overtime ticks.");
        }

        [Test]
        public void SideLimitsApplyPerSide()
        {
            var driver = new Driver(Setup(TimerMode.CallNextEnemy, 2, 3, maxEnemies: 3));
            driver.Deploy(1);
            driver.Deploy(2);
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Deploy(103);
            driver.Step();

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(RosterStatus.Ready, driver.Status(2), "The player side allows one ship.");
            Assert.AreEqual(RosterStatus.Active, driver.Status(101));
            Assert.AreEqual(RosterStatus.Active, driver.Status(102));
            Assert.AreEqual(RosterStatus.Active, driver.Status(103));
        }

        [Test]
        public void SetupRejectsInvalidTimerValues()
        {
            var roster = Fixtures.Roster(1, 1);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BattleSetup(new BattleId(1), 7, roster, playerSide: 2));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BattleSetup(new BattleId(1), 7, roster, timerMode: (TimerMode)9));
            Assert.Throws<ArgumentException>(() =>
                new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsBySide: new[] { 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsBySide: new[] { 1, 0 }));
        }
    }
}
