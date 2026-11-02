using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Legacy Energy rules (IResourcePoints.cs), on ResourcePool and through the battle.
    public class ShipResourceTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void APoolStartsFullAndRechargesAtItsRate()
        {
            var pool = ResourcePool.Create(new ResourceDefinition(100f, 10f, 1f));
            Assert.AreEqual(100f, pool.Value);
            Assert.AreEqual(0f, pool.DelayRemaining);

            pool.Value = 50f;
            pool.Update(0.5f);
            Assert.AreEqual(55f, pool.Value, Tolerance);

            pool.Update(10f);
            Assert.AreEqual(100f, pool.Value, "Recharge stops at the maximum.");
        }

        [Test]
        public void ASpendRestartsTheFullDelay()
        {
            var pool = ResourcePool.Create(new ResourceDefinition(100f, 10f, 1f));

            Assert.AreEqual(30f, pool.Consume(30f));
            Assert.AreEqual(1f, pool.DelayRemaining);

            pool.Update(0.6f);
            Assert.AreEqual(70f, pool.Value, "No recharge during the delay.");
            Assert.AreEqual(0.4f, pool.DelayRemaining, Tolerance);

            Assert.IsTrue(pool.TryConsume(10f));
            Assert.AreEqual(1f, pool.DelayRemaining, "The delay restarts in full; it does not add up.");
        }

        [Test]
        public void OnlyThePartOfTheStepAfterTheDelayRecharges()
        {
            var pool = ResourcePool.Create(new ResourceDefinition(100f, 10f, 1f));
            pool.Consume(50f);
            pool.Update(0.75f);

            pool.Update(0.5f);
            Assert.AreEqual(52.5f, pool.Value, Tolerance);
            Assert.AreEqual(0f, pool.DelayRemaining);
        }

        [Test]
        public void AFailedTryConsumeChangesNothing()
        {
            var pool = ResourcePool.Create(new ResourceDefinition(100f, 10f, 1f));
            pool.Value = 5f;

            Assert.IsFalse(pool.TryConsume(6f));
            Assert.AreEqual(5f, pool.Value);
            Assert.AreEqual(0f, pool.DelayRemaining);

            pool.Value = 0f;
            Assert.IsFalse(pool.TryConsume(0f), "An empty pool grants nothing, also a zero amount.");
        }

        [Test]
        public void ConsumeClampsAndReturnsTheRemovedAmount()
        {
            var pool = ResourcePool.Create(new ResourceDefinition(100f, 10f, 1f));
            pool.Value = 20f;

            Assert.AreEqual(20f, pool.Consume(50f));
            Assert.AreEqual(0f, pool.Value);
            Assert.AreEqual(1f, pool.DelayRemaining);

            pool.DelayRemaining = 0f;
            Assert.AreEqual(-30f, pool.Consume(-30f), "A negative amount adds.");
            Assert.AreEqual(30f, pool.Value);
            Assert.AreEqual(0f, pool.DelayRemaining, "Adding does not start the delay.");

            Assert.AreEqual(-70f, pool.Consume(-200f));
            Assert.AreEqual(100f, pool.Value);
        }

        [Test]
        public void ANegativeRateDrainsWithoutADelay()
        {
            var pool = ResourcePool.Create(new ResourceDefinition(100f, -10f, 1f));
            pool.Consume(10f);
            Assert.AreEqual(0f, pool.DelayRemaining, "Only a positive rate has a delay.");

            pool.Update(1f);
            Assert.AreEqual(80f, pool.Value, Tolerance);
            pool.Update(100f);
            Assert.AreEqual(0f, pool.Value);
        }

        [Test]
        public void ArmorNeverRegeneratesAndNeverDelays()
        {
            var armor = ResourcePool.CreateArmor(1000f);
            armor.Consume(400f);
            Assert.AreEqual(0f, armor.DelayRemaining);

            armor.Update(100f);
            Assert.AreEqual(600f, armor.Value);
        }

        [Test]
        public void AZeroMaximumPoolHasNothing()
        {
            var pool = ResourcePool.Create(new ResourceDefinition(0f, 10f, 1f));
            pool.Update(1f);
            Assert.AreEqual(0f, pool.Value);
            Assert.AreEqual(0f, pool.Consume(5f));
            Assert.IsFalse(pool.TryConsume(0f));
        }

        [Test]
        public void TheBattleRechargesShieldAndEnergyEveryTick()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Step();
            var ship = driver.Ship(1);

            ship.Armor.Consume(100f);
            ship.Shield.Consume(100f);
            ship.Energy.Consume(100f);
            driver.Step(100);

            // Shield: 1 s delay, then 1 s at 10/s. Energy: 0.5 s delay, then 1.5 s at 20/s.
            Assert.AreEqual(900f, ship.Armor.Value);
            Assert.AreEqual(410f, ship.Shield.Value, 0.01f);
            Assert.AreEqual(230f, ship.Energy.Value, 0.01f);
        }
    }
}
