using System;
using CombatSimulation.Modes;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // RosterRules.EliminationDelayTicks (Frontier TODO/online_duel.md, step 6a): the Elimination result comes the delay
    // after the death of a side's last ship, and the alive counts at that tick decide.
    public class EliminationDelayTests
    {
        private const long Delay = RosterMode.ShipLostTicks;

        private static Driver Started(int side0Slots = 1, int side1Slots = 1, bool onlyOneShip = false,
            TimerMode mode = TimerMode.Draw, long limit = 0)
        {
            var setup = new BattleSetup(new BattleId(1), 7, Fixtures.Units(side0Slots, side1Slots),
                rules: new RosterRules(timeLimitTicks: limit, timerMode: mode, onlyOneShip: onlyOneShip,
                    eliminationDelayTicks: Delay));
            var driver = new Driver(setup);
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(10);
            return driver;
        }

        [Test]
        public void TheResultComesTheDelayAfterTheLastDeath()
        {
            var driver = Started();
            var deathTick = driver.Tick;
            driver.Kill(101);
            driver.Step(Delay);
            Assert.IsFalse(driver.Battle.IsCompleted);
            Assert.AreEqual(UnitStatus.Active, driver.Status(1), "The survivor keeps flying.");

            driver.Step();
            var result = driver.Battle.Result;
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(0, result.WinningSide);
            Assert.AreEqual(BattleEndReason.Elimination, result.Reason);
            Assert.AreEqual(deathTick + Delay, result.Tick);
        }

        [Test]
        public void TheWinnerDyingWithinTheDelayGivesADraw()
        {
            var driver = Started();
            var deathTick = driver.Tick;
            driver.Kill(101);
            driver.Step(50);
            driver.Kill(1);
            driver.Step(Delay - 50);
            Assert.IsFalse(driver.Battle.IsCompleted, "The second death does not move the deadline.");

            driver.Step();
            var result = driver.Battle.Result;
            Assert.AreEqual(BattleOutcome.Draw, result.Outcome);
            Assert.AreEqual(BattleEndReason.Elimination, result.Reason);
            Assert.AreEqual(deathTick + Delay, result.Tick);
        }

        [Test]
        public void TheTimerHoldsDuringTheDelay()
        {
            var driver = Started(limit: Delay / 2);
            driver.Kill(101);
            driver.Step(Delay);
            Assert.IsFalse(driver.Battle.IsCompleted, "No timeout while one side has no active ship.");
            Assert.AreEqual(0L, driver.Battle.TimerElapsedTicks);

            driver.Step();
            Assert.AreEqual(BattleEndReason.Elimination, driver.Battle.Result.Reason);
            Assert.AreEqual(0, driver.Battle.Result.WinningSide);
        }

        // Without the delay the elimination would have ended the battle before the OnlyOneShip deadline.
        [Test]
        public void APendingEliminationReplacesTheShipLostDeadline()
        {
            var driver = Started(2, 1, onlyOneShip: true);
            driver.Kill(1);
            driver.Step(100);
            var deathTick = driver.Tick;
            driver.Kill(101);
            driver.StepUntil(deathTick + RosterMode.ShipLostTicks - 99);
            Assert.IsFalse(driver.Battle.IsCompleted, "The ShipLost deadline has passed.");

            driver.StepUntil(deathTick + Delay);
            Assert.IsFalse(driver.Battle.IsCompleted);
            driver.Step();
            var result = driver.Battle.Result;
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(0, result.WinningSide);
            Assert.AreEqual(BattleEndReason.Elimination, result.Reason);
            Assert.AreEqual(deathTick + Delay, result.Tick);
        }

        [Test]
        public void ANegativeDelayIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RosterRules(eliminationDelayTicks: -1));
        }
    }
}
