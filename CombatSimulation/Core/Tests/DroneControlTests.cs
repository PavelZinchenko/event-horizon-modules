using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class DroneControlTests
    {
        private static BulletDefinition Control(bool crazy = false, bool area = false, float drain = 0f) =>
            new BulletDefinition(area ? 8f : 0.36f, 0f, 0f, 0f, lifetime: 2f,
                impact: area ? BulletImpact.AllTargets : BulletImpact.FirstTarget, friendlyFire: area,
                effects: new ImpactEffects(captureDrones: !crazy, driveDronesCrazy: crazy, energyDrain: drain));

        private static ShipDefinition Shooter(BulletDefinition bullet) =>
            Fixtures.CannonShip(bullet, cooldown: 10f, energyCost: 0f);

        [TestCase(false)]
        [TestCase(true)]
        public void DroneChangesOwnerButStaysInItsOriginalBay(bool crazy)
        {
            var driver = DroneBayTests.Start(enemy: Shooter(Control(crazy)));
            var drone = DroneBayTests.Launch(driver);
            var bay = drone.Bay;
            DroneBayTests.Place(driver, drone, new Vec2(101f, 100f));
            driver.Control(101, systems: 1);
            driver.Step();
            Assert.AreEqual(crazy ? Allegiance.Undefined : driver.Ship(101).Allegiance, drone.Allegiance);
            Assert.AreEqual(crazy ? default(EntityId) : driver.Entity(101), drone.MothershipId);
            Assert.AreSame(bay, drone.Bay);
            Assert.AreEqual(1, bay.ActiveCount);
            Assert.AreEqual(0, bay.InHangar);
            Assert.AreEqual(0, drone.Side);
            Assert.IsEmpty(driver.Battle.Snapshot.Bullets.ToArray(), "HitFirstTarget stops even without damage.");
            driver.Battle.QueueDeath(drone.Id);
            driver.Step();
            Assert.AreEqual(0, bay.ActiveCount);
            Assert.IsNull(driver.Battle.FindShip(drone.Id));
        }

        [Test]
        public void CaptureStopsOnAShipWithoutChangingIt()
        {
            var driver = CannonTests.Duel(Shooter(Control()), Fixtures.Ship);
            driver.Place(101, new Vec2(52f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(2);
            Assert.AreEqual(Allegiances.OfSide(1), driver.Ship(101).Allegiance);
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value);
            Assert.IsEmpty(driver.Battle.Snapshot.Bullets.ToArray());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EmpAffectsAlliedAndEnemyDronesButNotTheFirersOwn(bool allied)
        {
            var empCarrier = Fixtures.ShipWith(barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new DroneBayDefinition(1, 0f, DroneBayTests.Drone()),
                    new CannonDefinition(0, 10f, 0f, 0f, Control(crazy: true, area: true, drain: 10f)) });
            var driver = DroneBayTests.Start(enemy: empCarrier);
            driver.Control(101, systems: 1);
            var target = DroneBayTests.Launch(driver);
            var own = driver.Battle.Snapshot.Ships.ToArray().Single(s => s.IsDrone && s.Side == 1);
            var ownDrone = driver.Battle.FindShip(own.Id);
            if (allied) target.Allegiance = driver.Ship(101).Allegiance;
            DroneBayTests.Place(driver, target, new Vec2(104f, 100f));
            DroneBayTests.Place(driver, ownDrone, new Vec2(104f, 103f));
            driver.Control(101, systems: 2);
            driver.Step();
            Assert.AreEqual(Allegiance.Undefined, target.Allegiance);
            Assert.IsTrue(target.MothershipId.IsNull);
            Assert.AreEqual(90f, target.Energy.Value);
            Assert.AreEqual(driver.Ship(101).Allegiance, ownDrone.Allegiance);
            Assert.AreEqual(driver.Entity(101), ownDrone.MothershipId);
            Assert.AreEqual(100f, ownDrone.Energy.Value);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissileChangesFilterAndSearchesAgainWithoutFollowingItsOldShip(bool crazy)
        {
            var missile = new BulletDefinition(0.2f, 15f, 30f, 0f, hitPoints: 1000f, homing: true,
                startingVelocityMultiplier: 1f);
            var driver = CannonTests.Duel(Shooter(Control(crazy, area: true)), Shooter(missile));
            driver.Place(101, new Vec2(58f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(101, systems: 1);
            driver.Step(2);
            driver.Control(1, systems: 1);
            driver.Step();
            var bullets = driver.Battle.Bullets;
            var index = Enumerable.Range(0, bullets.Capacity).Single(i => bullets.IsAlive(i) && bullets[i].Definition == missile);
            ref var target = ref bullets[index];
            Assert.AreEqual(crazy ? Allegiance.Undefined : driver.Ship(1).Allegiance, target.Allegiance);
            Assert.IsTrue(target.TargetId.IsNull);
            Assert.AreEqual(driver.Tick, target.NextSearchTick);
            Assert.AreEqual(crazy ? default(EntityId) : driver.Entity(1), target.SourceShipId);
            driver.Step();
            Assert.AreEqual(crazy ? driver.Entity(1) : driver.Entity(101), target.TargetId);
            driver.Battle.Submit(ShipCommand.ChangeAllegiance(driver.BattleId, 10000, driver.Tick,
                driver.Entity(101), Allegiance.Neutral));
            driver.Step();
            Assert.AreEqual(crazy ? Allegiance.Undefined : driver.Ship(1).Allegiance, target.Allegiance);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemoteOwnersDisableDroneEffectsButKeepTheDrain(bool crazy)
        {
            var pair = new NetPair(Shooter(Control(crazy, area: true, drain: 10f)), DroneBayTests.Carrier());
            pair.A.Control(101, systems: 1);
            pair.Step(23);
            foreach (var driver in new[] { pair.A, pair.B })
            {
                driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(101, new Vec2(80f, 50f), Vec2.Zero);
                var drone = driver.Battle.Snapshot.Ships.ToArray().Single(s => s.IsDrone);
                DroneBayTests.Place(driver, driver.Battle.FindShip(drone.Id), new Vec2(53f, 50f));
            }
            pair.B.Control(1, systems: 1);
            pair.Step(4);
            foreach (var driver in new[] { pair.A, pair.B })
            {
                var drone = driver.Battle.Snapshot.Ships.ToArray().Single(s => s.IsDrone);
                Assert.AreEqual(Allegiances.OfSide(1), drone.Allegiance);
                Assert.AreEqual(driver.Entity(101), drone.MothershipId);
                Assert.IsNull(drone.MothershipOverride);
            }
            Assert.AreEqual(90f, pair.A.Battle.Snapshot.Ships.ToArray().Single(s => s.IsDrone).Energy.Value);
        }

        [Test]
        public void OverTimeOwnershipChangesOnlyOnEntry()
        {
            var area = new BulletDefinition(4f, 0f, 0f, 0f, lifetime: 2f, impact: BulletImpact.OverTime,
                friendlyFire: true, effects: new ImpactEffects(driveDronesCrazy: true));
            var driver = DroneBayTests.Start(enemy: Shooter(area));
            var drone = DroneBayTests.Launch(driver);
            DroneBayTests.Place(driver, drone, new Vec2(103f, 100f));
            driver.Control(101, systems: 1);
            driver.Step();
            Assert.AreEqual(Allegiance.Undefined, drone.Allegiance);
            drone.MothershipOverride = driver.Entity(1);
            driver.Step();
            Assert.AreEqual(driver.Entity(1), drone.MothershipId, "A continuing contact does not clear a new owner.");
            DroneBayTests.Place(driver, drone, new Vec2(120f, 100f));
            driver.Step();
            DroneBayTests.Place(driver, drone, new Vec2(103f, 100f));
            driver.Step();
            Assert.IsTrue(drone.MothershipId.IsNull);
        }

        [Test]
        public void ChargeKeepsBothOwnershipEffects()
        {
            var effects = new ImpactEffects(captureDrones: true, driveDronesCrazy: true).WithCharge(0f);
            Assert.IsTrue(effects.CaptureDrones);
            Assert.IsTrue(effects.DriveDronesCrazy);
        }
    }
}
