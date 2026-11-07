using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 9c: legacy AcceleratorDevice and BrakeDevice, the brake rule of owner question 6 and the accelerator's
    // owner records (H8). Rows: TODO/battle_simulator_phase5.md, "Statuses (9)".
    public class EngineDeviceTests
    {
        private const float Tolerance = 1e-3f;

        // Record 1: Power 4, cooldown 1 s (50 ticks); 5 energy per second, 0.1 per tick.
        private static readonly AcceleratorDefinition Accelerator = new AcceleratorDefinition(5f, 1f, 4f);

        // Record 2 on a ship of weight 1: Power 5 / sqrt(1).
        private static readonly BrakeDefinition Brake = new BrakeDefinition(5f);

        // Fixtures.Engine (20 u/s², 10 u/s) with the legacy velocity limit 25 instead of 40, so Power 4 reaches it.
        private static readonly EngineDefinition Engine =
            new EngineDefinition(20f, 10f, 300f * Fixtures.Degrees, 120f * Fixtures.Degrees, 25f);

        // Energy without recharge.
        private static ShipDefinition EngineShip(float energy = 100f, params DeviceDefinition[] devices) =>
            Fixtures.ShipWith(engine: Engine, energy: new ResourceDefinition(energy),
                devices: devices.Length > 0 ? devices : new DeviceDefinition[] { Accelerator, Brake });

        private static DeviceState Device(Driver driver, uint rosterId, int index) =>
            GateDeviceTests.Device(driver, rosterId, index);

        private static ShipStatusFlags Status(Driver driver, uint rosterId) => driver.Ship(rosterId).Status;

        private static Vec2 Velocity(Driver driver, uint rosterId) =>
            driver.Battle.World.GetLinearVelocity(driver.Ship(rosterId).Body);

        // Row Accelerator, effect: throttle 1 without thrust input, and the speed rises to min(10 x 4, 25).
        [Test]
        public void AHeldAcceleratorFliesAtFullThrottleUpToTheVelocityLimit()
        {
            var driver = CannonTests.Duel(EngineShip());
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(ShipStatusFlags.FullThrottle, Status(driver, 1));
            Assert.IsTrue(Device(driver, 1, 0).Active);
            Assert.AreEqual(99.9f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(80f * Battle.TickDuration, Velocity(driver, 1).X, Tolerance, "Propulsion 20 x 4, throttle 1.");

            driver.Step(249);
            Assert.AreEqual(75f, driver.Ship(1).Energy.Value, 0.01f);
            Assert.AreEqual(25f, Velocity(driver, 1).X, 0.01f);
            Assert.AreEqual(0f, Velocity(driver, 1).Y, Tolerance);
        }

        // Without a limit the scaled velocity is 10 x 4.
        [Test]
        public void TheAcceleratorScalesTheVelocityByItsPower()
        {
            var driver = CannonTests.Duel(Fixtures.ShipWith(energy: new ResourceDefinition(100f),
                devices: new DeviceDefinition[] { Accelerator }));
            driver.Control(1, systems: 1);
            driver.Step(400);

            Assert.AreEqual(40f, Velocity(driver, 1).X, 0.01f);
        }

        // Row Accelerator, ends: the release ends it at once; the cooldown counts from the release tick.
        [Test]
        public void TheReleaseEndsTheAcceleratorAndStartsTheCooldown()
        {
            var driver = CannonTests.Duel(EngineShip());
            driver.Control(1, systems: 1);
            driver.Step(10);
            var release = driver.Tick;
            driver.Control(1, systems: 0);
            driver.Step();

            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.IsFalse(Device(driver, 1, 0).Active);
            Assert.AreEqual(99f, driver.Ship(1).Energy.Value, Tolerance, "Ten ticks paid.");

            driver.Control(1, systems: 1);
            driver.StepUntil(release + 50);
            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.IsFalse(Device(driver, 1, 0).Active, "Legacy Active needs the cooldown.");
            Assert.AreEqual(99f, driver.Ship(1).Energy.Value, Tolerance);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.FullThrottle, Status(driver, 1), "Ready at release + 50.");
        }

        // Row Accelerator, ends: energy out. Legacy Active stays true while the bit is held.
        [Test]
        public void TheAcceleratorEndsWhenTheEnergyRunsOut()
        {
            var driver = CannonTests.Duel(EngineShip(energy: 0.25f));
            driver.Control(1, systems: 1);
            driver.Step(2);
            Assert.AreEqual(ShipStatusFlags.FullThrottle, Status(driver, 1));

            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.AreEqual(0.05f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.IsTrue(Device(driver, 1, 0).Active);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
        }

        // Legacy modifications in turn: two accelerators multiply.
        [Test]
        public void TwoAcceleratorsMultiplyTheirPower()
        {
            var twice = new AcceleratorDefinition(5f, 1f, 2f);
            var driver = CannonTests.Duel(Fixtures.ShipWith(energy: new ResourceDefinition(100f),
                devices: new DeviceDefinition[] { twice, twice }));
            driver.Control(1, systems: 3);
            driver.Step();

            Assert.AreEqual(80f * Battle.TickDuration, Velocity(driver, 1).X, Tolerance);
        }

        // Step 9b, finding 3: the accelerator is no longer inert, so it ends stealth.
        [Test]
        public void AnActiveAcceleratorEndsStealth()
        {
            var stealth = new StealthDefinition(5f, 5f, endsOnContact: true);
            var driver = CannonTests.Duel(EngineShip(devices: new DeviceDefinition[] { stealth, Accelerator }));
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.Stealth, Status(driver, 1));

            driver.Control(1, systems: 2);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.FullThrottle, Status(driver, 1));
        }

        // A retreat blocks the activation: the use ends as at a release.
        [Test]
        public void ARetreatEndsTheAccelerator()
        {
            var driver = CannonTests.Duel(EngineShip());
            driver.Control(1, systems: 1);
            driver.Step(3);
            Assert.IsTrue(driver.Retreat(driver.Entity(1)));
            driver.Step();

            Assert.AreEqual(ShipStatusFlags.None, Status(driver, 1));
            Assert.IsFalse(Device(driver, 1, 0).Active);
        }

        // Row Brake: deceleration 5 against the velocity at throttle 0.
        [Test]
        public void TheBrakeSlowsAShipWithoutThrottle()
        {
            var driver = CannonTests.Duel(EngineShip(devices: new DeviceDefinition[] { Brake }));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(3f, 4f));
            driver.Step(50);

            Assert.AreEqual(0f, Velocity(driver, 1).Length, Tolerance, "5 u/s lost in 1 s.");
            driver.Place(1, new Vec2(50f, 50f), new Vec2(3f, 4f));
            driver.Step(25);
            Assert.AreEqual(1.5f, Velocity(driver, 1).X, Tolerance);
            Assert.AreEqual(2f, Velocity(driver, 1).Y, Tolerance);
        }

        // Owner question 6 (b): the brake stops at zero speed and never reverses the velocity.
        [Test]
        public void TheBrakeStopsTheShipWithoutJitter()
        {
            var driver = CannonTests.Duel(EngineShip(devices: new DeviceDefinition[] { Brake }));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(0.05f, 0f));
            driver.Step();
            Assert.AreEqual(Vec2.Zero, Velocity(driver, 1));

            driver.Step(3);
            Assert.AreEqual(Vec2.Zero, Velocity(driver, 1));
        }

        // Legacy adds the deceleration of each brake.
        [Test]
        public void TwoBrakesAdd()
        {
            var driver = CannonTests.Duel(EngineShip(devices: new DeviceDefinition[] { Brake, Brake }));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(20f, 0f));
            driver.Step(50);

            Assert.AreEqual(10f, Velocity(driver, 1).X, Tolerance);
        }

        // Legacy BrakeDevice acts at a throttle of 0.001 or less, and the accelerator's throttle 1 stops it.
        [Test]
        public void TheBrakeDoesNotActWithThrottle()
        {
            var driver = CannonTests.Duel(EngineShip(devices: new DeviceDefinition[] { Brake }));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(20f, 0f));
            driver.Control(1, throttle: 0.005f);
            driver.Step(10);
            Assert.AreEqual(20f, Velocity(driver, 1).X, Tolerance, "No thrust under 0.01, no brake over 0.001.");

            var accelerated = CannonTests.Duel(EngineShip());
            accelerated.Place(1, new Vec2(50f, 50f), new Vec2(30f, 0f));
            accelerated.Control(1, systems: 1);
            accelerated.Step();
            Assert.Greater(Velocity(accelerated, 1).X, 30f - 5f * Battle.TickDuration + Tolerance);
        }

        // Legacy WarpDrive sets Deceleration 0: a warp keeps its speed with a brake.
        [Test]
        public void AWarpIgnoresTheBrake()
        {
            var warp = new WarpDriveDefinition(10f, 3f, 100f, 44f);
            var driver = CannonTests.Duel(EngineShip(devices: new DeviceDefinition[] { warp, Brake }));
            driver.Control(1, systems: 1);
            driver.Step(10);

            Assert.AreEqual(ShipStatusFlags.NoContacts | ShipStatusFlags.EngineOff | ShipStatusFlags.Faded, Status(driver, 1));
            Assert.AreEqual(100f, Velocity(driver, 1).Length, Tolerance);
        }

        // H8: the owner records the start and the end; the copy has the same status and velocity in the same ticks.
        [Test]
        public void TheCopyFollowsTheOwnersAcceleratorRecords()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(EngineShip());
            var start = owner.Tick;
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1, tick: start);
            GateDeviceTests.Tick(owner, remote, link);
            var use = GateDeviceTests.Uses(owner).Single();
            Assert.IsTrue(use.UseStarted);
            Assert.AreEqual(start, use.Tick);
            Assert.AreEqual(ShipStatusFlags.FullThrottle, Status(remote, 101));

            for (var i = 0; i < 5; ++i) GateDeviceTests.Tick(owner, remote, link);
            owner.Control(101, systems: 0, tick: owner.Tick);
            remote.Control(101, systems: 0, tick: owner.Tick);
            GateDeviceTests.Tick(owner, remote, link);
            var end = GateDeviceTests.Uses(owner).Single();
            Assert.IsFalse(end.UseStarted);
            Assert.AreEqual(start + 6, end.Tick);

            Assert.AreEqual(ShipStatusFlags.None, Status(remote, 101));
            Assert.AreEqual(Velocity(owner, 101).X, Velocity(remote, 101).X, Tolerance);
            Assert.AreEqual(owner.Ship(101).Energy.Value, remote.Ship(101).Energy.Value, Tolerance);
        }

        // A late end record ends the copy's use at once; until then it keeps full throttle, and its failed payments end
        // nothing.
        [Test]
        public void ALateEndRecordEndsTheCopyAtOnce()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(EngineShip(energy: 0.25f));
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1, tick: owner.Tick);
            GateDeviceTests.Tick(owner, remote, link);
            for (var i = 0; i < 4; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            Assert.AreEqual(ShipStatusFlags.None, Status(owner, 101));
            Assert.AreEqual(ShipStatusFlags.FullThrottle, Status(remote, 101), "The copy waits for the record.");

            link.Deliver(remote);
            remote.Step();
            Assert.AreEqual(ShipStatusFlags.None, Status(remote, 101));
        }

        [Test]
        public void TheCopyDoesNotStartTheAcceleratorFromItsOwnControls()
        {
            var (_, remote, _) = GateDeviceTests.Pair(EngineShip());
            remote.Control(101, systems: 1);
            remote.Step(3);

            Assert.AreEqual(ShipStatusFlags.None, Status(remote, 101));
            Assert.IsEmpty(GateDeviceTests.Uses(remote));
        }

        // The brake needs no record: both cores read the throttle of the same control frames.
        [Test]
        public void TheCopyBrakesAsTheOwner()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(EngineShip(devices: new DeviceDefinition[] { Brake }));
            foreach (var driver in new[] { owner, remote })
                driver.Place(101, new Vec2(50f, 50f), new Vec2(10f, 0f));
            for (var i = 0; i < 20; ++i) GateDeviceTests.Tick(owner, remote, link);

            Assert.AreEqual(8f, Velocity(owner, 101).X, Tolerance);
            Assert.AreEqual(8f, Velocity(remote, 101).X, Tolerance);
        }
    }
}
