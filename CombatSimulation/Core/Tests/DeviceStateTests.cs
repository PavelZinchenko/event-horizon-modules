using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 4, step 3b: the device state in the snapshot (legacy CanBeActivated, Active, Cooldown, ActiveBullet and
    // the drone bay counts).
    public class DeviceStateTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void ACannonIsReadyWhenItsTimersAllowAndActiveOnlyInItsShotTick()
        {
            // 0.1 s cooldown = 5 ticks, 3-tick barrel. Shots at ticks 2 and 7 (CannonTests).
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0.1f));

            // End of tick 0: the barrel has run 2 of its 3 ticks at tick 1.
            var state = State(driver);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(1f / 3f, state.Cooldown, Tolerance);

            driver.Control(1, systems: 1);
            driver.Step();
            state = State(driver);
            Assert.IsTrue(state.Ready);
            Assert.IsFalse(state.Active);
            Assert.AreEqual(0f, state.Cooldown);

            driver.Step();
            state = State(driver);
            Assert.AreEqual(2, driver.Battle.Snapshot.Tick);
            Assert.IsTrue(state.Active);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(0.8f, state.Cooldown, Tolerance);

            driver.Step();
            state = State(driver);
            Assert.IsFalse(state.Active);
            Assert.AreEqual(0.6f, state.Cooldown, Tolerance);

            driver.Step(3);
            state = State(driver);
            Assert.AreEqual(6, driver.Battle.Snapshot.Tick);
            Assert.IsTrue(state.Ready);
            Assert.AreEqual(0f, state.Cooldown);
        }

        [Test]
        public void ACannonWithoutEnergyForItsCostIsNotReady()
        {
            // 3 energy without recharge, cost 2: one shot leaves 1.
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0.1f, energy: new ResourceDefinition(3f)));
            driver.Control(1, systems: 1);
            driver.StepUntil(7);

            var state = State(driver);
            Assert.AreEqual(0f, state.Cooldown);
            Assert.IsFalse(state.Ready);
            Assert.IsFalse(state.Active);
        }

        [Test]
        public void ABeamReportsItsBulletWhileAttachedAndItsCooldownAfterRelease()
        {
            // 0.1 s cooldown = 5 ticks. No energy check in Ready (legacy ContinuousCannon).
            var driver = CannonTests.Duel(Fixtures.ShipWith(energy: new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new[] { new BeamDefinition(0, 0.1f, 2f, 25f, 30f, 50f) }));
            driver.Control(1, systems: 1);
            driver.StepUntil(4);

            var snapshot = driver.Battle.Snapshot;
            var state = State(driver);
            Assert.AreEqual(1, snapshot.Bullets.Length);
            Assert.AreEqual(snapshot.Bullets[0].Id, state.Beam);
            Assert.IsTrue(state.Ready);
            Assert.IsTrue(state.Active);

            driver.Control(1, systems: 0);
            driver.Step();
            state = State(driver);
            Assert.IsTrue(state.Beam.IsNull);
            Assert.IsFalse(state.Active);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(0.8f, state.Cooldown, Tolerance);
        }

        [Test]
        public void PointDefenseStaysActiveWhileHeldAndStartsItsCooldownOnRelease()
        {
            // 0.1 s activation cooldown = 5 ticks, cost 1.
            var driver = CannonTests.Duel(Fixtures.ShipWith(energy: new ResourceDefinition(100f),
                devices: new[] { new PointDefenseDefinition(4f, 0.1f, 1f) }));
            driver.Control(1, systems: 1);
            driver.Step(3);

            var state = State(driver);
            Assert.IsTrue(state.Active);
            Assert.IsTrue(state.Ready);

            driver.Control(1, systems: 0);
            driver.Step();
            state = State(driver);
            Assert.IsFalse(state.Active);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(0.8f, state.Cooldown, Tolerance);

            driver.Step(4);
            Assert.IsTrue(State(driver).Ready);
        }

        [Test]
        public void PointDefenseWithoutEnergyForItsCostIsNotReady()
        {
            var driver = CannonTests.Duel(Fixtures.ShipWith(energy: new ResourceDefinition(0.5f),
                devices: new[] { new PointDefenseDefinition(4f, 0f, 1f) }));
            driver.Control(1, systems: 1);
            driver.Step(3);

            var state = State(driver);
            Assert.IsFalse(state.Active);
            Assert.IsFalse(state.Ready);
        }

        [Test]
        public void ADroneBayCountsItsHangarAndItsDronesInFlight()
        {
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(capacity: 2));
            var drone = DroneBayTests.Launch(driver);

            var state = State(driver);
            Assert.AreEqual(1, state.InHangar);
            Assert.AreEqual(1, state.ActiveDrones);
            Assert.IsFalse(state.Ready);
            Assert.Greater(state.Cooldown, 0f);

            driver.Control(1, systems: 0);
            driver.Battle.QueueDeath(drone.Id);
            driver.Step();
            state = State(driver);
            Assert.AreEqual(1, state.InHangar);
            Assert.AreEqual(0, state.ActiveDrones);
            Assert.IsFalse(state.Active);

            driver.Step(DroneBayDevice.LaunchCooldownTicks);
            Assert.IsTrue(State(driver).Ready);
        }

        [Test]
        public void AnEmptyHangarIsNotReady()
        {
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(capacity: 1));
            DroneBayTests.Launch(driver);
            driver.Step(DroneBayDevice.LaunchCooldownTicks);

            var state = State(driver);
            Assert.AreEqual(0, state.InHangar);
            Assert.AreEqual(0f, state.Cooldown);
            Assert.IsFalse(state.Ready);
            Assert.IsFalse(state.Active);
        }

        [Test]
        public void AnInertDeviceIsNeverReadyOrActive()
        {
            var driver = CannonTests.Duel(Fixtures.ShipWith(devices: new DeviceDefinition[] { new InertDeviceDefinition() }));
            driver.Control(1, systems: 1);
            driver.Step(3);

            var state = State(driver);
            Assert.IsFalse(state.Ready);
            Assert.IsFalse(state.Active);
            Assert.AreEqual(1f, state.Cooldown);
        }

        [Test]
        public void EveryShipAndDroneHasOneEntryPerDevice()
        {
            // The carrier has one bay; its drone has a cannon and an inert device; the enemy has no device.
            var drone = DroneBayTests.Drone(barrels: new[] { new BarrelDefinition(new Vec2(0.5f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0.1f, 1f, 0f, Fixtures.Bolt), new InertDeviceDefinition() });
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(capacity: 2, drone: drone));
            DroneBayTests.Launch(driver);

            var snapshot = driver.Battle.Snapshot;
            var ships = snapshot.Ships;
            Assert.AreEqual(3, ships.Length);
            Assert.AreEqual(1, snapshot.GetDevices(ships[0]).Length);
            Assert.AreEqual(1, snapshot.GetDevices(ships[0])[0].ActiveDrones);
            Assert.AreEqual(0, snapshot.GetDevices(ships[1]).Length);
            Assert.IsTrue(ships[2].IsDrone);
            var devices = snapshot.GetDevices(ships[2]);
            Assert.AreEqual(2, devices.Length);
            Assert.AreEqual(0, devices[0].InHangar);
            Assert.AreEqual(DeviceState.Inert, devices[1]);
        }

        [Test]
        public void ARetreatingShipHasNoActiveDevice()
        {
            var driver = CannonTests.Duel(Fixtures.ShipWith(energy: new ResourceDefinition(100f),
                devices: new[] { new PointDefenseDefinition(4f, 0f, 1f) }));
            driver.Control(1, systems: 1);
            driver.Step(3);
            Assert.IsTrue(State(driver).Active);

            driver.Retreat(driver.Entity(1));
            driver.Step(2);
            Assert.IsTrue(driver.Ship(1).RetreatCompletionTick != ShipEntity.NoRetreat);
            Assert.IsFalse(State(driver).Active);
        }

        private static DeviceState State(Driver driver, uint rosterId = 1, int index = 0)
        {
            var snapshot = driver.Battle.Snapshot;
            Assert.IsTrue(snapshot.TryGetShip(driver.Entity(rosterId), out var ship));
            return snapshot.GetDevices(ship)[index];
        }
    }
}
