using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 14 (H10): legacy asteroids as core bodies (TODO/battle_simulator_phase5_part2.md, "Asteroids (14, M4)",
    // examples). Slot 1 waits at (50, 50) facing +x, slot 101 far away at (150, 150), unless a test moves them.
    public class AsteroidTests
    {
        private const float Tolerance = 1e-3f;
        private static readonly Vec2 Shooter = new Vec2(50f, 50f);
        private static readonly Vec2 Away = new Vec2(150f, 150f);

        // Legacy CombatManager: mass size² × 5, hit points size² × 100 × p, ramming multiplier p.
        private static AsteroidSetup Rock(Vec2 position, Vec2 velocity = default, float size = 4f, float p = 1f) =>
            new AsteroidSetup(position, velocity, size, size * size * 5f, size * size * 100f * p, p);

        private static BattleSetup Setup(ShipDefinition ship, IReadOnlyList<AsteroidSetup> asteroids, ulong battleId = 1,
            IReadOnlyCollection<int> localOwners = null) =>
            new BattleSetup(new BattleId(battleId), 7, new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, ship ?? Fixtures.Ship),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
            }, localOwners: localOwners, asteroids: asteroids);

        // Both ships deploy at tick 0; then the ships and asteroids take their test poses.
        private static Driver Start(ShipDefinition ship, params AsteroidSetup[] asteroids)
        {
            var driver = new Driver(Setup(ship, asteroids));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, Away, Vec2.Zero);
            for (var i = 0; i < asteroids.Length; ++i) Place(driver, i, asteroids[i].Position, asteroids[i].Velocity);
            return driver;
        }

        private static Asteroid Asteroid(Driver driver, int index = 0) => driver.Battle.Asteroids.Items[index];

        private static void Place(Driver driver, int index, Vec2 position, Vec2 velocity)
        {
            var body = Asteroid(driver, index).Body;
            driver.Battle.World.SetPose(body, position, 0f);
            driver.Battle.World.SetLinearVelocity(body, velocity);
            driver.Battle.World.SetAngularVelocity(body, 0f);
        }

        private static Vec2 Velocity(Driver driver, int index = 0) =>
            driver.Battle.World.GetLinearVelocity(Asteroid(driver, index).Body);

        private static List<BattleEvent> HitsOn(IEnumerable<BattleEvent> events, EntityId target) =>
            events.Where(e => e.Kind == BattleEventKind.Hit && e.EntityId == target).ToList();

        private static List<BattleEvent> StepUntilHit(Driver driver, EntityId target, int limit = 50)
        {
            var events = new List<BattleEvent>();
            for (var i = 0; i < limit && HitsOn(events, target).Count == 0; ++i) events.AddRange(driver.StepCollecting());
            Assert.IsNotEmpty(HitsOn(events, target), "no hit");
            return events;
        }

        [Test]
        public void TheSetupValidatesItsValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AsteroidSetup(new Vec2(float.NaN, 0f), Vec2.Zero, 1f, 1f, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AsteroidSetup(Vec2.Zero, new Vec2(0f, float.PositiveInfinity), 1f, 1f, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AsteroidSetup(Vec2.Zero, Vec2.Zero, 0f, 1f, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AsteroidSetup(Vec2.Zero, Vec2.Zero, 1f, float.PositiveInfinity, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AsteroidSetup(Vec2.Zero, Vec2.Zero, 1f, 1f, 0f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AsteroidSetup(Vec2.Zero, Vec2.Zero, 1f, 1f, 1f, -1f));
            Assert.DoesNotThrow(() => new AsteroidSetup(Vec2.Zero, Vec2.Zero, 1f, 1f, 1f, 0f));
            Assert.Throws<ArgumentException>(() => Setup(null, new AsteroidSetup[] { null }));
        }

        // Owner question 2 (a): nobody owns an asteroid, so a battle with a remote owner has none.
        [Test]
        public void ABattleWithARemoteOwnerTakesNoAsteroids()
        {
            Assert.Throws<ArgumentException>(() => Setup(null, new[] { Rock(Shooter) }, localOwners: new int[0]));
            Assert.DoesNotThrow(() => Setup(null, new AsteroidSetup[0], localOwners: new int[0]));
            Assert.AreEqual(0, Setup(null, null).Asteroids.Count);
        }

        // Example 1: the snapshot before the first step lists the setup's asteroids, in order, with stable IDs.
        [Test]
        public void TheAsteroidsExistBeforeTheFirstTick()
        {
            var rocks = new[] { Rock(new Vec2(20f, 30f), new Vec2(1f, -2f), 2f), Rock(new Vec2(120f, 80f), size: 5f, p: 11f) };
            var battle = new Battle(Setup(null, rocks));
            var states = battle.Snapshot.Asteroids.ToArray();

            Assert.AreEqual(-1, battle.Snapshot.Tick);
            Assert.AreEqual(2, states.Length);
            Assert.AreEqual(20f, states[0].Position.X, Tolerance);
            Assert.AreEqual(30f, states[0].Position.Y, Tolerance);
            Assert.AreEqual(-2f, states[0].LinearVelocity.Y, Tolerance);
            Assert.AreEqual(2f, states[0].Size);
            Assert.AreEqual(20f, states[0].Mass);
            Assert.AreEqual(400f, states[0].HitPoints);
            Assert.AreEqual(25f * 100f * 11f, states[1].MaxHitPoints, Tolerance);
            Assert.AreEqual(125f, battle.World.GetMass(battle.Asteroids.Items[1].Body), Tolerance);
            Assert.AreNotEqual(states[0].Id, states[1].Id);
            Assert.IsFalse(states[0].Id.IsNull);

            var again = new Battle(Setup(null, rocks)).Snapshot.Asteroids.ToArray();
            Assert.AreEqual(states[0].Id, again[0].Id);
            Assert.AreEqual(states[1].Id, again[1].Id);
            Assert.AreNotEqual(states[0].Id, new Battle(Setup(null, rocks, battleId: 2)).Snapshot.Asteroids[0].Id);
        }

        // Example 4: a bolt deals its damage, stops and pushes the asteroid by its own push (0.5 × 20 = 10).
        [Test]
        public void ABoltDamagesStopsAndPushes()
        {
            var bolt = new BulletDefinition(0.25f, 20f, 20f, 5f, push: 0.5f);
            var driver = Start(Fixtures.CannonShip(bolt, cooldown: 10f), Rock(new Vec2(56f, 50f)));
            var id = Asteroid(driver).Id;
            driver.Control(1, systems: 1);
            var events = StepUntilHit(driver, id);

            var hit = HitsOn(events, id).Single();
            Assert.AreEqual(5f, hit.Damage, Tolerance);
            Assert.AreEqual(BattleEventAuthority.Committed, hit.Authority);
            Assert.AreEqual(1595f, driver.Battle.Snapshot.Asteroids[0].HitPoints, Tolerance);
            Assert.AreEqual(10f / 80f, Velocity(driver).X, Tolerance);
            Assert.AreEqual(RemovalReason.Death, CannonTests.BulletRemovals(events).Single().RemovalReason);
        }

        // Example 5: legacy dies below 0, so exactly 0 hit points lives; the next hit kills it at stage 12.
        [Test]
        public void AnAsteroidDiesBelowZeroHitPoints()
        {
            var driver = Start(Fixtures.CannonShip(cooldown: 0.5f), Rock(new Vec2(56f, 50f)));
            var asteroid = Asteroid(driver);
            var id = asteroid.Id;
            asteroid.HitPoints = 5f;
            driver.Control(1, systems: 1);
            StepUntilHit(driver, id);
            Assert.AreEqual(0f, driver.Battle.Snapshot.Asteroids[0].HitPoints, Tolerance);

            var events = StepUntilHit(driver, id);
            var removal = events.Single(e => e.Kind == BattleEventKind.AsteroidRemoval);
            Assert.AreEqual(id, removal.EntityId);
            Assert.AreEqual(-1, removal.Side);
            Assert.AreEqual(0, driver.Battle.Snapshot.Asteroids.Length);
            Assert.IsNull(driver.Battle.FindAsteroid(id));
            Assert.IsNull(driver.Battle.Asteroids.Items[0]);
            Assert.AreEqual(0, driver.Battle.Asteroids.AliveCount);
        }

        // Examples 6 and 7: 0.1 × 5 × min(80, 200) × the rammer's multiplier, each way, once per touch.
        [TestCase(1f, 40f)]
        [TestCase(11f, 440f)]
        public void AnAsteroidAndAShipRamEachOther(float p, float shipDamage)
        {
            var driver = Start(Fixtures.ShipWith(mass: 200f), Rock(new Vec2(53.2f, 50f), new Vec2(-5f, 0f), p: p));
            var id = Asteroid(driver).Id;
            var events = driver.StepCollecting(5);

            var onShip = HitsOn(events, driver.Entity(1)).Single();
            var onAsteroid = HitsOn(events, id).Single();
            Assert.AreEqual(id, onShip.AttackerId);
            Assert.AreEqual(shipDamage, onShip.Damage, Tolerance);
            Assert.AreEqual(driver.Entity(1), onAsteroid.AttackerId);
            Assert.AreEqual(40f, onAsteroid.Damage, Tolerance);
            Assert.AreEqual(Fixtures.Shield.Max - shipDamage, driver.Ship(1).Shield.Value, Tolerance);
            Assert.AreEqual(1600f * p - 40f, driver.Battle.Snapshot.Asteroids[0].HitPoints, 1e-2f);
        }

        // Example 8: two asteroids are friends (legacy side Undefined); they bounce without damage.
        [Test]
        public void TwoAsteroidsBounceWithoutDamage()
        {
            var driver = Start(null, Rock(new Vec2(80f, 80f), new Vec2(5f, 0f)), Rock(new Vec2(84.2f, 80f), new Vec2(-5f, 0f)));
            var events = driver.StepCollecting(10);

            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.Hit));
            Assert.Less(Velocity(driver, 0).X, 0f);
            Assert.Greater(Velocity(driver, 1).X, 0f);
            Assert.AreEqual(1600f, driver.Battle.Snapshot.Asteroids[0].HitPoints);
            Assert.AreEqual(1600f, driver.Battle.Snapshot.Asteroids[1].HitPoints);
        }

        // Example 10: Power × √mass away from the centre, no damage.
        [Test]
        public void AGravityWavePushesAnAsteroid()
        {
            var generator = new GravityGeneratorDefinition(10f, 1f, 50f, 10f);
            var driver = Start(Fixtures.ShipWith(energy: new ResourceDefinition(100f), devices: new[] { generator }),
                Rock(new Vec2(60f, 50f)));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting();

            Assert.AreEqual(10f * (float)Math.Sqrt(80f) / 80f, Velocity(driver).X, Tolerance);
            Assert.AreEqual(0f, HitsOn(events, Asteroid(driver).Id).Single().Damage);
            Assert.AreEqual(1600f, driver.Battle.Snapshot.Asteroids[0].HitPoints);
        }

        // Example 11: legacy Asteroid.OnCollision reads only the damage, the impulse and Destroy.
        [Test]
        public void NoOtherEffectActsOnAnAsteroid()
        {
            var bolt = new BulletDefinition(0.25f, 20f, 20f, 5f,
                effects: new ImpactEffects(energyDrain: 10f, slowDown: 3f, roll: 90f, teleport: 10f));
            var driver = Start(Fixtures.CannonShip(bolt, cooldown: 10f), Rock(new Vec2(56f, 50f), new Vec2(0f, 1f)));
            driver.Control(1, systems: 1);
            StepUntilHit(driver, Asteroid(driver).Id);

            Assert.AreEqual(0f, Velocity(driver).X, Tolerance);
            Assert.AreEqual(1f, Velocity(driver).Y, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(Asteroid(driver).Body), Tolerance);
            Assert.AreEqual(1595f, driver.Battle.Snapshot.Asteroids[0].HitPoints, Tolerance);
        }

        // Example 12: point defense targets bolts, missiles and drones only.
        [Test]
        public void PointDefenseIgnoresAnAsteroid()
        {
            var driver = Start(Fixtures.ShipWith(devices: new DeviceDefinition[] { new PointDefenseDefinition(10f, 0f, 0f) }),
                Rock(new Vec2(55f, 50f)));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(20);

            Assert.IsEmpty(HitsOn(events, Asteroid(driver).Id));
            Assert.AreEqual(1600f, driver.Battle.Snapshot.Asteroids[0].HitPoints);
        }

        // Example 19: the ray stops at the asteroid and damages it; the ship behind it takes nothing.
        [Test]
        public void ABeamStopsAtAnAsteroid()
        {
            var beam = new BeamDefinition(0, 0f, 0f, 0f, 30f, 50f);
            var ship = Fixtures.ShipWith(barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) }, devices: new[] { beam });
            var driver = Start(ship, Rock(new Vec2(58f, 50f)));
            driver.Place(101, new Vec2(66f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(10);

            var hits = HitsOn(events, Asteroid(driver).Id);
            Assert.IsNotEmpty(hits);
            Assert.IsEmpty(HitsOn(events, driver.Entity(101)));
            Assert.AreEqual(1600f - hits.Count * 50f * Battle.TickDuration, driver.Battle.Snapshot.Asteroids[0].HitPoints,
                Tolerance);
            Assert.AreEqual(Fixtures.Shield.Max, driver.Ship(101).Shield.Value);
        }

        // Legacy StickyController sticks to an asteroid (SpaceObject) too, and its damage over time goes on.
        [Test]
        public void AStickyBulletRidesOnAnAsteroid()
        {
            var sticky = new BulletDefinition(0.75f, 30f, 30f, 1f, hitPoints: 50f, impact: BulletImpact.OverTime,
                detonateWhenDestroyed: false, stickyLifetime: 15f);
            var driver = Start(Fixtures.CannonShip(sticky, cooldown: 100f, energyCost: 0f), Rock(new Vec2(56f, 50f)));
            var id = Asteroid(driver).Id;
            driver.Control(1, systems: 1);
            StepUntilHit(driver, id);
            var events = driver.StepCollecting(10);

            var bullets = driver.Battle.Bullets;
            var index = Enumerable.Range(0, bullets.Capacity).Single(i => bullets.IsAlive(i));
            Assert.IsTrue(bullets[index].Stuck);
            Assert.AreEqual(id, bullets[index].TargetId);
            Assert.AreEqual(10, HitsOn(events, id).Count);
        }
    }
}
