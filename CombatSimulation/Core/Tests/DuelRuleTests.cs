using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 7, Q1: with remote owners the legacy player rules apply to both sides. B owns side 0, A owns side 1.
    public class DuelRuleTests
    {
        [Test]
        public void DrainHitsTheActiveShipsOfBothSides()
        {
            const long limit = 50;
            var p = new NetPair(timerMode: TimerMode.DrainPlayerHp, timeLimitTicks: limit);
            p.Step((int)limit + 150);

            var side0 = p.B.Ship(1);
            var side1 = p.A.Ship(101);
            Assert.Less(side0.Armor.Value, 1000f, "Side 0 drains on its owner.");
            Assert.Less(side1.Armor.Value, 1000f, "Side 1 drains on its owner.");
            Assert.AreEqual(side0.Armor.Value, side1.Armor.Value, 0.01f);
            Assert.AreEqual(side0.Shield.Value, side1.Shield.Value, 0.01f);
        }

        [TestCase(TimerMode.DrainPlayerHp, 0)]
        [TestCase(TimerMode.DrainPlayerHp, 1)]
        [TestCase(TimerMode.CallNextEnemy, 0)]
        [TestCase(TimerMode.CallNextEnemy, 1)]
        public void ARemovalOnEitherSideResetsTheTimer(TimerMode mode, int side)
        {
            var p = new NetPair(timerMode: mode, timeLimitTicks: 1000, reserves: true);
            p.Step(100);
            Assert.Greater(p.A.Battle.TimerElapsedTicks, 50L);

            if (side == 0) p.B.Kill(1); else p.A.Kill(101);
            p.Step(3);
            Assert.IsFalse(p.A.Battle.IsCompleted);
            Assert.AreEqual(0L, p.A.Battle.TimerElapsedTicks);
            Assert.AreEqual(0L, p.B.Battle.TimerElapsedTicks);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void TheFirstDeathOnEitherSideLosesTheBattle(int side)
        {
            var p = new NetPair(onlyOneShip: true, reserves: true);
            p.Step(10);
            var deathTick = p.A.Tick;
            if (side == 0) p.B.Kill(1); else p.A.Kill(101);

            var result = RunToResult(p);
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(1 - side, result.WinningSide);
            Assert.AreEqual(BattleEndReason.ShipLost, result.Reason);
            Assert.AreEqual(deathTick + Battle.ShipLostTicks, result.Tick);
        }

        [Test]
        public void FirstDeathsOfBothSidesInOneTickAreADraw()
        {
            var p = new NetPair(onlyOneShip: true, reserves: true);
            p.Step(10);
            p.B.Kill(1);
            p.A.Kill(101);

            var result = RunToResult(p);
            Assert.AreEqual(BattleOutcome.Draw, result.Outcome);
            Assert.AreEqual(BattleEndReason.ShipLost, result.Reason);
        }

        [Test]
        public void ALaterDeathOfTheOtherSideDoesNotChangeTheLoser()
        {
            var p = new NetPair(onlyOneShip: true, reserves: true);
            p.Step(10);
            p.A.Kill(101);
            p.Step(20);
            p.B.Kill(1);

            var result = RunToResult(p);
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(0, result.WinningSide);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void TheLosingSideCannotDeployAReserve(int side)
        {
            var p = new NetPair(onlyOneShip: true, reserves: true);
            p.Step(10);
            var owner = side == 0 ? p.B : p.A;
            uint active = side == 0 ? 1u : 101u, reserve = side == 0 ? 2u : 102u;
            owner.Kill(active);
            p.Step(2);

            owner.Deploy(reserve);
            p.Step(2);
            Assert.AreEqual(UnitStatus.Ready, owner.Status(reserve));
            Assert.AreEqual(1, owner.Battle.RejectedCommandCount);
        }

        // Both cores reach the same result in the same pair step.
        private static BattleResult RunToResult(NetPair p)
        {
            for (var i = 0; i < 1000 && !p.A.Battle.IsCompleted && !p.B.Battle.IsCompleted; ++i) p.Step();
            Assert.IsTrue(p.A.Battle.IsCompleted && p.B.Battle.IsCompleted, "Both cores complete together.");
            Assert.AreEqual(p.A.Battle.Result, p.B.Battle.Result);
            return p.A.Battle.Result;
        }
    }
}
