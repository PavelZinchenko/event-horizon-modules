using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Q2a and Q2b: the homing missile (legacy HomingController on new ammunition), its target search, its hit points
    // and its contacts with ships, bolts and missiles.
    public class MissileTests
    {
        private const float Tolerance = 1e-4f;

        // Thrust per tick of the fixture missile: 0.5 x 15 x 2.5 x 0.02.
        private const float ThrustPerTick = 0.375f;

        // Turn per tick of the fixture missile: 120 x 2.5 = 300 degrees per second.
        private const float TurnPerTick = 6f * Fixtures.Degrees;

        private static readonly Vec2 Shooter = new Vec2(50f, 50f);

        // From the muzzle at (51, 50) the default enemy (Duel) at (150, 150) is beyond the 39 u search range.
        private const long SpawnTick = 2;

        [Test]
        public void TheFixtureMatchesTheLegacyFormulas()
        {
            var missile = Fixtures.Missile;

            Assert.AreEqual(300f * Fixtures.Degrees, missile.TurnRate, 1e-5f);
            Assert.AreEqual(18.75f, missile.Acceleration, 1e-5f);
            Assert.AreEqual(101, missile.LifetimeTicks);
            Assert.AreEqual(101, HomingSteering.SearchTicks);
            Assert.AreEqual(0.15f, missile.Recoil, 1e-6f, "Weight x speed x starting velocity x parent velocity effect.");
            Assert.IsTrue(missile.IsMissile);
        }

        [Test]
        public void TheMissileStartsAtATenthOfItsSpeedPlusTheShipVelocity()
        {
            var driver = Duel();
            driver.Place(1, Shooter, new Vec2(0f, 4f));
            driver.Control(1, systems: 1);
            driver.StepUntil(SpawnTick + 1);

            var state = driver.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.AreEqual(1.5f, state.LinearVelocity.X, Tolerance);
            Assert.AreEqual(4f, state.LinearVelocity.Y, Tolerance);
        }

        [Test]
        public void WithoutATargetTheMissileAcceleratesStraightUpToItsSpeed()
        {
            var driver = Fire(Duel());

            driver.Step(10);
            var state = Snapshot(driver);
            Assert.AreEqual(1.5f + 10 * ThrustPerTick, state.LinearVelocity.X, Tolerance);
            Assert.AreEqual(0f, state.LinearVelocity.Y, Tolerance);
            Assert.AreEqual(0f, state.Angle, Tolerance);

            driver.Step(40);
            state = Snapshot(driver);
            Assert.GreaterOrEqual(state.LinearVelocity.X, 15f);
            Assert.Less(state.LinearVelocity.X, 15f + ThrustPerTick);

            var speed = state.LinearVelocity.X;
            driver.Step(5);
            Assert.AreEqual(speed, Snapshot(driver).LinearVelocity.X, Tolerance, "No thrust at full forward speed.");
        }

        [Test]
        public void ThrustAlsoRemovesSidewaysVelocity()
        {
            // Legacy: v += normalize(speed x heading - v) x acceleration x dt.
            var driver = Duel();
            driver.Place(1, Shooter, new Vec2(0f, 4f));
            driver.Control(1, systems: 1);
            driver.StepUntil(SpawnTick + 2);

            var change = new Vec2(15f - 1.5f, -4f);
            var expected = new Vec2(1.5f, 4f) + ThrustPerTick / change.Length * change;
            var state = Snapshot(driver);
            Assert.AreEqual(expected.X, state.LinearVelocity.X, Tolerance);
            Assert.AreEqual(expected.Y, state.LinearVelocity.Y, Tolerance);
        }

        [Test]
        public void TheMissileTurnsAtItsFullRateTowardItsTarget()
        {
            // The enemy is left of the heading, outside the cone: the smallest deviation picks it.
            var driver = Duel();
            driver.Place(101, new Vec2(55f, 70f), Vec2.Zero);
            Fire(driver);

            driver.Step();
            Assert.AreEqual(driver.Entity(101), Missile(driver).TargetId);
            Assert.AreEqual(TurnPerTick, Snapshot(driver).Angle, 1e-5f);

            driver.Step();
            Assert.AreEqual(2 * TurnPerTick, Snapshot(driver).Angle, 1e-5f);
        }

        [Test]
        public void TheMissileTurnsRightTowardATargetOnItsRight()
        {
            var driver = Duel();
            driver.Place(101, new Vec2(55f, 30f), Vec2.Zero);
            Fire(driver);

            driver.Step();
            Assert.AreEqual(-TurnPerTick, Snapshot(driver).Angle, 1e-5f);
        }

        [Test]
        public void ATargetWithinFiveDegreesDoesNotTurnTheMissile()
        {
            // From the muzzle at (51, 50) the enemy is 4.5 degrees off the heading.
            var driver = Duel();
            driver.Place(101, new Vec2(70f, 51.5f), Vec2.Zero);
            Fire(driver);

            driver.Step();
            Assert.AreEqual(driver.Entity(101), Missile(driver).TargetId);
            Assert.AreEqual(0f, Snapshot(driver).Angle, 1e-6f);
        }

        [Test]
        public void TheFirstSearchIsInTheTickAfterTheLaunchAndTheNextOne101TicksLater()
        {
            // A missile with a 4 s lifetime: 60 u range at 15 u/s, search radius 78 u.
            var bullet = new BulletDefinition(0.2f, 15f, 60f, 8f, weight: 0.1f, hitPoints: 2f, homing: true,
                startingVelocityMultiplier: 0.1f);
            var driver = Duel(Fixtures.CannonShip(bullet, cooldown: 10f));
            Fire(driver);

            Assert.AreEqual(SpawnTick + 1, Missile(driver).NextSearchTick);
            driver.Step();
            Assert.AreEqual(SpawnTick + 1 + 101, Missile(driver).NextSearchTick);
            Assert.IsTrue(Missile(driver).TargetId.IsNull);

            // Move the enemy within reach, beside the missile's path: the missile sees it only at the next search.
            driver.StepUntil(50);
            var position = Snapshot(driver).Position;
            driver.Place(101, position + new Vec2(0f, 30f), Vec2.Zero);

            driver.StepUntil(SpawnTick + 1 + 101);
            Assert.IsTrue(Missile(driver).TargetId.IsNull);

            driver.Step();
            Assert.AreEqual(driver.Entity(101), Missile(driver).TargetId);
            Assert.AreEqual(SpawnTick + 1 + 2 * 101, Missile(driver).NextSearchTick);
        }

        [Test]
        public void ARemovedTargetStartsASearchAtOnce()
        {
            // Q2b redesign (owner, 2026-10-20). Both enemies are outside the cone; the nearer deviation is 101.
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, MissileShip()),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Deploy(102);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, new Vec2(60f, 70f), Vec2.Zero);
            driver.Place(102, new Vec2(60f, 25f), Vec2.Zero);
            Fire(driver);

            driver.Step();
            Assert.AreEqual(driver.Entity(101), Missile(driver).TargetId);

            // The death applies at stage 12 of the next tick; the missile searches at stage 4 of the tick after it.
            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(SpawnTick + 1 + 101, Missile(driver).NextSearchTick);

            driver.Step();
            Assert.AreEqual(driver.Entity(102), Missile(driver).TargetId);
            Assert.AreEqual(driver.Tick - 1 + 101, Missile(driver).NextSearchTick, "A search restarts the period.");
        }

        [Test]
        public void TheMissileDetonatesOnAShip()
        {
            var driver = Duel();
            driver.Place(101, new Vec2(58f, 50f), Vec2.Zero);
            var events = Fire(driver).StepCollecting(40);

            var hit = events.Single(e => e.Kind == BattleEventKind.Hit);
            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(driver.Entity(101), hit.EntityId);
            Assert.AreEqual(removal.EntityId, hit.AttackerId);
            Assert.AreEqual(8f, hit.Damage);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreEqual(hit.Tick, removal.Tick);
        }

        [Test]
        public void TheMissileExpiresAfterRangeOverSpeed()
        {
            var events = Fire(Duel()).StepCollecting(120);

            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
            Assert.AreEqual(SpawnTick + 101 - 1, removal.Tick);
        }

        [Test]
        public void ALethalBoltDestroysTheMissile()
        {
            var events = BoltAgainstMissile(boltDamage: 5f, out var driver);

            var hits = events.Where(e => e.Kind == BattleEventKind.Hit).ToList();
            var removals = CannonTests.BulletRemovals(events);
            Assert.AreEqual(1, hits.Count, "The missile deals nothing to the bolt.");
            var missile = removals.Single(e => e.BulletDefinition == Fixtures.Missile);
            var bolt = removals.Single(e => e.BulletDefinition != Fixtures.Missile);
            Assert.AreEqual(missile.EntityId, hits[0].EntityId);
            Assert.AreEqual(bolt.EntityId, hits[0].AttackerId);
            Assert.AreEqual(RemovalReason.Death, missile.RemovalReason);
            Assert.AreEqual(RemovalReason.Death, bolt.RemovalReason);
            Assert.AreEqual(missile.Tick, bolt.Tick);
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.Hit && e.EntityId == driver.Entity(101)));
        }

        [Test]
        public void ASubLethalBoltDamagesTheMissileAndStops()
        {
            var events = BoltAgainstMissile(boltDamage: 1f, out var driver);

            var boltHit = events.First(e => e.Kind == BattleEventKind.Hit);
            var removals = CannonTests.BulletRemovals(events);
            var bolt = removals.Single(e => e.BulletDefinition != Fixtures.Missile);
            Assert.AreEqual(bolt.EntityId, boltHit.AttackerId);
            Assert.AreEqual(RemovalReason.Death, bolt.RemovalReason);

            // The missile flies on with 1 hit point and detonates on the enemy ship.
            var missile = removals.Single(e => e.BulletDefinition == Fixtures.Missile);
            Assert.Greater(missile.Tick, bolt.Tick);
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Hit && e.EntityId == driver.Entity(101) &&
                                          e.AttackerId == missile.EntityId));
        }

        [Test]
        public void TwoMissilesThatMeetBothDetonate()
        {
            var driver = CannonTests.Duel(MissileShip(), MissileShip());
            driver.Place(101, new Vec2(70f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(1, systems: 1);
            driver.Control(101, course: (float)Math.PI, systems: 1);
            var events = driver.StepCollecting(60);

            var removals = CannonTests.BulletRemovals(events);
            Assert.AreEqual(2, removals.Count);
            Assert.IsTrue(removals.All(e => e.RemovalReason == RemovalReason.Death));
            Assert.AreEqual(removals[0].Tick, removals[1].Tick);

            var hits = events.Where(e => e.Kind == BattleEventKind.Hit).ToList();
            Assert.AreEqual(2, hits.Count);
            Assert.IsTrue(hits.All(e => removals.Any(r => r.EntityId == e.EntityId)), "Neither ship is hit.");
        }

        [Test]
        public void SteeringSearchingAndHittingAllocateNothingAfterWarmUp()
        {
            // Heavy ships, so the recoil of 300 launches does not push them apart.
            var ship = Fixtures.CannonShip(Fixtures.Missile, cooldown: 0.1f, energyCost: 0f, mass: 100f);
            var driver = CannonTests.Duel(ship, ship);
            driver.Place(101, new Vec2(70f, 60f), Vec2.Zero, (float)Math.PI);
            driver.Control(1, systems: 1);
            driver.Control(101, course: (float)Math.PI, systems: 1);
            driver.Step(300);

            // NUnit asserts allocate, so the loop only counts.
            var hits = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; ++i)
            {
                driver.Battle.Step();
                foreach (var item in driver.Battle.Snapshot.Events)
                    if (item.Kind == BattleEventKind.Hit) ++hits;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Greater(hits, 0);
            Assert.AreEqual(0, allocated);
        }

        private static ShipDefinition MissileShip() => Fixtures.CannonShip(Fixtures.Missile, cooldown: 10f);

        private static Driver Duel(ShipDefinition shooter = null)
        {
            var driver = CannonTests.Duel(shooter ?? MissileShip());
            driver.Place(1, Shooter, Vec2.Zero);
            return driver;
        }

        // Holds the trigger and steps through the launch tick.
        private static Driver Fire(Driver driver)
        {
            driver.Control(1, systems: 1);
            driver.StepUntil(SpawnTick + 1);
            Assert.AreEqual(1, driver.Battle.Bullets.AliveCount, "The missile is launched at tick 2.");
            return driver;
        }

        // The side 1 ship at (70, 50) faces the missile ship and fires one bolt along the missile's path.
        private static List<BattleEvent> BoltAgainstMissile(float boltDamage, out Driver driver)
        {
            var bolt = new BulletDefinition(0.25f, 20f, 30f, boltDamage);
            driver = CannonTests.Duel(MissileShip(), Fixtures.CannonShip(bolt, cooldown: 10f));
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, new Vec2(70f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(1, systems: 1);
            driver.Control(101, course: (float)Math.PI, systems: 1);
            return driver.StepCollecting(100);
        }

        private static BulletState Snapshot(Driver driver) =>
            driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.Definition.IsMissile);

        private static ref BulletEntity Missile(Driver driver)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && bullets[i].Definition.IsMissile)
                    return ref bullets[i];

            throw new AssertionException("No missile is alive.");
        }
    }
}
