using System;
using System.Linq;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 10a: legacy DroneReplicator (TODO/battle_simulator_phase5.md, "Child units (10)"). The carrier
    // deploys at tick 0; a build time of 1 s is 50 ticks; the bays refresh at ticks 0, 26, 52, 78, 104, ...
    public class DroneReplicatorTests
    {
        private const int Bay = 1;
        private const ulong BayBit = 1UL << Bay;

        private static ShipDefinition Carrier(int capacity = 1, int buildExtraCycles = 0, int bays = 1)
        {
            var devices = new DeviceDefinition[1 + bays];
            devices[0] = new DroneReplicatorDefinition(1f);
            for (var i = 1; i <= bays; ++i)
                devices[i] = new DroneBayDefinition(capacity, 0f, DroneBayTests.Drone(), buildExtraCycles);
            return Fixtures.ShipWith(energy: new ResourceDefinition(1000f), devices: devices);
        }

        private static DroneBayInventory Inventory(Driver driver, int index = Bay) => driver.Slot(1).GetBay(index);

        // Launches one drone at tick 20 and releases the key.
        private static EntityId LaunchOne(Driver driver, ulong systems = BayBit)
        {
            driver.Control(1, systems: systems);
            driver.StepUntil(21);
            driver.Control(1, systems: 0);
            return DroneBayTests.Drones(driver).Single().Id;
        }

        private static void Lose(Driver driver, EntityId drone)
        {
            driver.Battle.QueueDeath(drone);
            driver.Step();
            Assert.IsNull(driver.Battle.FindShip(drone));
        }

        [Test]
        public void DefinitionRejectsInvalidBuildTimeAndALaunchedShipWithAReplicator()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new DroneReplicatorDefinition(0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DroneReplicatorDefinition(float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DroneReplicatorDefinition(float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DroneBayDefinition(1, 0f, DroneBayTests.Drone(), -1));
            Assert.Throws<ArgumentException>(() => new DroneBayDefinition(1, 0f,
                DroneBayTests.Drone(devices: new DeviceDefinition[] { new DroneReplicatorDefinition(1f) })));
        }

        // Legacy table row 5 and finding 6: the first count runs from deployment, though the hangar was full. A drone
        // restored in the tick launches in the same tick, because the replicator comes first.
        [Test]
        public void TheFirstCountRunsFromDeploymentAndTheRestoredDroneLaunchesAtOnce()
        {
            var driver = DroneBayTests.Start(Carrier(), reserve: false);
            driver.Control(1, systems: BayBit);
            driver.StepUntil(21);
            Lose(driver, DroneBayTests.Drones(driver).Single().Id);

            driver.StepUntil(49);
            Assert.IsEmpty(DroneBayTests.Drones(driver));
            Assert.AreEqual(0, Inventory(driver).InHangar);
            driver.Step();
            Assert.AreEqual(1, DroneBayTests.Drones(driver).Length);
            Assert.AreEqual(1, Inventory(driver).Restored);
            Assert.AreEqual(2, Inventory(driver).Launched);
        }

        // Question 5a: after a restore the timer stops; the next bay refresh with room restarts it.
        [Test]
        public void TheRestartWaitsForTheNextRefresh()
        {
            var driver = DroneBayTests.Start(Carrier(), reserve: false);
            Lose(driver, LaunchOne(driver));
            driver.StepUntil(50);
            Assert.AreEqual(1, Inventory(driver).InHangar);

            driver.Control(1, systems: BayBit);
            driver.Step();
            driver.Control(1, systems: 0);
            var drone = DroneBayTests.Drones(driver).Single().Id;
            driver.StepUntil(60);
            Lose(driver, drone);

            // Lost at tick 60; the refresh at 78 restarts the timer; a plain timer would restore at 110.
            driver.StepUntil(128);
            Assert.AreEqual(0, Inventory(driver).InHangar);
            driver.Step();
            Assert.AreEqual(1, Inventory(driver).InHangar);
        }

        // Question 10b: a loss after the last refresh does not block the restore (legacy counts that drone as live).
        [Test]
        public void ALossSinceTheLastRefreshDoesNotBlockTheRestore()
        {
            var driver = DroneBayTests.Start(Carrier(), reserve: false);
            var drone = LaunchOne(driver);
            driver.StepUntil(30);
            Lose(driver, drone);

            driver.StepUntil(49);
            Assert.AreEqual(0, Inventory(driver).InHangar);
            driver.Step();
            Assert.AreEqual(1, Inventory(driver).InHangar);
        }

        // Legacy TryRestoreDrone spends BuildExtraCycles before each drone. The HUD shows the full cooldown while a cycle
        // is left, then the replicator's share.
        [Test]
        public void ExtraCyclesComeBeforeTheDroneAndFillTheHudCooldown()
        {
            var driver = DroneBayTests.Start(Carrier(buildExtraCycles: 2), reserve: false);
            Lose(driver, LaunchOne(driver));

            driver.StepUntil(50);
            Assert.AreEqual(1, Inventory(driver).BuildCyclesLeft);
            driver.StepUntil(61);
            Assert.AreEqual(1f, GateDeviceTests.Device(driver, 1, Bay).Cooldown);

            driver.StepUntil(103);
            Assert.AreEqual(0, Inventory(driver).BuildCyclesLeft);
            driver.StepUntil(111);
            Assert.AreEqual(43f / 50f, GateDeviceTests.Device(driver, 1, Bay).Cooldown, 1e-6f);

            driver.StepUntil(154);
            Assert.AreEqual(0, Inventory(driver).InHangar);
            driver.Step();
            Assert.AreEqual(1, Inventory(driver).InHangar);
            Assert.AreEqual(2, Inventory(driver).BuildCyclesLeft);
        }

        // Legacy Any(TryRestoreDrone): one restore per count, to the first bay with room in device order.
        [Test]
        public void ARestoreGoesToTheFirstBayWithRoom()
        {
            var driver = DroneBayTests.Start(Carrier(bays: 2), reserve: false);
            driver.Control(1, systems: BayBit | BayBit << 1);
            driver.StepUntil(42);
            driver.Control(1, systems: 0);
            foreach (var drone in DroneBayTests.Drones(driver).Select(s => s.Id).ToArray())
                driver.Battle.QueueDeath(drone);
            driver.Step();
            Assert.IsEmpty(DroneBayTests.Drones(driver));

            // The count ending at 49 restores the first bay only; the refresh at 52 restarts it for the second.
            driver.StepUntil(50);
            Assert.AreEqual(1, Inventory(driver, 1).InHangar);
            Assert.AreEqual(0, Inventory(driver, 2).InHangar);
            driver.StepUntil(102);
            Assert.AreEqual(0, Inventory(driver, 2).InHangar);
            driver.Step();
            Assert.AreEqual(1, Inventory(driver, 2).InHangar);
        }

        // A full bay shows the full cooldown; an empty bay without a replicator too (legacy DroneBay.Cooldown).
        [Test]
        public void AFullBayShowsTheFullCooldown()
        {
            var driver = DroneBayTests.Start(Carrier(), reserve: false);
            LaunchOne(driver);
            driver.Step(DroneBayDevice.LaunchCooldownTicks);
            Assert.AreEqual(1f, GateDeviceTests.Device(driver, 1, Bay).Cooldown);
        }

        // H8: only the owner restores. The copy takes the owner's launch beyond its hangar as a restore.
        [Test]
        public void TheCopyFollowsTheOwnersRestoredLaunch()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(Carrier());
            owner.Control(101, systems: BayBit);
            while (DroneBayTests.Drones(owner).Length == 0) GateDeviceTests.Tick(owner, remote, link);
            var first = DroneBayTests.Drones(owner).Single().Id;
            Assert.AreEqual(first, DroneBayTests.Drones(remote).Single().Id);

            owner.Battle.QueueDeath(first);
            GateDeviceTests.Tick(owner, remote, link);
            Assert.IsEmpty(DroneBayTests.Drones(remote));
            while (DroneBayTests.Drones(owner).Length == 0) GateDeviceTests.Tick(owner, remote, link);

            var second = DroneBayTests.Drones(owner).Single().Id;
            Assert.AreEqual(second, DroneBayTests.Drones(remote).Single().Id);
            var bay = remote.Slot(101).GetBay(Bay);
            Assert.AreEqual(1, bay.Restored);
            Assert.AreEqual(2, bay.Launched);
            Assert.AreEqual(0, bay.InHangar);
        }

        // The copy's replicator does not count: without the owner's launch its hangar stays empty.
        [Test]
        public void TheCopyDoesNotRestoreItself()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(Carrier());
            owner.Control(101, systems: BayBit);
            while (DroneBayTests.Drones(owner).Length == 0) GateDeviceTests.Tick(owner, remote, link);
            owner.Control(101, systems: 0);
            owner.Battle.QueueDeath(DroneBayTests.Drones(owner).Single().Id);
            GateDeviceTests.Tick(owner, remote, link);

            remote.Step(200);
            Assert.AreEqual(0, remote.Slot(101).GetBay(Bay).InHangar);
        }
    }
}
