using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 4c: homing with IgnoreRotation (legacy MagneticController). The bullet thrusts toward the velocity
    // that leads to its target; with a direction it turns toward its velocity, without one it never turns.
    public class MagneticTests
    {
        private const float Tolerance = 1e-4f;

        // 25 u/s² x 0.02 s.
        private const float ThrustPerTick = 0.5f;

        private static readonly Vec2 Shooter = new Vec2(50f, 50f);

        // The launch tick; the default enemy (Duel) at (150, 150) is beyond the 104 u search range.
        private const long SpawnTick = 2;

        private static readonly BulletDefinition Spark = new BulletDefinition(0.4f, 30f, 80f, 10f, weight: 0.5f,
            homing: true, magnetic: true, hasDirection: false);

        [Test]
        public void TheFixtureMatchesTheLegacyFormulas()
        {
            var pixie = Fixtures.Pixie;

            Assert.IsTrue(pixie.IsHoming);
            Assert.IsTrue(pixie.IsMagnetic);
            Assert.IsTrue(pixie.HasDirection);
            Assert.AreEqual(25f, pixie.Acceleration, 1e-5f);
            Assert.Throws<ArgumentException>(() => new BulletDefinition(0.4f, 30f, 80f, 10f, magnetic: true));
        }

        [Test]
        public void TheThrustAimsAtTheTargetPlusItsVelocityTimesTheTimeToCoverTheDistance()
        {
            // Target 30 u to the left, moving forward at 10 u/s: the lead point is (10, 30). The thrust has full size.
            var velocity = HomingSteering.MagneticThrust(new Vec2(30f, 0f), new Vec2(0f, 30f), new Vec2(10f, 0f), 30f, 25f);

            Assert.AreEqual(29.707645f, velocity.X, Tolerance);
            Assert.AreEqual(0.405621f, velocity.Y, Tolerance);
        }

        [Test]
        public void WithATargetTheThrustHasFullSizeEveryTick()
        {
            // The target is ahead, so the bullet is at full speed toward it and still thrusts (legacy jitter).
            var driver = Duel();
            driver.Place(101, new Vec2(80f, 52f), Vec2.Zero);
            Fire(driver);

            for (var i = 0; i < 3; ++i)
            {
                var before = Snapshot(driver).LinearVelocity;
                driver.Step();
                Assert.AreEqual(driver.Entity(101), Bullet(driver).TargetId);
                Assert.AreEqual(ThrustPerTick, (Snapshot(driver).LinearVelocity - before).Length, Tolerance);
            }
        }

        [Test]
        public void WithoutATargetTheBulletDriftsAndTurnsTowardItsVelocity()
        {
            // The ship velocity (0, 10) gives a velocity 18.4 degrees off the heading. Each tick the heading turns by
            // 10 x 0.02 of the angle left.
            var driver = Fire(Moving(Duel()));
            var launch = Snapshot(driver).LinearVelocity;
            var target = (float)Math.Atan2(launch.Y, launch.X);

            driver.Step();
            Assert.IsTrue(Bullet(driver).TargetId.IsNull);
            Assert.AreEqual(launch.X, Snapshot(driver).LinearVelocity.X, Tolerance);
            Assert.AreEqual(launch.Y, Snapshot(driver).LinearVelocity.Y, Tolerance);
            Assert.AreEqual(0.2f * target, Snapshot(driver).Angle, 1e-5f);

            driver.Step();
            Assert.AreEqual(0.2f * target + 0.2f * 0.8f * target, Snapshot(driver).Angle, 1e-5f);
        }

        [Test]
        public void ASparkNeverTurns()
        {
            var driver = Fire(Moving(Duel(Fixtures.CannonShip(Spark, cooldown: 10f))));
            driver.Place(101, new Vec2(55f, 70f), Vec2.Zero);

            driver.Step(5);
            Assert.AreEqual(driver.Entity(101), Bullet(driver).TargetId);
            Assert.AreEqual(0f, Snapshot(driver).Angle);
        }

        [Test]
        public void ABulletWithADirectionPrefersTheConeAndASparkTheNearestEnemy()
        {
            // 101 is ahead at about 38 u, 102 behind at about 10 u.
            Assert.AreEqual(101u, FirstTarget(Fixtures.Pixie));
            Assert.AreEqual(102u, FirstTarget(Spark));
        }

        private static uint FirstTarget(BulletDefinition bullet)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(bullet, cooldown: 10f)),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, new Vec2(90f, 50f), Vec2.Zero);
            driver.Place(102, new Vec2(40f, 50f), Vec2.Zero);
            Fire(driver);

            driver.Step();
            var target = Bullet(driver).TargetId;
            if (target == driver.Entity(101)) return 101;
            if (target == driver.Entity(102)) return 102;
            throw new AssertionException("No target.");
        }

        private static Driver Duel(ShipDefinition shooter = null)
        {
            var driver = CannonTests.Duel(shooter ?? Fixtures.CannonShip(Fixtures.Pixie, cooldown: 10f));
            driver.Place(1, Shooter, Vec2.Zero);
            return driver;
        }

        private static Driver Moving(Driver driver)
        {
            driver.Place(1, Shooter, new Vec2(0f, 10f));
            return driver;
        }

        // Holds the trigger and steps through the launch tick.
        private static Driver Fire(Driver driver)
        {
            driver.Control(1, systems: 1);
            driver.StepUntil(SpawnTick + 1);
            Assert.AreEqual(1, driver.Battle.Bullets.AliveCount, "The bullet is launched at tick 2.");
            return driver;
        }

        private static BulletState Snapshot(Driver driver) => driver.Battle.Snapshot.Bullets.ToArray().Single();

        private static ref BulletEntity Bullet(Driver driver)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i))
                    return ref bullets[i];

            throw new AssertionException("No bullet is alive.");
        }
    }
}
