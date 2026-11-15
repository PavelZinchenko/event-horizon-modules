using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // The bullet store: hits through the contact pipeline, expiry, filters, identity, events and the snapshot.
    public class BulletTests
    {
        private const float Tolerance = 1e-3f;

        private static readonly Vec2 Shooter = new Vec2(50f, 50f);

        [Test]
        public void LifetimeIsRangeOverSpeedAndExpiryIsStrict()
        {
            // 10 / 20 = 0.5 s = 25 ticks; legacy expires when the age exceeds it, so the bullet flies 26 steps.
            Assert.AreEqual(26, Fixtures.Bolt.LifetimeTicks);
            Assert.AreEqual(1, new BulletDefinition(0.1f, 1000f, 1f, 0f).LifetimeTicks);
        }

        [Test]
        public void ABulletExpiresAfterItsLastStep()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 10f));
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(40);
            var spawn = CannonTests.BulletSpawns(events).Single();
            var removal = CannonTests.BulletRemovals(events).Single();

            Assert.AreEqual(spawn.EntityId, removal.EntityId);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
            Assert.AreEqual(spawn.Tick + Fixtures.Bolt.LifetimeTicks - 1, removal.Tick);
            Assert.AreEqual(driver.Entity(1), removal.ParentId);
            Assert.AreSame(Fixtures.Bolt, removal.BulletDefinition);
            Assert.AreEqual(0, driver.Battle.Bullets.AliveCount);
            Assert.AreEqual(0, driver.Battle.World.BulletCount);
        }

        [Test]
        public void ABulletFliesItsRangeBeforeItExpires()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 10f));
            driver.Control(1, systems: 1);

            var last = default(BulletState);
            for (var i = 0; i < 40; ++i)
            {
                driver.Step();
                if (driver.Battle.Snapshot.Bullets.Length > 0) last = driver.Battle.Snapshot.Bullets[0];
            }

            // Muzzle at 51; 25 published steps of 0.4 u. The physics of the expiry tick adds one more step.
            Assert.AreEqual(Shooter.X + 1f + 10f, last.Position.X, Tolerance);
        }

        [Test]
        public void AHitStopsTheBulletAndDamagesTheShieldFirst()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 10f));
            driver.Place(101, new Vec2(56f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(20);
            var spawn = CannonTests.BulletSpawns(events).Single();
            var hit = RammingTests.Hits(events).Single();
            var removal = CannonTests.BulletRemovals(events).Single();

            Assert.AreEqual(spawn.EntityId, hit.AttackerId);
            Assert.AreEqual(driver.Entity(101), hit.EntityId);
            Assert.AreEqual(5f, hit.Damage);
            Assert.AreEqual(55f, hit.Point.X, Tolerance, "The point is on the target hull.");

            Assert.AreEqual(hit.Tick, removal.Tick);
            Assert.Greater(removal.Sequence, hit.Sequence);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);

            var target = driver.Ship(101);
            Assert.AreEqual(Fixtures.Shield.Max - 5f, target.Shield.Value, Tolerance);
            Assert.AreEqual(1000f, target.Armor.Value);
        }

        [Test]
        public void ABulletPassesItsAlliesAndHitsTheEnemyBehind()
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(cooldown: 10f)),
                new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(2);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(2, new Vec2(54f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var hits = RammingTests.Hits(driver.StepCollecting(30));

            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(driver.Entity(101), hits[0].EntityId);
        }

        [Test]
        public void OpposingBulletsPassThroughEachOther()
        {
            var cannon = Fixtures.CannonShip(cooldown: 10f);
            var driver = CannonTests.Duel(cannon, cannon);
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(1, systems: 1);
            driver.Control(101, course: (float)Math.PI, systems: 1);

            var events = driver.StepCollecting(30);
            var hits = RammingTests.Hits(events);

            Assert.AreEqual(2, CannonTests.BulletSpawns(events).Count);
            Assert.AreEqual(2, hits.Count);
            CollectionAssert.AreEquivalent(new[] { driver.Entity(1), driver.Entity(101) }, hits.Select(h => h.EntityId));
        }

        [Test]
        public void TwoBulletsHitOneShipInTheSameStep()
        {
            var ship = Fixtures.ShipWith(
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0.3f)), new BarrelDefinition(new Vec2(1f, -0.3f)) },
                devices: new[]
                {
                    new CannonDefinition(0, 10f, 2f, 0f, Fixtures.Bolt),
                    new CannonDefinition(1, 10f, 2f, 0f, Fixtures.Bolt),
                });
            var driver = CannonTests.Duel(ship);
            driver.Place(101, new Vec2(56f, 50f), Vec2.Zero);
            driver.Control(1, systems: 3);

            var events = driver.StepCollecting(20);
            var hits = RammingTests.Hits(events);

            Assert.AreEqual(2, hits.Count);
            Assert.AreEqual(hits[0].Tick, hits[1].Tick);
            Assert.AreEqual(2, CannonTests.BulletRemovals(events).Count(e => e.RemovalReason == RemovalReason.Death));
            Assert.AreEqual(Fixtures.Shield.Max - 10f, driver.Ship(101).Shield.Value, Tolerance);
        }

        [Test]
        public void ABulletThatStartsInsideTheTargetHitsInItsSpawnTick()
        {
            // The muzzle at 51.5 is inside the enemy hull (51.2 to 53.2); the hulls do not touch.
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 10f, muzzle: new Vec2(1.5f, 0f)));
            driver.Place(101, new Vec2(52.2f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(10);
            var spawn = CannonTests.BulletSpawns(events).Single();
            var hit = RammingTests.Hits(events).Single();

            Assert.AreEqual(spawn.Tick, hit.Tick);
            Assert.AreEqual(spawn.EntityId, hit.AttackerId);
        }

        [Test]
        public void BulletIdsDoNotDependOnAllocationHistory()
        {
            // In the second battle the enemy fires first, so the shooter's bullet gets another storage index.
            var quiet = CannonTests.Duel(Fixtures.CannonShip(cooldown: 10f));
            var busy = CannonTests.Duel(Fixtures.CannonShip(cooldown: 10f), Fixtures.CannonShip(cooldown: 0f));
            busy.Control(101, systems: 1);
            busy.Step(5);

            quiet.StepUntil(6);
            quiet.Control(1, systems: 1);
            busy.Control(1, systems: 1);
            var first = CannonTests.BulletSpawns(quiet.StepCollecting(5)).Single();
            var second = CannonTests.BulletSpawns(busy.StepCollecting(5)).Single(e => e.ParentId == busy.Entity(1));

            Assert.AreEqual(quiet.Entity(1), busy.Entity(1));
            Assert.AreEqual(first.Tick, second.Tick);
            Assert.AreEqual(first.EntityId, second.EntityId);
            Assert.AreEqual(SpawnIdentity.CreateChild(quiet.Entity(1), first.Tick, 0, 0, 0), first.EntityId);
        }

        [Test]
        public void BulletsOutliveTheShipThatFiredThem()
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(cooldown: 10f)),
                new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
            };
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, Shooter, Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var bullet = CannonTests.BulletSpawns(driver.StepCollecting(3)).Single().EntityId;
            driver.Kill(1);
            driver.Step();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.IsTrue(driver.Battle.Snapshot.Bullets.ToArray().Any(b => b.Id == bullet));

            var removal = CannonTests.BulletRemovals(driver.StepCollecting(30)).Single();
            Assert.AreEqual(bullet, removal.EntityId);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
        }

        [Test]
        public void TheSnapshotListsEveryLiveBullet()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0f));
            driver.Control(1, systems: 1);

            var alive = new HashSet<EntityId>();
            for (var i = 0; i < 60; ++i)
            {
                driver.Step();
                var snapshot = driver.Battle.Snapshot;
                foreach (var item in snapshot.Events)
                {
                    if (!item.IsBullet) continue;
                    if (item.Kind == BattleEventKind.Spawn) Assert.IsTrue(alive.Add(item.EntityId));
                    if (item.Kind == BattleEventKind.Removal) Assert.IsTrue(alive.Remove(item.EntityId));
                }

                CollectionAssert.AreEquivalent(alive, snapshot.Bullets.ToArray().Select(b => b.Id));
            }

            Assert.Greater(alive.Count, 5);
        }

        [Test]
        public void RemovalsOfOneTickAreInEntityIdOrder()
        {
            // Two cannons fire in the same tick and their bullets expire together.
            var ship = Fixtures.ShipWith(
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0.5f)), new BarrelDefinition(new Vec2(1f, -0.5f)) },
                devices: new[]
                {
                    new CannonDefinition(0, 10f, 2f, 0f, Fixtures.Bolt),
                    new CannonDefinition(1, 10f, 2f, 0f, Fixtures.Bolt),
                });
            var driver = CannonTests.Duel(ship);
            driver.Control(1, systems: 3);

            var removals = CannonTests.BulletRemovals(driver.StepCollecting(40));

            Assert.AreEqual(2, removals.Count);
            Assert.AreEqual(removals[0].Tick, removals[1].Tick);
            Assert.Less(removals[0].EntityId.Value, removals[1].EntityId.Value);
        }

        [Test]
        public void ContinuousFireWithHitsAllocatesNothingAfterWarmUp()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0f, energyCost: 0f));
            driver.Place(101, new Vec2(56f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(40);

            // NUnit asserts allocate, so the loop only counts.
            var hits = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i)
            {
                driver.Battle.Step();
                foreach (var item in driver.Battle.Snapshot.Events)
                    if (item.Kind == BattleEventKind.Hit) ++hits;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Greater(hits, 30);
            Assert.AreEqual(0L, allocated);
        }
    }
}
