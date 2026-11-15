using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 13: legacy armor repair (ArmorRepairRate, ArmorRepairCooldown), the examples of "Ship stats (13)"
    // in TODO/battle_simulator_phase5_part2.md. A bionic ship with armor 1,000: 10 per second (0.2 per tick) after 1 s.
    public class ArmorRepairTests
    {
        private const float Tolerance = 1e-3f;

        private static ShipDefinition Bionic() =>
            Fixtures.ShipWith(shield: new ResourceDefinition(0f), armorRepairRate: 10f, armorRepairDelay: 1f);

        private static Driver Duel(float cooldown)
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: cooldown), Bionic());
            driver.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            return driver;
        }

        // Only a hit starts the delay.
        private static void StepUntilHit(Driver driver)
        {
            for (var i = 0; i < 20; ++i)
            {
                driver.Step();
                if (driver.Ship(101).Armor.DelayRemaining > 0f) return;
            }
            Assert.Fail("The bolt did not hit.");
        }

        [Test]
        public void TheDefinitionGivesTheArmorPoolItsRateAndDelay()
        {
            var ship = Bionic();
            Assert.AreEqual(10f, ship.ArmorRepairRate);
            Assert.AreEqual(1f, ship.ArmorRepairDelay);
            Assert.AreEqual(0f, Fixtures.Ship.ArmorRepairRate, "A ship without a source does not repair.");

            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(armorRepairRate: -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(armorRepairRate: float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(armorRepairRate: float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(armorRepairDelay: -1f));
        }

        // Example 1: no repair during the delay, then 0.2 per tick up to the maximum.
        [Test]
        public void AHitStopsTheRepairForTheDelayThenArmorRepairsAtItsRate()
        {
            var driver = Duel(cooldown: 100f);
            var ship = driver.Ship(101);
            ship.Armor.Value = 990f;
            var before = ship.Armor.Value;
            StepUntilHit(driver);
            var armor = ship.Armor.Value;
            Assert.Less(armor, before, "The bolt takes 5; the ticks before it repaired 0.2 each.");
            Assert.AreEqual(1f, ship.Armor.DelayRemaining);

            driver.Step(49);
            Assert.AreEqual(armor, ship.Armor.Value, "No repair during the delay.");

            driver.Step(51);
            Assert.AreEqual(armor + 10f, ship.Armor.Value, 0.25f);

            driver.Step(100);
            Assert.AreEqual(1000f, ship.Armor.Value, "The repair stops at the maximum.");
        }

        // Example 2: each hit restarts the delay, so armor never grows while hits come every 0.5 s.
        [Test]
        public void HitsWithinTheDelayKeepTheArmorFromRepairing()
        {
            var driver = Duel(cooldown: 0.5f);
            StepUntilHit(driver);
            var ship = driver.Ship(101);
            var armor = ship.Armor.Value;
            for (var i = 0; i < 150; ++i)
            {
                driver.Step();
                Assert.LessOrEqual(ship.Armor.Value, armor);
                armor = ship.Armor.Value;
            }
            Assert.Less(armor, 980f);
        }

        // Examples 3 and 4 on the damage path: a repair alone starts no delay; armor damage restarts it even when a
        // larger repair in the same tick nets it out (question 2 (b)).
        [TestCase(0f, 508f, 0f)]
        [TestCase(5f, 503f, 1f)]
        public void ArmorDamageRestartsTheDelayEvenWhenTheRepairIsLarger(float damage, float armorAfter, float delayAfter)
        {
            var driver = CannonTests.Duel(Fixtures.Ship, Fixtures.Ship);
            var definition = Bionic();
            var shield = ResourcePool.Create(definition.Shield);
            var armor = ResourcePool.Create(definition.ArmorResource);
            var energy = ResourcePool.Create(definition.Energy);
            armor.Value = 500f;
            var amounts = new float[LegacyDamageModel.Types.Count];
            amounts[LegacyDamageModel.Impact] = damage;

            driver.Battle.ApplyShieldFirst(ref shield, ref armor, ref energy, amounts, new PendingEffects { Repair = 8f },
                definition, 0f, new float[amounts.Length]);
            Assert.AreEqual(armorAfter, armor.Value, Tolerance);
            Assert.AreEqual(delayAfter, armor.DelayRemaining);
        }

        // Without a repair rate the same tick starts no delay: the baseline ships keep their results.
        [Test]
        public void ArmorWithoutRepairStartsNoDelay()
        {
            var driver = CannonTests.Duel(Fixtures.Ship, Fixtures.Ship);
            var definition = Fixtures.Ship;
            var shield = ResourcePool.Create(new ResourceDefinition(0f));
            var armor = ResourcePool.Create(definition.ArmorResource);
            var energy = ResourcePool.Create(definition.Energy);
            var amounts = new float[LegacyDamageModel.Types.Count];
            amounts[LegacyDamageModel.Impact] = 5f;

            driver.Battle.ApplyShieldFirst(ref shield, ref armor, ref energy, amounts, new PendingEffects { Repair = 8f },
                definition, 0f, new float[amounts.Length]);
            Assert.AreEqual(1000f, armor.Value);
            Assert.AreEqual(0f, armor.DelayRemaining);
        }

        // Example 5: the timer drain restarts the delay every tick, so the drained armor does not repair.
        [Test]
        public void TheTimerDrainHoldsTheRepair()
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Bionic()),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
            }, timeLimitTicks: 10, timerMode: TimerMode.DrainPlayerHp));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step(200);

            var ship = driver.Ship(1);
            Assert.Less(ship.Armor.Value, 1000f);
            Assert.AreEqual(1f, ship.Armor.DelayRemaining);
            var armor = ship.Armor.Value;
            driver.Step(50);
            Assert.Less(ship.Armor.Value, armor);
        }

        // Example 8: the owner repairs; the copy does not, and takes the owner's armor from a correction.
        [Test]
        public void ARemoteCopyTakesTheRepairFromCorrections()
        {
            var p = new NetPair(ship101: Bionic());
            p.A.Ship(101).Armor.Consume(400f);
            p.B.Ship(101).Armor.Value = 600f;
            p.Step(100);

            var owner = p.A.Ship(101);
            var copy = p.B.Ship(101);
            Assert.AreEqual(610f, owner.Armor.Value, 0.25f);
            Assert.AreEqual(600f, copy.Armor.Value, "The copy does not repair by itself.");

            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            // One level of armor 1,000 is 3.90625.
            Assert.AreEqual(owner.Armor.Value, copy.Armor.Value, 4.2f);
        }

        // Example 9: armor repairs during the retreat; the redeployed ship takes the deficit as a spend, so its delay
        // starts at deployment (legacy ShipInfo.Create).
        [Test]
        public void ARedeployedShipKeepsItsDamageAndStartsTheDelay()
        {
            var driver = new Driver(Fixtures.Setup(Bionic()));
            driver.Deploy(1);
            driver.Step();
            driver.Ship(1).Armor.Consume(400f);
            driver.Retreat(driver.Entity(1));
            driver.StepUntil(driver.Tick + Battle.RetreatTicks + 1);

            // 350 ticks of retreat, 50 of them in the delay: 300 x 0.2 = 60 repaired.
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            var deficit = driver.Slot(1).ArmorDeficit;
            Assert.AreEqual(340f, deficit, 0.5f);

            driver.Deploy(1);
            driver.Step();
            var ship = driver.Ship(1);
            Assert.AreEqual(1000f - deficit, ship.Armor.Value, Tolerance);
            Assert.Greater(ship.Armor.DelayRemaining, 0.95f);
        }
    }
}
