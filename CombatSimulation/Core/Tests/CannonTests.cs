using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Q1: the cannon (legacy CommonCannon on a FixedPlatform), its bullet and its recoil.
    public class CannonTests
    {
        private const float Tolerance = 1e-4f;

        private static readonly Vec2 Shooter = new Vec2(50f, 50f);

        // The enemy stays far behind the shooter, so no shot hits it.
        private static readonly Vec2 FarAway = new Vec2(150f, 150f);

        [Test]
        public void HeldBitFiresEveryCooldownAndTheFirstShotWaitsForTheBarrel()
        {
            // 0.1 s cooldown = 5 ticks. Deploy at tick 0; legacy FixedPlatform is ready after more than 0.04 s.
            var driver = Duel(Fixtures.CannonShip(cooldown: 0.1f));
            driver.Control(1, systems: 1);

            Assert.AreEqual(new long[] { 2, 7, 12, 17 }, ShotTicks(driver.StepCollecting(19)));
        }

        [Test]
        public void TheBarrelCooldownCapsAFastWeapon()
        {
            var driver = Duel(Fixtures.CannonShip(cooldown: 0f));
            driver.Control(1, systems: 1);

            Assert.AreEqual(new long[] { 2, 5, 8, 11 }, ShotTicks(driver.StepCollecting(11)));
        }

        [Test]
        public void ACooldownBetweenTicksRoundsUp()
        {
            // 1/30 s is 1.67 ticks, so the weapon waits 2 ticks; the 3-tick barrel cooldown still decides.
            // 0.07 s is 3.5 ticks: 4 ticks.
            Assert.AreEqual(2, new CannonDefinition(0, 1f / 30f, 0f, 0f, Fixtures.Bolt).CooldownTicks);
            Assert.AreEqual(4, new CannonDefinition(0, 0.07f, 0f, 0f, Fixtures.Bolt).CooldownTicks);
            Assert.AreEqual(5, new CannonDefinition(0, 0.1f, 0f, 0f, Fixtures.Bolt).CooldownTicks);
            Assert.AreEqual(3, ShipEntity.BarrelCooldownTicks);
        }

        [Test]
        public void ReleasedBitStopsFiring()
        {
            var driver = Duel(Fixtures.CannonShip(cooldown: 0.1f));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 8);

            Assert.AreEqual(new long[] { 2, 7 }, ShotTicks(driver.StepCollecting(19)));
        }

        [Test]
        public void EachShotSpendsItsEnergyCost()
        {
            // 5 energy and no recharge: two shots of 2, then 1 is left and the cannon stays silent.
            var driver = Duel(Fixtures.CannonShip(cooldown: 0f, energyCost: 2f, energy: new ResourceDefinition(5f)));
            driver.Control(1, systems: 1);

            Assert.AreEqual(new long[] { 2, 5 }, ShotTicks(driver.StepCollecting(30)));
            Assert.AreEqual(1f, driver.Ship(1).Energy.Value, Tolerance);
        }

        [Test]
        public void AFailedShotDoesNotStartTheEnergyDelay()
        {
            // 1 energy, 2 per shot: the cannon never fires and the recharge delay never starts.
            var energy = new ResourceDefinition(1f, 1f, 2f);
            var driver = Duel(Fixtures.CannonShip(energyCost: 2f, energy: energy));
            driver.Control(1, systems: 1);

            Assert.IsEmpty(ShotTicks(driver.StepCollecting(10)));
            Assert.AreEqual(0f, driver.Ship(1).Energy.DelayRemaining);
            Assert.AreEqual(1f, driver.Ship(1).Energy.Value);
        }

        [Test]
        public void ARetreatingShipDoesNotFire()
        {
            var driver = Duel(Fixtures.CannonShip(cooldown: 0f));
            driver.Retreat(driver.Entity(1));
            driver.Control(1, systems: 1);

            Assert.IsEmpty(ShotTicks(driver.StepCollecting(20)));
        }

        [TestCase(0f)]
        [TestCase(1f)]
        [TestCase(2f)]
        public void TheBulletInheritsTheShipVelocity(float parentVelocityEffect)
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, parentVelocityEffect: parentVelocityEffect);
            var driver = Duel(Fixtures.CannonShip(bullet));
            driver.Place(1, Shooter, new Vec2(0f, 4f));
            driver.Control(1, systems: 1);

            var state = FirstBullet(driver);
            Assert.AreEqual(20f, state.LinearVelocity.X, Tolerance);
            Assert.AreEqual(4f * parentVelocityEffect, state.LinearVelocity.Y, Tolerance);
        }

        [Test]
        public void TheBulletStartsAtTheMuzzleOfTheTurnedShip()
        {
            // Ship heading 90°: the muzzle at (1.5, 0.5) in the ship frame is at (−0.5, 1.5) in the world.
            var driver = Duel(Fixtures.CannonShip(muzzle: new Vec2(1.5f, 0.5f)));
            driver.Place(1, Shooter, Vec2.Zero, 90f * Fixtures.Degrees);
            driver.Control(1, course: 90f * Fixtures.Degrees, systems: 1);

            var state = FirstBullet(driver);

            // The snapshot is taken after one physics step of 0.02 s at 20 u/s.
            Assert.AreEqual(Shooter.X - 0.5f, state.Position.X, 1e-3f);
            Assert.AreEqual(Shooter.Y + 1.5f + 0.4f, state.Position.Y, 1e-3f);
            Assert.AreEqual(90f * Fixtures.Degrees, state.Angle, 1e-4f);
        }

        [Test]
        public void SpreadStaysWithinHalfTheAngleEachWay()
        {
            var angles = SpreadAngles(7, 60);

            Assert.IsTrue(angles.All(a => Math.Abs(a) <= 15f * Fixtures.Degrees + 1e-5f));
            Assert.Greater(angles.Max() - angles.Min(), 15f * Fixtures.Degrees, "The shots spread out.");
        }

        [Test]
        public void SpreadDependsOnlyOnTheMatchSeed()
        {
            CollectionAssert.AreEqual(SpreadAngles(7, 20), SpreadAngles(7, 20));
            CollectionAssert.AreNotEqual(SpreadAngles(7, 20), SpreadAngles(8, 20));
        }

        [Test]
        public void RecoilPushesTheShipBackAlongTheShot()
        {
            // Recoil = weight × speed × ParentVelocityEffect = 0.5 × 20 × 1 = 10; the ship mass is 10.
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, weight: 0.5f);
            var driver = Duel(Fixtures.CannonShip(bullet, mass: 10f));
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Control(1, systems: 1);

            FirstBullet(driver);
            var body = driver.Ship(1).Body;
            Assert.AreEqual(-1f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Y, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(body), Tolerance);
        }

        [Test]
        public void RecoilFromAnOffCentreBarrelSpinsTheShip()
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, weight: 0.5f);
            var driver = Duel(Fixtures.CannonShip(bullet, mass: 10f, muzzle: new Vec2(1f, 0.5f)));
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Control(1, systems: 1);

            FirstBullet(driver);
            var body = driver.Ship(1).Body;
            Assert.AreEqual(-1f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            Assert.Greater(driver.Battle.World.GetAngularVelocity(body), 0f, "A push behind the left side turns the ship left.");
        }

        [Test]
        public void AnInertDeviceIgnoresItsBitAndKeepsTheNextIndex()
        {
            // D1: device 0 is inert, the cannon is device 1. 5 energy and no recharge.
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new InertDeviceDefinition(), new CannonDefinition(0, 0.1f, 2f, 0f, Fixtures.Bolt) });

            var inertOnly = Duel(ship);
            inertOnly.Control(1, systems: 1);
            Assert.IsEmpty(ShotTicks(inertOnly.StepCollecting(10)));
            Assert.AreEqual(5f, inertOnly.Ship(1).Energy.Value);

            var both = Duel(ship);
            both.Control(1, systems: 3);
            var spawns = BulletSpawns(both.StepCollecting(10));
            Assert.AreEqual(new long[] { 2, 7 }, spawns.Select(e => e.Tick).ToArray());
            Assert.AreEqual(1f, both.Ship(1).Energy.Value, Tolerance);
        }

        // Slot 1 flies the given ship, slot 101 a plain enemy far away. Steps tick 0 (the deployment).
        internal static Driver Duel(ShipDefinition shooter, ShipDefinition enemy = null, ulong matchSeed = 7)
        {
            var driver = new Driver(Fixtures.Setup(shooter, enemy ?? Fixtures.Ship, matchSeed));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, FarAway, Vec2.Zero);
            return driver;
        }

        internal static List<BattleEvent> BulletSpawns(IEnumerable<BattleEvent> events) =>
            events.Where(e => e.Kind == BattleEventKind.Spawn && e.IsBullet).ToList();

        internal static List<BattleEvent> BulletRemovals(IEnumerable<BattleEvent> events) =>
            events.Where(e => e.Kind == BattleEventKind.Removal && e.IsBullet).ToList();

        private static long[] ShotTicks(IEnumerable<BattleEvent> events) =>
            BulletSpawns(events).Select(e => e.Tick).ToArray();

        // Steps until the first shot and returns the bullet in that tick's snapshot.
        private static BulletState FirstBullet(Driver driver)
        {
            for (var i = 0; i < 10; ++i)
            {
                driver.Step();
                var snapshot = driver.Battle.Snapshot;
                if (snapshot.Bullets.Length > 0) return snapshot.Bullets[0];
            }

            Assert.Fail("The cannon did not fire.");
            return default;
        }

        // Shot angles relative to the heading, with a 30° spread.
        private static List<float> SpreadAngles(ulong matchSeed, int shots)
        {
            var ship = Fixtures.CannonShip(cooldown: 0f, energyCost: 0f, spread: 30f * Fixtures.Degrees);
            var driver = Duel(ship, matchSeed: matchSeed);
            driver.Control(1, systems: 1);

            var angles = new List<float>();
            while (angles.Count < shots)
            {
                driver.Step();
                var snapshot = driver.Battle.Snapshot;
                foreach (var item in snapshot.Events)
                {
                    if (item.Kind != BattleEventKind.Spawn || !item.IsBullet) continue;
                    foreach (var bullet in snapshot.Bullets)
                        if (bullet.Id == item.EntityId)
                            angles.Add(ShipEngine.DeltaAngle(driver.Battle.World.GetAngle(driver.Ship(1).Body), bullet.Angle));
                }
            }
            return angles;
        }
    }
}
