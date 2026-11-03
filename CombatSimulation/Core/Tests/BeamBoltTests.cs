using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 4e: beam bolts (legacy MovingBeamController and RayCastCollider), rows of "Moving beam bolts (4e)".
    // The duel's shooter sits at (50, 50) facing +x; its muzzle is at (51, 50) and its first shot is at tick 2.
    public class BeamBoltTests
    {
        private const float Tolerance = 1e-3f;
        private const long FirstShot = 2;

        // IonCannon_M_1 (ammunition 136): speed 100, range 40, length 6. Damage 10 for the tests. Growth 3 ticks;
        // lifetime 0.4 s (21 ticks) after it.
        private static readonly BulletDefinition Ion = new BulletDefinition(0.25f, 100f, 40f, 10f, beamLength: 6f);

        // HeavyPlasmaEmitter (ammunition 124): a piercing laser that hits all targets; damage 10 for the tests.
        private static readonly BulletDefinition Plasma = new BulletDefinition(0.25f, 100f, 50f, 10f,
            impact: BulletImpact.AllTargets, beamLength: 10f, piercing: true);

        [Test]
        public void BeamBoltDefinitionsCheckTheirParameters()
        {
            Assert.IsTrue(Ion.IsBeamBolt);
            Assert.AreEqual(3, Ion.GrowthTicks(1f));
            Assert.AreEqual(21 + 3, Ion.LifetimeTicks);
            Assert.AreEqual(0, Fixtures.Bolt.GrowthTicks(1f));

            Assert.Throws<ArgumentOutOfRangeException>(() => new BulletDefinition(0.25f, 100f, 40f, 10f, beamLength: -1f));
            Assert.Throws<ArgumentException>(() => new BulletDefinition(0.25f, 0f, 0f, 10f, lifetime: 1f, beamLength: 6f));
            Assert.Throws<ArgumentException>(() => new BulletDefinition(0.25f, 100f, 40f, 10f, homing: true, beamLength: 6f));
            Assert.Throws<ArgumentException>(() => new BulletDefinition(0.25f, 100f, 40f, 10f, hitPoints: 1f, beamLength: 6f));
            Assert.Throws<ArgumentException>(() =>
                new BulletDefinition(0.25f, 100f, 40f, 10f, attachedToParent: true, beamLength: 6f));
            Assert.Throws<ArgumentException>(() => new BulletDefinition(0.25f, 100f, 40f, 10f, piercing: true));
        }

        // Rows 1-3: no ray in the spawn tick; the ray grows by 2 u per tick to 6 u while the bolt keeps the ship's
        // velocity; then the bolt flies at 100 u/s; its lifetime starts after the growth, and its ray shrinks in the
        // last ticks.
        [Test]
        public void ABoltGrowsToItsLengthThenFliesAndShrinksAtTheEnd()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(Ion, cooldown: 10f, energyCost: 0f));
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot);

            var lengths = new List<float>();
            var speeds = new List<float>();
            List<BattleEvent> removals = null;
            for (var tick = FirstShot; tick <= FirstShot + 24; ++tick)
            {
                var events = driver.StepCollecting();
                var bullets = driver.Battle.Snapshot.Bullets.ToArray();
                if (bullets.Length == 0)
                {
                    removals = CannonTests.BulletRemovals(events);
                    break;
                }
                var bolt = bullets.Single();
                Assert.IsTrue(bolt.HasRay);
                lengths.Add(Length(driver, bolt));
                speeds.Add(bolt.LinearVelocity.X);
            }

            var expected = new List<float> { 0f, 2f, 4f };
            expected.AddRange(Enumerable.Repeat(6f, 19)); // ticks 5-23
            expected.Add(4f);
            Assert.That(lengths, Is.EqualTo(expected).Within(Tolerance));
            Assert.That(speeds.Take(3), Is.All.EqualTo(0f));
            Assert.That(speeds.Skip(3), Is.All.EqualTo(100f).Within(Tolerance));

            // Spawn tick 2 + 3 growth ticks + 21 lifetime ticks - 1.
            Assert.AreEqual(FirstShot + 24 - 1, removals.Single().Tick);
            Assert.AreEqual(RemovalReason.Expiry, removals.Single().RemovalReason);
        }

        // Row 1: the bolt starts with the ship's velocity share (ParentVelocityEffect), not with its own speed.
        [Test]
        public void ABoltStartsWithTheShipVelocity()
        {
            var bolt = new BulletDefinition(0.25f, 100f, 40f, 10f, parentVelocityEffect: 0.5f, beamLength: 6f);
            var driver = CannonTests.Duel(Fixtures.CannonShip(bolt, cooldown: 10f, energyCost: 0f));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(0f, 8f));
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot + 1);

            var spawn = driver.Battle.Snapshot.Bullets.ToArray().Single().LinearVelocity;
            Assert.AreEqual(0f, spawn.X, Tolerance);
            Assert.Greater(spawn.Y, 3f);
            driver.StepUntil(FirstShot + 4);
            var flight = driver.Battle.Snapshot.Bullets.ToArray().Single().LinearVelocity;
            Assert.AreEqual(100f, flight.X, Tolerance);
            Assert.AreEqual(spawn.Y, flight.Y, Tolerance);
        }

        // Row 4: the first body along the ray takes the hit once, and the bolt dies. Its ray ends at the hit.
        [Test]
        public void ABoltHitsTheFirstBodyAlongItsRayAndDies()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(Ion, cooldown: 10f, energyCost: 0f), Target);
            // Hull surface at x = 60.5: the ray [55, 61] of tick 7 reaches it.
            driver.Place(101, new Vec2(61.5f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(8); // ticks 1-8
            var hit = events.Single(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(7, hit.Tick);
            Assert.AreEqual(driver.Entity(101), hit.EntityId);
            Assert.AreEqual(60.5f, hit.Point.X, Tolerance);
            Assert.AreEqual(990f, driver.Ship(101).Armor.Value, Tolerance);
            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(7, removal.Tick);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
        }

        // An OverTime bolt without pass-through burns the nearer body every tick it touches it, and its ray ends there.
        [Test]
        public void AnOverTimeBoltWithoutPassThroughBurnsOnlyTheNearerBody()
        {
            var bolt = new BulletDefinition(0.25f, 100f, 40f, 50f, impact: BulletImpact.OverTime, beamLength: 6f);
            var driver = TwoTargets(bolt);
            driver.Control(1, systems: 1);

            var hits = new List<BattleEvent>();
            var ends = new List<float>();
            for (var tick = 1; tick <= 9; ++tick)
            {
                hits.AddRange(driver.StepCollecting().Where(e => e.Kind == BattleEventKind.Hit));
                var state = driver.Battle.Snapshot.Bullets.ToArray();
                if (state.Length == 1) ends.Add(state[0].BeamEndPoint.X);
            }

            // Rays of ticks 5-8 from x = 51, 53, 55, 57 meet the near body (56.5-58.5); at tick 9 the far one.
            CollectionAssert.AreEqual(new long[] { 5, 6, 7, 8, 9 }, hits.Select(e => e.Tick).ToArray());
            Assert.IsTrue(hits.Take(4).All(e => e.EntityId == driver.Entity(101)));
            Assert.AreEqual(driver.Entity(102), hits[4].EntityId);
            Assert.AreEqual(1000f - 4f, driver.Ship(101).Armor.Value, Tolerance);
            // After its step the bolt sits at 53, 55, 57, 59: the drawn ray ends at the near surface while it is ahead.
            Assert.That(ends.Skip(3).Take(3), Is.EqualTo(new[] { 56.5f, 56.5f, 57f }).Within(Tolerance));
        }

        // Row 4 with two bodies on the ray: only the nearer one takes the hit.
        [Test]
        public void ABoltWithoutPassThroughStopsAtTheNearerBody()
        {
            var driver = TwoTargets(Ion);
            driver.Control(1, systems: 1);
            driver.StepCollecting(20);

            Assert.AreEqual(990f, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(1000f, driver.Ship(102).Armor.Value, Tolerance);
        }

        // Row 5: a piercing bolt hits each body once, when the body enters its ray, and flies on.
        [Test]
        public void APiercingBoltHitsEveryBodyOnceAndFliesOn()
        {
            var driver = TwoTargets(Plasma);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(12); // ticks 1-12
            var hits = events.Where(e => e.Kind == BattleEventKind.Hit).ToList();
            // Growth 5 ticks: the ray [51, 57] of tick 5 meets the near body; the bolt flies from tick 7, and its ray
            // [53, 63] of tick 8 meets the far one.
            CollectionAssert.AreEqual(new[] { driver.Entity(101), driver.Entity(102) }, hits.Select(e => e.EntityId).ToArray());
            CollectionAssert.AreEqual(new long[] { 5, 8 }, hits.Select(e => e.Tick).ToArray());
            Assert.AreEqual(990f, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(990f, driver.Ship(102).Armor.Value, Tolerance);
            Assert.IsEmpty(CannonTests.BulletRemovals(events));
        }

        // Row 6: a length above the range stops the ray at the range; the lifetime still waits for the full length.
        [Test]
        public void ARayLongerThanTheRangeStopsGrowingAtTheRange()
        {
            // Speed 100, range 8, length 10: growth 5 ticks (8 u from the fourth), lifetime 0.08 s (5 ticks) after it.
            var bolt = new BulletDefinition(0.25f, 100f, 8f, 10f, beamLength: 10f, piercing: true);
            Assert.AreEqual(10, bolt.LifetimeTicks);
            var driver = CannonTests.Duel(Fixtures.CannonShip(bolt, cooldown: 10f, energyCost: 0f));
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot);

            var lengths = new List<float>();
            var speeds = new List<float>();
            for (var i = 0; i < 9; ++i)
            {
                driver.Step();
                var state = driver.Battle.Snapshot.Bullets.ToArray().Single();
                lengths.Add(Length(driver, state));
                speeds.Add(state.LinearVelocity.X);
            }
            Assert.That(lengths, Is.EqualTo(new[] { 0f, 2f, 4f, 6f, 8f, 8f, 8f, 6f, 4f }).Within(Tolerance));
            Assert.That(speeds, Is.EqualTo(new[] { 0f, 0f, 0f, 0f, 0f, 100f, 100f, 100f, 100f }).Within(Tolerance));
            driver.Step();
            Assert.IsEmpty(driver.Battle.Snapshot.Bullets.ToArray());
        }

        // Legacy: the bolt prefab has no physics collider, so point defense never finds it.
        [Test]
        public void PointDefenseDoesNotSeeABolt()
        {
            var defense = Fixtures.ShipWith(shield: new ResourceDefinition(0f),
                devices: new DeviceDefinition[] { new PointDefenseDefinition(8f, 0f, 1f) });
            var driver = CannonTests.Duel(Fixtures.CannonShip(Ion, cooldown: 10f, energyCost: 0f), defense);
            driver.Place(101, new Vec2(61.5f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(101, systems: 1);

            var events = driver.StepCollecting(8);
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.Disarm));
            Assert.AreEqual(990f, driver.Ship(101).Armor.Value, Tolerance);
        }

        // A remote copy that spawns three ticks late launches at the owner's tick and flies on the owner's line.
        [Test]
        public void ALateCopyLaunchesWithItsOwner()
        {
            var p = new NetPair(Fixtures.CannonShip(Ion, cooldown: 10f, energyCost: 0f), delayB: 3);
            p.B.Control(1, systems: 1);
            for (var i = 0; i < 12; ++i)
            {
                p.Step();
                var owner = p.B.Battle.Snapshot.Bullets.ToArray();
                var copy = p.A.Battle.Snapshot.Bullets.ToArray();
                if (owner.Length == 0 || copy.Length == 0) continue;
                Assert.AreEqual(owner.Single().Position.X, copy.Single().Position.X, Tolerance);
                Assert.AreEqual(owner.Single().LinearVelocity.X, copy.Single().LinearVelocity.X, Tolerance);
                Assert.AreEqual(owner.Single().BeamEndPoint.X, copy.Single().BeamEndPoint.X, Tolerance);
            }
            Assert.AreEqual(100f, p.A.Battle.Snapshot.Bullets.ToArray().Single().LinearVelocity.X, Tolerance);
        }

        // Armor only: hits show in the armor.
        private static readonly ShipDefinition Target = Fixtures.ShipWith(shield: new ResourceDefinition(0f));

        private static float Length(Driver driver, in BulletState bolt) =>
            driver.Battle.World.Torus.Delta(bolt.Position, bolt.BeamEndPoint).Length;

        // The shooter and two enemies on its line: hull surfaces at x = 56.5-58.5 and 61.5-63.5.
        private static Driver TwoTargets(BulletDefinition bolt)
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(bolt, cooldown: 10f, energyCost: 0f)),
                new RosterSlotSetup(new RosterId(101), 1, Target),
                new RosterSlotSetup(new RosterId(102), 1, Target),
            }, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(57.5f, 50f), Vec2.Zero);
            driver.Place(102, new Vec2(62.5f, 50f), Vec2.Zero);
            return driver;
        }
    }
}
