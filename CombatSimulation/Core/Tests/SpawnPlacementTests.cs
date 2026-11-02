using System;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class SpawnPlacementTests
    {
        [Test]
        public void SpawnKeepsDistanceFromEnemiesAndDoesNotOverlap()
        {
            var driver = new Driver(Fixtures.Setup(1, 3, maxActiveShipsPerSide: 3));
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Deploy(103);
            driver.Step();
            driver.Deploy(1);
            driver.Step();

            var world = driver.Battle.World;
            var ship = driver.Slot(1).ActiveShip;
            var position = world.GetPosition(ship.Body);
            foreach (var enemy in new uint[] { 101, 102, 103 })
            {
                var distance = world.Torus.Delta(position, world.GetPosition(driver.Slot(enemy).ActiveShip.Body)).Length;
                Assert.GreaterOrEqual(distance, Battle.SpawnMinEnemyDistance);
            }
            Assert.LessOrEqual(world.GetOverlap(ship.Body, position, world.GetAngle(ship.Body)), 0f);
        }

        [Test]
        public void AlliesNeverOverlapAtSpawn()
        {
            // Legacy ignores allies when it picks a position; the free-position query separates them.
            var driver = new Driver(Fixtures.Setup(8, 1, maxActiveShipsPerSide: 8));
            for (uint id = 1; id <= 8; ++id) driver.Deploy(id);
            driver.Step();

            var world = driver.Battle.World;
            for (uint id = 1; id <= 8; ++id)
            {
                var body = driver.Slot(id).ActiveShip.Body;
                Assert.LessOrEqual(world.GetOverlap(body, world.GetPosition(body), world.GetAngle(body)), 0f);
            }
        }

        [Test]
        public void PlacementIsTheSameInEveryBattleInstance()
        {
            var first = Deploy(7);
            var second = Deploy(7);
            var otherSeed = Deploy(8);

            Assert.AreEqual(first.position.X, second.position.X);
            Assert.AreEqual(first.position.Y, second.position.Y);
            Assert.AreEqual(first.angle, second.angle);
            Assert.IsFalse(first.position.X == otherSeed.position.X && first.position.Y == otherSeed.position.Y);
        }

        [Test]
        public void SpawnRotationIsAWholeDegree()
        {
            var (_, angle) = Deploy(7);
            var degrees = angle * 180.0 / Math.PI;
            if (degrees < 0) degrees += 360;
            Assert.AreEqual(Math.Round(degrees), degrees, 1e-3);
        }

        private static (Vec2 position, float angle) Deploy(ulong matchSeed)
        {
            var driver = new Driver(Fixtures.Setup(matchSeed: matchSeed));
            driver.Deploy(101);
            driver.Step();
            driver.Deploy(1);
            driver.Step();

            var body = driver.Slot(1).ActiveShip.Body;
            return (driver.Battle.World.GetPosition(body), driver.Battle.World.GetAngle(body));
        }
    }
}
