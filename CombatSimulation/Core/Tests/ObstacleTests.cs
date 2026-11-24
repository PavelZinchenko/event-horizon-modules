using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Frontier's island (TODO/other_modes.md, O8): a static obstacle with legacy StaticPlatform's collisions. The enemy
    // island is a 10 × 10 square at (100, 50). Slot 1 (player) waits at (50, 50), slot 101 (enemy) at (150, 150), unless
    // a test moves them.
    public class ObstacleTests
    {
        private const float Tolerance = 1e-3f;
        private static readonly Vec2 Centre = new Vec2(100f, 50f);

        private static readonly Vec2[] Square =
        {
            new Vec2(-5f, -5f), new Vec2(5f, -5f), new Vec2(5f, 5f), new Vec2(-5f, 5f),
        };

        private static ObstacleSetup Island(Allegiance allegiance = Allegiance.Enemy, long tag = 3) =>
            new ObstacleSetup(Square, Centre, 0f, allegiance, 500f, 1f, tag);

        private static Driver Start(ScriptRules rules, ulong battleId = 1)
        {
            var units = Fixtures.Units(1, 1);
            var driver = new Driver(new BattleSetup(new BattleId(battleId), 7, units, rules: rules));
            foreach (var unit in units) driver.Deploy(unit.UnitId.Value);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        private static EntityId Add(ScriptRules rules, Driver driver, ObstacleSetup obstacle)
        {
            var id = default(EntityId);
            rules.Mode.Next.Add(context => id = context.AddObstacle(obstacle));
            driver.Step();
            Assert.IsFalse(id.IsNull);
            return id;
        }

        private static List<BattleEvent> HitsOn(IEnumerable<BattleEvent> events, EntityId target) =>
            events.Where(e => e.Kind == BattleEventKind.Hit && e.EntityId == target).ToList();

        private static Vec2 Velocity(Driver driver, uint rosterId) =>
            driver.Battle.World.GetLinearVelocity(driver.Ship(rosterId).Body);

        private static Vec2 Position(Driver driver, uint rosterId) =>
            driver.Battle.World.GetPosition(driver.Ship(rosterId).Body);

        [Test]
        public void TheSetupValidatesItsValues()
        {
            Assert.Throws<ArgumentNullException>(() => new ObstacleSetup(null, Centre, 0f, Allegiance.Enemy, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ObstacleSetup(Square, new Vec2(float.NaN, 0f), 0f, Allegiance.Enemy, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ObstacleSetup(Square, Centre, float.PositiveInfinity, Allegiance.Enemy, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ObstacleSetup(Square, Centre, 0f, (Allegiance)9, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ObstacleSetup(Square, Centre, 0f, Allegiance.Enemy, 0f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ObstacleSetup(Square, Centre, 0f, Allegiance.Enemy, 1f, -1f));
            var bowTie = new[] { new Vec2(-5f, -5f), new Vec2(5f, 5f), new Vec2(5f, -5f), new Vec2(-5f, 5f) };
            Assert.Throws<ArgumentException>(() => new ObstacleSetup(bowTie, Centre, 0f, Allegiance.Enemy, 1f, 1f));

            // A concave outline splits into convex parts.
            var notch = new[]
            {
                new Vec2(-5f, -5f), new Vec2(5f, -5f), new Vec2(5f, 5f), new Vec2(0f, 0f), new Vec2(-5f, 5f),
            };
            Assert.Greater(new ObstacleSetup(notch, Centre, 0f, Allegiance.Enemy, 1f, 1f).Parts.Count, 1);
        }

        [Test]
        public void TheSnapshotListsTheObstacleUntilItsRemoval()
        {
            var rules = new ScriptRules();
            var driver = Start(rules);
            var id = Add(rules, driver, new ObstacleSetup(Square, Centre, 0.5f, Allegiance.Enemy, 500f, 1f, 3));

            var state = driver.Battle.Snapshot.Obstacles.ToArray().Single();
            Assert.AreEqual(id, state.Id);
            Assert.AreEqual(3L, state.Tag);
            Assert.AreEqual(Allegiance.Enemy, state.Allegiance);
            Assert.AreEqual(Centre, state.Position);
            Assert.AreEqual(0.5f, state.Angle, 1e-6f);
            CollectionAssert.AreEqual(Square, state.Outline);

            // The same battle ID and number give the same ID.
            var again = new ScriptRules();
            Assert.AreEqual(id, Add(again, Start(again), Island()));
            var other = new ScriptRules();
            Assert.AreNotEqual(id, Add(other, Start(other, 2), Island()));

            var removed = false;
            rules.Mode.Next.Add(context => removed = context.RemoveObstacle(id));
            var events = driver.StepCollecting();
            Assert.IsTrue(removed);
            Assert.AreEqual(0, driver.Battle.Snapshot.Obstacles.Length);
            Assert.IsFalse(events.Any(e => e.EntityId == id), "No event.");

            rules.Mode.Next.Add(context => removed = context.RemoveObstacle(id));
            driver.Step();
            Assert.IsFalse(removed, "It is gone.");

            // The next obstacle takes a new ID.
            Assert.AreNotEqual(id, Add(rules, driver, Island()));
        }

        // Legacy DefaultCollisionBehaviour(1): 0.1 × 20 × min(1, 500) × 1 on the ship, once per touch; the ship deals
        // nothing to the island. The island does not move.
        [Test]
        public void TheIslandBlocksAndRamsAHostileShip()
        {
            var rules = new ScriptRules();
            var driver = Start(rules);
            var id = Add(rules, driver, Island());
            driver.Place(1, new Vec2(93.5f, 50f), new Vec2(20f, 0f));

            var events = driver.StepCollecting(30);

            var hit = HitsOn(events, driver.Entity(1)).Single();
            Assert.AreEqual(id, hit.AttackerId);
            Assert.AreEqual(2f, hit.Damage, Tolerance);
            Assert.IsEmpty(HitsOn(events, id));
            Assert.Less(Position(driver, 1).X, 94.1f, "Blocked.");
            Assert.LessOrEqual(Velocity(driver, 1).X, 0f);
            Assert.AreEqual(Centre, driver.Battle.Snapshot.Obstacles[0].Position);
            Assert.AreEqual(0f, driver.Battle.Snapshot.Obstacles[0].Angle);
            Assert.AreEqual(id, driver.Ship(1).LastBodyContact);
        }

        [Test]
        public void AFriendlyShipFliesThrough()
        {
            var rules = new ScriptRules();
            var driver = Start(rules);
            Add(rules, driver, Island());
            driver.Place(101, new Vec2(93.5f, 50f), new Vec2(20f, 0f));

            var events = driver.StepCollecting(50);

            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.Greater(Position(driver, 101).X, 110f);
        }

        // Legacy layer Platform does not meet the energy layer: the bolt crosses the island and hits the enemy behind it.
        [Test]
        public void ABoltCrossesTheIsland()
        {
            var bolt = new BulletDefinition(0.25f, 40f, 40f, 5f);
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.CannonShip(bolt, cooldown: 10f)),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            };
            var rules = new ScriptRules();
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var id = Add(rules, driver, Island());
            driver.Place(1, new Vec2(85f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(110f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(60);

            Assert.IsEmpty(HitsOn(events, id));
            Assert.AreEqual(5f, HitsOn(events, driver.Entity(101)).Single().Damage, Tolerance);
        }

        // The island rams an asteroid (legacy side Undefined is hostile to it): 0.1 × 5 × min(80, 500) × 1.
        [Test]
        public void AnAsteroidBouncesOffAndTakesTheRam()
        {
            var rules = new ScriptRules();
            var driver = Start(rules);
            var id = Add(rules, driver, Island());
            var rock = default(EntityId);
            rules.Mode.Next.Add(context =>
                rock = context.SpawnAsteroid(new AsteroidSetup(new Vec2(92.9f, 50f), new Vec2(5f, 0f), 4f, 80f, 1600f, 1f)));
            var events = driver.StepCollecting(30);

            var hit = HitsOn(events, rock).Single();
            Assert.AreEqual(id, hit.AttackerId);
            Assert.AreEqual(40f, hit.Damage, Tolerance);
            Assert.IsEmpty(HitsOn(events, id));
            var asteroid = driver.Battle.Snapshot.Asteroids.ToArray().Single();
            Assert.AreEqual(1560f, asteroid.HitPoints, Tolerance);
            Assert.LessOrEqual(asteroid.LinearVelocity.X, 0f);
        }

        // Point defense and the gravity wave ignore the island.
        [Test]
        public void TheQueriesIgnoreTheIsland()
        {
            var devices = new DeviceDefinition[]
            {
                new PointDefenseDefinition(10f, 0f, 0f), new GravityGeneratorDefinition(10f, 1f, 50f, 10f),
            };
            var units = new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, Fixtures.ShipWith(energy: new ResourceDefinition(100f), devices: devices)),
                new UnitSetup(new UnitId(101), 1, Fixtures.Ship),
            };
            var rules = new ScriptRules();
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var id = Add(rules, driver, Island());
            driver.Place(1, new Vec2(92f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Control(1, systems: 3);

            var events = driver.StepCollecting(20);

            Assert.IsEmpty(HitsOn(events, id));
        }

        [Test]
        public void ARemovedIslandLetsTheShipThrough()
        {
            var rules = new ScriptRules();
            var driver = Start(rules);
            var id = Add(rules, driver, Island());
            rules.Mode.Next.Add(context => context.RemoveObstacle(id));
            driver.Step();
            driver.Place(1, new Vec2(93.5f, 50f), new Vec2(20f, 0f));

            var events = driver.StepCollecting(50);

            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.Greater(Position(driver, 1).X, 110f);
        }
    }
}
