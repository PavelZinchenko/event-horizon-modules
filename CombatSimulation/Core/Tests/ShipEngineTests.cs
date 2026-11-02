using System;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Legacy ShipEngine rules. Only slot 1 deploys, so no other ship can touch it.
    public class ShipEngineTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void OneTickFromRestMatchesTheLegacyFormula()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var forward = Forward(driver, ship);

            driver.Control(1, throttle: 1f);
            driver.Step();

            // Required velocity 10 along the heading; propulsion vector = 10 f / max(5, 10) = f; dv = 20 × 0.02.
            AssertVector(0.4f * forward, Velocity(driver, ship));
        }

        [Test]
        public void ThrottleScalesTheThrustAboveTheThreshold()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var forward = Forward(driver, ship);

            driver.Control(1, throttle: 0.01f);
            driver.Step();
            AssertVector(Vec2.Zero, Velocity(driver, ship), "Legacy thrusts only above throttle 0.01.");

            driver.Control(1, throttle: 0.5f);
            driver.Step();
            AssertVector(0.2f * forward, Velocity(driver, ship));
        }

        [Test]
        public void SpeedApproachesMaxVelocityWithoutOvershoot()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            driver.Control(1, throttle: 1f);

            var max = 0f;
            for (var i = 0; i < 500; ++i)
            {
                driver.Step();
                max = Math.Max(max, Velocity(driver, ship).Length);
            }

            Assert.LessOrEqual(max, Fixtures.Engine.MaxVelocity + Tolerance);
            Assert.AreEqual(Fixtures.Engine.MaxVelocity, Velocity(driver, ship).Length, 0.01f);
        }

        [Test]
        public void ThrustTurnsSidewaysVelocityTowardTheHeading()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var forward = Forward(driver, ship);
            var side = Vec2.LeftPerp(forward);
            driver.Battle.World.SetLinearVelocity(ship.Body, 10f * side);

            driver.Control(1, throttle: 1f);
            driver.Step(500);

            AssertVector(10f * forward, Velocity(driver, ship), tolerance: 0.01f);
        }

        [Test]
        public void TheShipTurnsToItsCourseAndSettles()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var world = driver.Battle.World;
            var course = world.GetAngle(ship.Body) + 90f * Fixtures.Degrees;

            driver.Control(1, course: course);
            var maxSpin = 0f;
            for (var i = 0; i < 150; ++i)
            {
                driver.Step();
                maxSpin = Math.Max(maxSpin, Math.Abs(world.GetAngularVelocity(ship.Body)));
            }

            Assert.AreEqual(0f, ShipEngine.DeltaAngle(world.GetAngle(ship.Body), course), 0.5f * Fixtures.Degrees);
            Assert.AreEqual(0f, world.GetAngularVelocity(ship.Body), 1f * Fixtures.Degrees);
            Assert.LessOrEqual(maxSpin, 1.5f * Fixtures.Engine.MaxAngularVelocity + Tolerance);
        }

        [Test]
        public void TheShipTurnsTheShortWay()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var world = driver.Battle.World;

            driver.Control(1, course: world.GetAngle(ship.Body) - 30f * Fixtures.Degrees);
            driver.Step();
            Assert.Less(world.GetAngularVelocity(ship.Body), 0f);
        }

        [Test]
        public void WithoutACourseTheEngineBrakesTheSpin()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var world = driver.Battle.World;
            world.SetAngularVelocity(ship.Body, 2f);

            driver.Step();
            Assert.AreEqual(2f - Fixtures.Engine.TurnRate * Battle.TickDuration, world.GetAngularVelocity(ship.Body), Tolerance);

            driver.Step(50);
            Assert.AreEqual(0f, world.GetAngularVelocity(ship.Body));
        }

        [Test]
        public void WithoutEnergyAndWithoutAFallbackEngineTheShipNeitherThrustsNorTurns()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var world = driver.Battle.World;
            world.SetAngularVelocity(ship.Body, 2f);
            ship.Energy.Consume(ship.Energy.Max);

            driver.Control(1, throttle: 1f);
            driver.Step();

            AssertVector(Vec2.Zero, Velocity(driver, ship));
            Assert.AreEqual(2f, world.GetAngularVelocity(ship.Body), Tolerance);
        }

        [Test]
        public void WithoutEnergyTheFallbackEngineDrives()
        {
            var fallback = new EngineDefinition(5f, 4f, 0f, 0f);
            var driver = Deployed(Fixtures.ShipWith(engineWithoutEnergy: fallback), out var ship);
            var forward = Forward(driver, ship);
            ship.Energy.Consume(ship.Energy.Max);

            driver.Control(1, throttle: 1f);
            driver.Step();

            // Propulsion vector = 4 f / max(2, 4) = f; dv = 5 × 0.02.
            AssertVector(0.1f * forward, Velocity(driver, ship));
        }

        [Test]
        public void TheEngineSamplesEnergyBeforeTheRecharge()
        {
            var noDelay = new ResourceDefinition(300f, 20f, 0f);
            var driver = Deployed(Fixtures.ShipWith(energy: noDelay), out var ship);
            ship.Energy.Consume(ship.Energy.Max);

            driver.Control(1, throttle: 1f);
            driver.Step();
            Assert.AreEqual(0.4f, ship.Energy.Value, Tolerance, "Stage 2 recharged the energy.");
            AssertVector(Vec2.Zero, Velocity(driver, ship), "The engine saw zero energy at the start of stage 2.");

            driver.Step();
            Assert.Greater(Velocity(driver, ship).Length, 0f);
        }

        [Test]
        public void ARetreatingShipHasItsEngineOffAndDrifts()
        {
            var driver = Deployed(Fixtures.Ship, out var ship);
            var world = driver.Battle.World;
            var drift = new Vec2(1f, 2f);
            world.SetLinearVelocity(ship.Body, drift);
            world.SetAngularVelocity(ship.Body, 1f);

            driver.Retreat(ship.Id);
            driver.Control(1, course: 0f, throttle: 1f);
            driver.Step(10);

            AssertVector(drift, Velocity(driver, ship));
            Assert.AreEqual(1f, world.GetAngularVelocity(ship.Body), Tolerance);
        }

        private static Driver Deployed(ShipDefinition definition, out ShipEntity ship)
        {
            var driver = new Driver(Fixtures.Setup(definition));
            driver.Deploy(1);
            driver.Step();
            ship = driver.Ship(1);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(ship.Body));
            return driver;
        }

        private static Vec2 Forward(Driver driver, ShipEntity ship)
        {
            var rotation = driver.Battle.World.GetRotation(ship.Body);
            return new Vec2(rotation.C, rotation.S);
        }

        private static Vec2 Velocity(Driver driver, ShipEntity ship) => driver.Battle.World.GetLinearVelocity(ship.Body);

        private static void AssertVector(Vec2 expected, Vec2 actual, string message = null, float tolerance = Tolerance)
        {
            Assert.AreEqual(expected.X, actual.X, tolerance, message);
            Assert.AreEqual(expected.Y, actual.Y, tolerance, message);
        }
    }
}
