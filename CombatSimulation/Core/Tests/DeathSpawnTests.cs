using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 11a: the death-spawn device of legacy ToxicWaste and RetributionDevice
    // (TODO/battle_simulator_phase5.md, "Instant devices (11)") and its owner records (H8).
    public class DeathSpawnTests
    {
        private const float Tolerance = 1e-3f;

        // The built-in toxic cloud of a scale 1 ship (record 18, Size 1): radius 2, corrosive 5 per second, 4 s, drift
        // 0.02 of the ship velocity.
        private static readonly BulletDefinition Cloud = new BulletDefinition(2f, 0f, 0f, 5f, parentVelocityEffect: 0.02f,
            damageType: 3, impact: BulletImpact.OverTime, lifetime: 4f, scale: 4f);

        // A wraith pack: no effect, 0.5 s (26 ticks); at its expiry 3 homing missiles at a random angle, 0.5 u ahead.
        private static readonly BulletDefinition Pack = new BulletDefinition(0.5f, 0f, 0f, 0f, parentVelocityEffect: 0f,
            lifetime: 0.5f, impact: BulletImpact.AllTargets, triggers: new[]
            {
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Expired, Fixtures.Missile, new BulletSpawnPattern(
                    Enumerable.Repeat(new SpawnSlot(0f, 2f * (float)Math.PI, new Vec2(0.5f, 0f), Vec2.Zero), 3).ToArray())),
            });

        private static ShipDefinition Dying(BulletDefinition bullet, params DeviceDefinition[] more) =>
            Fixtures.ShipWith(armor: 100f, devices: new DeviceDefinition[] { new DeathSpawnDefinition(bullet) }.Concat(more).ToArray());

        // No shield, no resistances.
        private static readonly ShipDefinition Target = Fixtures.ShipWith(mass: 2f, armor: 200f, shield: new ResourceDefinition(0f));

        // Side 0 keeps a ready slot, so the battle goes on after its ship dies.
        private static Driver Duel(ShipDefinition ship)
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, ship),
                new RosterSlotSetup(new RosterId(101), 1, Target),
                new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship),
            }));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        private static BulletState[] Bullets(Driver driver) => driver.Battle.Snapshot.Bullets.ToArray();

        [Test]
        public void TheDefinitionRejectsABulletThatNeedsALiveShip()
        {
            Assert.Throws<ArgumentNullException>(() => new DeathSpawnDefinition(null));
            Assert.Throws<ArgumentException>(() => new DeathSpawnDefinition(
                new BulletDefinition(1f, 0f, 0f, 1f, lifetime: 1f, attachedToParent: true)));
            Assert.Throws<ArgumentException>(() => new DeathSpawnDefinition(
                new BulletDefinition(0.2f, 10f, 20f, 1f, harpoon: true)));
        }

        // ToxicWaste, row 1: at the death the cloud starts at the ship centre with 0.02 of the ship velocity. Its spawn event
        // comes before the ship's removal, in the death tick, and it first moves in the next tick.
        [Test]
        public void TheShipsDeathLeavesTheCloudWhereItDied()
        {
            var driver = Duel(Dying(Cloud));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(10f, 0f), 1f);
            var ship = driver.Entity(1);
            driver.Kill(1);
            var tick = driver.Tick;

            var events = driver.StepCollecting();
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            var spawn = events.FindIndex(e => e.Kind == BattleEventKind.Spawn && e.IsBullet);
            var removal = events.FindIndex(e => e.Kind == BattleEventKind.Removal && e.EntityId == ship);
            Assert.That(spawn, Is.GreaterThanOrEqualTo(0).And.LessThan(removal));
            Assert.AreEqual(tick, events[spawn].Tick);
            Assert.AreEqual(ship, events[spawn].ParentId);

            var cloud = Bullets(driver).Single();
            Assert.AreSame(Cloud, cloud.Definition);
            Assert.AreEqual(SpawnIdentity.CreateChild(ship, tick, 0, 0, 0), cloud.Id);
            Assert.AreEqual(50.2f, cloud.Position.X, Tolerance, "The ship moved by this tick's physics.");
            Assert.AreEqual(50f, cloud.Position.Y, Tolerance);
            Assert.AreEqual(1f, cloud.Angle, Tolerance);
            Assert.AreEqual(0.2f, cloud.LinearVelocity.X, Tolerance);
            Assert.AreEqual(tick + Cloud.LifetimeTicks - 1, cloud.ExpiryTick);

            driver.Step();
            Assert.AreEqual(50.204f, Bullets(driver).Single().Position.X, Tolerance);
        }

        // Row 1: an enemy inside takes 5 per second for the cloud's life; it expires after 4 s.
        [Test]
        public void TheCloudHurtsAnEnemyInsideUntilItExpires()
        {
            var driver = Duel(Dying(Cloud));
            driver.Place(101, new Vec2(52.5f, 50f), Vec2.Zero);
            driver.Kill(1);
            driver.Step();

            var events = driver.StepCollecting(Cloud.LifetimeTicks + 5);
            var hits = events.Where(e => e.Kind == BattleEventKind.Hit).ToList();
            Assert.AreEqual(Cloud.LifetimeTicks - 1, hits.Count);
            foreach (var hit in hits) Assert.AreEqual(0.1f, hit.Damage, Tolerance);
            Assert.AreEqual(RemovalReason.Expiry, CannonTests.BulletRemovals(events).Single().RemovalReason);
            Assert.IsEmpty(Bullets(driver));
        }

        // Retribution, row 2: the pack starts at the ship centre and heading; at its expiry its missiles fly out. They
        // keep their search without a live ship.
        [Test]
        public void TheShipsDeathReleasesThePackAndItsMissiles()
        {
            var driver = Duel(Dying(Pack));
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero, 0.5f);
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            var ship = driver.Entity(1);
            driver.Kill(1);
            driver.Step();

            var pack = Bullets(driver).Single();
            Assert.AreEqual(0.5f, pack.Angle, Tolerance);
            Assert.AreEqual(Vec2.Zero, pack.LinearVelocity);
            driver.StepUntil(pack.ExpiryTick + 1);

            var missiles = Bullets(driver);
            Assert.AreEqual(3, missiles.Length);
            foreach (var missile in missiles)
            {
                Assert.AreSame(Fixtures.Missile, missile.Definition);
                Assert.AreEqual(pack.Id, missile.ParentId);
                Assert.AreEqual(0.5f, (missile.Position - pack.Position).Length, Tolerance);
            }

            var events = driver.StepCollecting(60);
            Assert.That(events.Count(e => e.Kind == BattleEventKind.Hit && e.EntityId == driver.Entity(101)), Is.GreaterThan(0));
            Assert.IsNull(driver.Battle.FindShip(ship));
        }

        // Row 3: a retreat, a vanish and a saved ship spawn nothing.
        [Test]
        public void ARetreatSpawnsNothing()
        {
            var driver = Duel(Dying(Pack));
            driver.Retreat(driver.Entity(1));
            var events = driver.StepCollecting(Battle.RetreatTicks + 2);

            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            Assert.IsEmpty(CannonTests.BulletSpawns(events));
        }

        [Test]
        public void AVanishedShipSpawnsNothing()
        {
            var driver = Duel(Dying(Cloud));
            driver.Ship(1).PendingVanish = true;
            var events = driver.StepCollecting();

            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            Assert.IsEmpty(CannonTests.BulletSpawns(events));
        }

        [Test]
        public void AShipThatTheStoneSavesSpawnsNothing()
        {
            var driver = Duel(Dying(Cloud, new InfinityStoneDefinition(60f, 2f)));
            driver.Ship(1).AddPendingDamage(0, 1000f);
            var events = driver.StepCollecting(3);

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.IsEmpty(CannonTests.BulletSpawns(events));
        }

        // Finding 14: surrender destroys the ship through its death, so its devices spawn.
        [Test]
        public void SurrenderSpawnsTheCloud()
        {
            var driver = Duel(Dying(Cloud));
            driver.Surrender(0);
            var events = driver.StepCollecting();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.AreSame(Cloud, CannonTests.BulletSpawns(events).Single().BulletDefinition);
        }

        // Finding 8: a drone's cloud belongs to the drone (legacy: its mothership; only kill credit changes).
        [Test]
        public void ADronesDeathSpawnsItsCloud()
        {
            var drone = DroneBayTests.Drone(devices: new DeviceDefinition[] { new DeathSpawnDefinition(Cloud) });
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(drone: drone));
            var launched = DroneBayTests.Launch(driver);
            var id = launched.Id;
            driver.Battle.QueueDeath(id);
            driver.Step();

            var cloud = Bullets(driver).Single();
            Assert.AreEqual(id, cloud.ParentId);
            Assert.AreEqual(0, cloud.Side);
        }

        // H8: the copy adds the cloud from the owner's spawn record, which precedes the roster record that removes the
        // ship; both cores hold the same cloud.
        [Test]
        public void TheCopyAddsTheOwnersCloudBeforeTheShipLeaves()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(Dying(Cloud));
            owner.Place(101, new Vec2(70f, 70f), new Vec2(10f, 0f));
            remote.Place(101, new Vec2(70f, 70f), new Vec2(10f, 0f));
            owner.Kill(101);
            GateDeviceTests.Tick(owner, remote, link);

            Assert.AreEqual(RosterStatus.Destroyed, remote.Status(101));
            var original = Bullets(owner).Single();
            var copy = Bullets(remote).Single();
            Assert.AreEqual(original.Id, copy.Id);
            Assert.AreEqual(original.ExpiryTick, copy.ExpiryTick);
            Assert.AreEqual(original.Position.X, copy.Position.X, 0.01f);
            Assert.AreEqual(original.Position.Y, copy.Position.Y, Tolerance);
            Assert.AreEqual(original.LinearVelocity.X, copy.LinearVelocity.X, Tolerance);
        }

        [Test]
        public void TheCopySpawnsNothingFromItsOwnDeath()
        {
            var (_, remote, _) = GateDeviceTests.Pair(Dying(Cloud));
            remote.Ship(101).AddPendingDamage(0, 1000f);
            remote.Step(3);

            Assert.AreEqual(RosterStatus.Active, remote.Status(101));
            Assert.IsEmpty(Bullets(remote));
        }

        // A late batch applies the spawn record before the removal record of the same tick.
        [Test]
        public void ALateDeathSpawnRecordStillFindsTheShip()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(Dying(Pack));
            owner.Kill(101);
            for (var i = 0; i < 3; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            link.Deliver(remote);
            remote.Step();

            Assert.AreEqual(RosterStatus.Destroyed, remote.Status(101));
            Assert.AreEqual(Bullets(owner).Single().Id, Bullets(remote).Single().Id);
        }

        // A record later than the pack's life adds no pack, but the missiles of its owner's later records still come.
        [Test]
        public void ThePacksMissilesArriveAfterTheExpiredPack()
        {
            var (owner, remote, link) = GateDeviceTests.Pair(Dying(Pack));
            owner.Kill(101);
            for (var i = 0; i < 40; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            link.Deliver(remote);
            remote.Step();

            var missiles = Bullets(owner);
            Assert.AreEqual(3, missiles.Length);
            CollectionAssert.AreEquivalent(missiles.Select(m => m.Id), Bullets(remote).Select(m => m.Id));
            Assert.AreEqual(RosterStatus.Destroyed, remote.Status(101));
        }
    }
}
