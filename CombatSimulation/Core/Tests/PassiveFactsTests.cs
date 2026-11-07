using System;
using System.Collections.Generic;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 9a: the passive facts in a battle. The mapper turns a jammer into High priority and missile
    // camouflage into ShipDefinition.ChanceToAvoidMissile; the homing search of stage 4 reads both.
    public class PassiveFactsTests
    {
        private static readonly Vec2 Shooter = new Vec2(50f, 50f);

        // The launch tick of MagneticTests.Fire.
        private const long SpawnTick = 2;

        [Test]
        public void TheChanceMustBeAFraction()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(chanceToAvoidMissile: -0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(chanceToAvoidMissile: 1.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(chanceToAvoidMissile: float.NaN));
            Assert.AreEqual(0.9f, Fixtures.ShipWith(chanceToAvoidMissile: 0.9f).ChanceToAvoidMissile);
        }

        [Test]
        public void AMissileNeverTargetsAShipWithFullCamouflage()
        {
            // Both are ahead of the missile; the camouflaged one is nearer.
            var driver = Battle(Fixtures.ShipWith(chanceToAvoidMissile: 1f), Fixtures.Ship);

            for (var i = 0; i < 3; ++i)
            {
                driver.Step();
                Assert.AreEqual(driver.Entity(102), MissileTarget(driver));
            }
        }

        [Test]
        public void AMissilePrefersAHigherPriorityInItsCone()
        {
            var normal = Battle(Fixtures.Ship, Fixtures.Ship);
            normal.Step();
            Assert.AreEqual(normal.Entity(101), MissileTarget(normal), "The nearer one at equal priority.");

            var jammer = Battle(Fixtures.Ship, Fixtures.ShipWith(targetPriority: TargetPriority.High));
            jammer.Step();
            Assert.AreEqual(jammer.Entity(102), MissileTarget(jammer));
        }

        // A Pixie (cone 30°, search 104 u) fired along +X at enemies 101 (20 u ahead) and 102 (40 u ahead).
        private static Driver Battle(ShipDefinition near, ShipDefinition far)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(Fixtures.Pixie, cooldown: 10f)),
                new RosterSlotSetup(new RosterId(101), 1, near),
                new RosterSlotSetup(new RosterId(102), 1, far),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, Shooter + new Vec2(20f, 0f), Vec2.Zero);
            driver.Place(102, Shooter + new Vec2(40f, 1f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.StepUntil(SpawnTick + 1);
            Assert.AreEqual(1, driver.Battle.Bullets.AliveCount, "The missile is launched at tick 2.");
            return driver;
        }

        private static EntityId MissileTarget(Driver driver)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i))
                    return bullets[i].TargetId;

            throw new AssertionException("No missile is alive.");
        }
    }
}
