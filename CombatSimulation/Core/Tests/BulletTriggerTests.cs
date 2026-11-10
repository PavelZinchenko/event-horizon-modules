using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 2: SpawnBullet and Detonate triggers (legacy Bullet and SpawnBulletsAction), and the H8 owner rules.
    public class BulletTriggerTests
    {
        private const float Tolerance = 1e-4f;
        private const float Quarter = (float)(Math.PI / 2.0);

        // Speed 10, range 2: 0.2 s, 11 ticks.
        private static readonly BulletDefinition Child = new BulletDefinition(0.1f, 10f, 2f, 1f);
        private static readonly BulletDefinition Other = new BulletDefinition(0.1f, 10f, 2f, 2f);
        private static readonly BulletDefinition Third = new BulletDefinition(0.1f, 10f, 2f, 3f);

        // The fixture bolt (speed 20, range 10, 26 ticks) with triggers.
        private static BulletDefinition Bolt(params BulletTriggerDefinition[] triggers) => Bolt(true, triggers);

        private static BulletDefinition Bolt(bool detonateWhenDestroyed, params BulletTriggerDefinition[] triggers) =>
            new BulletDefinition(0.25f, 20f, 10f, 5f, triggers: triggers, detonateWhenDestroyed: detonateWhenDestroyed);

        private static BulletTriggerDefinition Spawn(BulletTriggerConditions conditions, BulletDefinition bullet = null,
            BulletSpawnPattern pattern = null, float randomFactor = 0f) =>
            BulletTriggerDefinition.Spawn(conditions, bullet ?? Child, pattern, randomFactor);

        private static Driver Fire(BulletDefinition bullet, Vec2? target = null)
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(bullet, cooldown: 10f));
            if (target.HasValue) driver.Place(101, target.Value, Vec2.Zero);
            driver.Control(1, systems: 1);
            return driver;
        }

        private static List<BattleEvent> Spawns(IEnumerable<BattleEvent> events) => CannonTests.BulletSpawns(events);

        private static List<BattleEvent> ChildrenOf(IEnumerable<BattleEvent> events, EntityId parent) =>
            Spawns(events).Where(e => e.ParentId == parent).ToList();

        private static BulletState State(Driver driver, EntityId id) =>
            driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.Id == id);

        [Test]
        public void ExpiredChildrenStartAtThePatternPoseAndMoveFromTheNextTick()
        {
            var pattern = new BulletSpawnPattern(new[]
            {
                new SpawnSlot(0f, 0f, new Vec2(1f, 0f), Vec2.Zero),
                new SpawnSlot(Quarter, 0f, new Vec2(1f, 0f), Vec2.Zero),
            });
            var driver = Fire(Bolt(Spawn(BulletTriggerConditions.Expired, pattern: pattern)));

            var events = new List<BattleEvent>();
            Vec2 end = default;
            while (!events.Any(e => e.Kind == BattleEventKind.Removal && e.IsBullet))
            {
                var bullets = driver.Battle.Snapshot.Bullets;
                if (bullets.Length == 1) end = bullets[0].Position + Battle.TickDuration * bullets[0].LinearVelocity;
                driver.Step();
                events.AddRange(driver.Battle.Snapshot.Events.ToArray());
            }

            var parent = Spawns(events).First();
            var removal = CannonTests.BulletRemovals(events).Single();
            var children = ChildrenOf(events, parent.EntityId);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
            Assert.AreEqual(2, children.Count);
            var spawnTick = removal.Tick + 1;
            for (var i = 0; i < 2; ++i)
            {
                Assert.AreEqual(SpawnIdentity.CreateChild(parent.EntityId, spawnTick, 0, 0, (uint)i), children[i].EntityId);
                Assert.AreEqual(removal.Tick, children[i].Tick);
                Assert.Less(children[i].Sequence, removal.Sequence, "The children come before the parent leaves.");
                Assert.AreSame(Child, children[i].BulletDefinition);
            }

            // The parent's last position, plus the offset turned by the child's heading. Parent velocity effect 1.
            var straight = State(driver, children[0].EntityId);
            var turned = State(driver, children[1].EntityId);
            Assert.AreEqual(end.X + 1f, straight.Position.X, Tolerance);
            Assert.AreEqual(end.Y, straight.Position.Y, Tolerance);
            Assert.AreEqual(end.X, turned.Position.X, Tolerance);
            Assert.AreEqual(end.Y + 1f, turned.Position.Y, Tolerance);
            Assert.AreEqual(Quarter, turned.Angle, Tolerance);
            Assert.AreEqual(20f, turned.LinearVelocity.X, Tolerance);
            Assert.AreEqual(10f, turned.LinearVelocity.Y, Tolerance);
            Assert.AreEqual(spawnTick + Child.LifetimeTicks - 1, turned.ExpiryTick);
            Assert.AreEqual(parent.EntityId, turned.ParentId);

            driver.Step();
            Assert.AreEqual(end.X + 0.4f, State(driver, children[1].EntityId).Position.X, Tolerance);
            Assert.AreEqual(end.Y + 1.2f, State(driver, children[1].EntityId).Position.Y, Tolerance);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AHitRunsHitThenDestroyedThenDetonated(bool detonateWhenDestroyed)
        {
            var bolt = Bolt(detonateWhenDestroyed,
                Spawn(BulletTriggerConditions.Detonated, Third),
                Spawn(BulletTriggerConditions.Destroyed, Other),
                Spawn(BulletTriggerConditions.Hit, Child),
                Spawn(BulletTriggerConditions.Expired, Child));
            var driver = Fire(bolt, new Vec2(56f, 50f));

            var events = driver.StepCollecting(20);
            var parent = Spawns(events).First();
            var hit = RammingTests.Hits(events).Single(e => e.AttackerId == parent.EntityId);
            var removal = CannonTests.BulletRemovals(events).Single(e => e.EntityId == parent.EntityId);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);

            var children = ChildrenOf(events, parent.EntityId);
            var expected = detonateWhenDestroyed ? new[] { Child, Other, Third } : new[] { Child, Other };
            CollectionAssert.AreEqual(expected, children.Select(c => c.BulletDefinition).ToArray());
            var triggers = detonateWhenDestroyed ? new uint[] { 2, 1, 0 } : new uint[] { 2, 1 };
            for (var i = 0; i < children.Count; ++i)
            {
                // The sequence counts the parent's children of one spawn tick across its triggers.
                Assert.AreEqual(SpawnIdentity.CreateChild(parent.EntityId, hit.Tick + 1, triggers[i], 0, (uint)i),
                    children[i].EntityId);
                Assert.Greater(children[i].Sequence, hit.Sequence);
                Assert.Less(children[i].Sequence, removal.Sequence);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ADetonateTriggerMakesAnExpiringBulletDetonate(bool detonate)
        {
            var triggers = new List<BulletTriggerDefinition>
            {
                Spawn(BulletTriggerConditions.Detonated, Third),
                Spawn(BulletTriggerConditions.Destroyed, Other),
            };
            if (detonate) triggers.Add(BulletTriggerDefinition.Detonate(BulletTriggerConditions.Expired));
            var driver = Fire(Bolt(triggers.ToArray()));

            var events = driver.StepCollecting(40);
            var parent = Spawns(events).First();
            var children = ChildrenOf(events, parent.EntityId).Select(c => c.BulletDefinition).ToArray();
            CollectionAssert.AreEqual(detonate ? new[] { Other, Third } : new[] { Other }, children);
        }

        [Test]
        public void ADetonateTriggerOnHitStopsABulletThatSurvivesTheContact()
        {
            // A missile survives a 1-damage bolt, but its Detonate trigger on Hit stops it (legacy DetonateAction).
            var missile = new BulletDefinition(0.2f, 1f, 100f, 0f, hitPoints: 5f, triggers: new[]
            {
                BulletTriggerDefinition.Detonate(BulletTriggerConditions.Hit),
                Spawn(BulletTriggerConditions.Detonated, Other),
            });
            var bolt = new BulletDefinition(0.25f, 20f, 10f, 1f);
            var driver = CannonTests.Duel(Fixtures.CannonShip(bolt, cooldown: 10f),
                Fixtures.CannonShip(missile, cooldown: 10f));
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(1, systems: 1);
            driver.Control(101, course: (float)Math.PI, systems: 1);

            var events = driver.StepCollecting(30);
            var parent = Spawns(events).Single(e => e.BulletDefinition == missile);
            var removal = CannonTests.BulletRemovals(events).Single(e => e.EntityId == parent.EntityId);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreEqual(BulletTriggerConditions.Destroyed | BulletTriggerConditions.Detonated, removal.RemovalConditions);
            Assert.AreSame(Other, ChildrenOf(events, parent.EntityId).Single().BulletDefinition);
        }

        // Phase 5b, step 6: the removal names the conditions that fired for the view's sound and effect triggers, also
        // for a bullet without gameplay triggers.
        [Test]
        public void TheRemovalNamesTheConditionsThatFired()
        {
            BattleEvent Expiry(BulletDefinition bullet)
            {
                var events = Fire(bullet).StepCollecting(40);
                return CannonTests.BulletRemovals(events).First(e => e.BulletDefinition == bullet);
            }

            const BulletTriggerConditions expired = BulletTriggerConditions.Expired | BulletTriggerConditions.Destroyed;
            var plain = Expiry(Bolt(false));
            Assert.AreEqual(RemovalReason.Expiry, plain.RemovalReason);
            Assert.AreEqual(expired, plain.RemovalConditions);
            Assert.AreEqual(expired | BulletTriggerConditions.Detonated,
                Expiry(Bolt(false, BulletTriggerDefinition.Detonate(BulletTriggerConditions.Expired))).RemovalConditions);
        }

        [Test]
        public void CreatedTriggersNestInTheSpawnTick()
        {
            var grandchild = new BulletDefinition(0.1f, 5f, 2f, 1f);
            var child = new BulletDefinition(0.1f, 10f, 2f, 1f,
                triggers: new[] { Spawn(BulletTriggerConditions.Created, grandchild) });
            var driver = Fire(Bolt(Spawn(BulletTriggerConditions.Created, child)));

            List<BattleEvent> events;
            do events = driver.StepCollecting(); while (Spawns(events).Count == 0);

            var spawns = Spawns(events);
            Assert.AreEqual(3, spawns.Count);
            var tick = spawns[0].Tick;
            Assert.AreEqual(SpawnIdentity.CreateChild(spawns[0].EntityId, tick, 0, 0, 0), spawns[1].EntityId);
            Assert.AreEqual(SpawnIdentity.CreateChild(spawns[1].EntityId, tick, 0, 0, 0), spawns[2].EntityId);

            // All three start at the muzzle (51, 50) and move in the spawn tick, each at its own speed.
            Assert.AreEqual(51.4f, State(driver, spawns[0].EntityId).Position.X, Tolerance);
            Assert.AreEqual(51.6f, State(driver, spawns[1].EntityId).Position.X, Tolerance, "Speed 10 plus the bolt's 20.");
            Assert.AreEqual(51.7f, State(driver, spawns[2].EntityId).Position.X, Tolerance, "Speed 5 plus the child's 30.");
        }

        [Test]
        public void TheRandomFactorVariesSpeedAndLifetimeTheSameWayOnEveryRun()
        {
            var slots = Enumerable.Repeat(new SpawnSlot(), 8).ToArray();
            var bolt = Bolt(Spawn(BulletTriggerConditions.Expired, pattern: new BulletSpawnPattern(slots), randomFactor: 1f));

            BulletState[] Run()
            {
                var driver = Fire(bolt);
                driver.StepCollecting(27);
                return driver.Battle.Snapshot.Bullets.ToArray().Where(b => b.Definition == Child).ToArray();
            }

            var first = Run();
            var second = Run();
            Assert.AreEqual(8, first.Length);
            var speeds = first.Select(b => b.LinearVelocity.X - 20f).ToArray();
            foreach (var speed in speeds)
                Assert.That(speed, Is.InRange(5f - Tolerance, 15f + Tolerance));
            Assert.Greater(speeds.Distinct().Count(), 1);
            Assert.Greater(first.Select(b => b.ExpiryTick).Distinct().Count(), 1);
            CollectionAssert.AreEqual(first.Select(b => b.LinearVelocity.X), second.Select(b => b.LinearVelocity.X));
            CollectionAssert.AreEqual(first.Select(b => b.ExpiryTick), second.Select(b => b.ExpiryTick));
        }

        [Test]
        public void PointDefenseRunsHitThenDisarmedTriggers()
        {
            var bolt = Bolt(Spawn(BulletTriggerConditions.Disarmed, Other), Spawn(BulletTriggerConditions.Hit, Child),
                Spawn(BulletTriggerConditions.Destroyed, Third));
            var defense = Fixtures.ShipWith(devices: new[] { new PointDefenseDefinition(4f, 0f, 1f) });
            var driver = CannonTests.Duel(Fixtures.CannonShip(bolt, cooldown: 10f), defense);
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(101, systems: 1);

            var events = driver.StepCollecting(30);
            var parent = Spawns(events).First();
            var removal = CannonTests.BulletRemovals(events).Single(e => e.EntityId == parent.EntityId);
            Assert.AreEqual(RemovalReason.Disposal, removal.RemovalReason);
            Assert.AreEqual(BulletTriggerConditions.Disarmed, removal.RemovalConditions);
            CollectionAssert.AreEqual(new[] { Child, Other },
                ChildrenOf(events, parent.EntityId).Select(c => c.BulletDefinition).ToArray());
        }

        // Legacy Body.CanBeDisarmed false: point defense pays for each zap and the bolt runs its Hit triggers, but flies on.
        [Test]
        public void PointDefenseZapsABulletThatCannotBeDisarmedWithoutStoppingIt()
        {
            var bolt = new BulletDefinition(0.25f, 20f, 10f, 5f, canBeDisarmed: false,
                triggers: new[] { Spawn(BulletTriggerConditions.Hit, Child) });
            var defense = Fixtures.ShipWith(devices: new[] { new PointDefenseDefinition(4f, 0f, 1f) });
            var driver = CannonTests.Duel(Fixtures.CannonShip(bolt, cooldown: 10f), defense);
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(101, systems: 1);
            driver.Ship(101).Energy.RechargeRate = 0f;
            var before = driver.Ship(101).Energy.Value;

            var events = driver.StepCollecting(30);
            var parent = Spawns(events).First(e => e.BulletDefinition == bolt);
            var zaps = events.Count(e => e.Kind == BattleEventKind.Disarm && e.EntityId == parent.EntityId);
            var removal = CannonTests.BulletRemovals(events).Single(e => e.EntityId == parent.EntityId);

            Assert.Greater(zaps, 1, "The bolt stays a target after each zap.");
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason, "It still hits the ship.");
            Assert.AreEqual(driver.Entity(101), RammingTests.Hits(events).Single(e => e.AttackerId == parent.EntityId).EntityId);
            // Its Hit children are bolts that can be disarmed; their zaps are paid too.
            var allZaps = events.Count(e => e.Kind == BattleEventKind.Disarm);
            Assert.AreEqual(before - 1f - allZaps, driver.Ship(101).Energy.Value, Tolerance, "Activation, then one payment per zap.");
            Assert.AreEqual(zaps + 1, ChildrenOf(events, parent.EntityId).Count, "One Hit run per zap and one at the ship.");
        }

        [Test]
        public void ARemoteZapOnABulletThatCannotBeDisarmedCountsOnce()
        {
            // The defender is remote: this device runs its copy of the point defense, then receives the defender's claim.
            var bolt = new BulletDefinition(0.25f, 20f, 10f, 5f, canBeDisarmed: false,
                triggers: new[] { Spawn(BulletTriggerConditions.Hit, Child) });
            var d = new Driver(new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(bolt, cooldown: 10f), 10),
                new RosterSlotSetup(new RosterId(101), 1,
                    Fixtures.ShipWith(devices: new[] { new PointDefenseDefinition(4f, 0.1f, 1f) }), 20),
            }, localOwners: new[] { 10 }));
            d.Battle.DeploySlot(d.Slot(1));
            d.Battle.DeploySlot(d.Slot(101));
            d.Step();
            d.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            d.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            d.Control(1, systems: 1);
            d.Control(101, systems: 1);

            BattleEvent zap = default;
            EntityId parent = default;
            for (var i = 0; i < 30 && zap.Kind != BattleEventKind.Disarm; ++i)
            {
                d.Step();
                foreach (var e in d.Battle.Snapshot.Events)
                {
                    if (e.Kind == BattleEventKind.Spawn && e.BulletDefinition == bolt) parent = e.EntityId;
                    if (e.Kind == BattleEventKind.Disarm) zap = e;
                }
            }
            Assert.AreEqual(BattleEventKind.Disarm, zap.Kind);
            Assert.AreEqual(1, Children(d, Child).Length);

            var claim = new ResolvedHit(d.BattleId, zap.Tick, d.Entity(101), 20, 0, parent, 10, 0, HitContactState.Start,
                HitImpactKind.Disarm, 0f, 1, targetIsBullet: true);
            Assert.IsTrue(d.Battle.Submit(new InboundHit(20, claim)));
            d.Step();

            Assert.AreEqual(1, Children(d, Child).Length, "The claim names a zap this device already counted.");
            Assert.IsTrue(d.Battle.Snapshot.Bullets.ToArray().Any(b => b.Id == parent), "The bolt flies on.");
        }

        [Test]
        public void ChildrenTakeTheAllegianceOfTheShipThatFiredTheirAncestor()
        {
            var lasting = new BulletDefinition(0.1f, 1f, 10f, 1f);
            var driver = Fire(Bolt(Spawn(BulletTriggerConditions.Created, lasting)));
            driver.StepCollecting(3);
            var child = driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.Definition == lasting);

            Assert.IsTrue(driver.Battle.Submit(ShipCommand.ChangeAllegiance(driver.BattleId, 900, driver.Tick,
                driver.Entity(1), Allegiance.Undefined)));
            driver.Step();
            Assert.AreEqual(Allegiance.Undefined, State(driver, child.Id).Allegiance);
        }

        [Test]
        public void TriggerDefinitionsRejectInvalidValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BulletTriggerDefinition.Detonate(BulletTriggerConditions.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => BulletTriggerDefinition.Detonate((BulletTriggerConditions)64));
            Assert.Throws<ArgumentNullException>(() => BulletTriggerDefinition.Spawn(BulletTriggerConditions.Hit, null));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Hit, Child, randomFactor: 1.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BulletSpawnPattern(Array.Empty<SpawnSlot>()));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BulletSpawnPattern(new[] { new SpawnSlot(float.NaN, 0f, Vec2.Zero, Vec2.Zero) }));

            var bolt = Bolt(Spawn(BulletTriggerConditions.Hit), BulletTriggerDefinition.Detonate(BulletTriggerConditions.Expired));
            Assert.AreEqual(BulletTriggerConditions.Hit | BulletTriggerConditions.Expired, bolt.TriggerConditions);
            Assert.IsTrue(bolt.HasSpawnTriggers);
            Assert.IsFalse(Bolt(BulletTriggerDefinition.Detonate(BulletTriggerConditions.Hit)).HasSpawnTriggers);
        }

        // H8 (4): the owner sends each child; the receiver adds it with the same identity and never derives one.

        private const int RemoteOwner = 20;

        private static BattleSetup NetworkSetup(int localOwner, ShipDefinition ship) => new BattleSetup(new BattleId(77), 13,
            new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, ship, RemoteOwner),
            }, localOwners: new[] { localOwner });

        private static (Driver owner, Driver remote, Link link) Pair(BulletDefinition bullet)
            => Pair(Fixtures.CannonShip(bullet, cooldown: 10f, energy: new ResourceDefinition(100000f)));

        private static (Driver owner, Driver remote, Link link) Pair(ShipDefinition ship)
        {
            var owner = new Driver(NetworkSetup(RemoteOwner, ship));
            var remote = new Driver(NetworkSetup(10, ship));
            var link = new Link();
            owner.Deploy(101);
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();
            Tick(owner, remote, link);
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1);
            return (owner, remote, link);
        }

        private static void Tick(Driver owner, Driver remote, Link link, bool deliver = true)
        {
            owner.Step();
            link.Capture(owner);
            if (deliver) link.Deliver(remote);
            remote.Step();
        }

        private static BulletState[] Children(Driver driver, BulletDefinition definition) =>
            driver.Battle.Snapshot.Bullets.ToArray().Where(b => b.Definition == definition).OrderBy(b => b.Id.Value).ToArray();

        [Test]
        public void TheReceiverAddsTheOwnersChildrenWithTheSameIdentity()
        {
            var pattern = new BulletSpawnPattern(new[]
            {
                new SpawnSlot(0f, 0f, new Vec2(1f, 0f), Vec2.Zero),
                new SpawnSlot(Quarter, 0f, new Vec2(1f, 0f), Vec2.Zero),
            });
            var (owner, remote, link) = Pair(Bolt(Spawn(BulletTriggerConditions.Expired, pattern: pattern, randomFactor: 0.5f)));
            for (var i = 0; i < 30 && Children(owner, Child).Length == 0; ++i) Tick(owner, remote, link);

            var expected = Children(owner, Child);
            Assert.AreEqual(2, expected.Length);
            Assert.IsEmpty(Children(remote, Child), "The receiver derives no children.");
            Assert.AreEqual(1, remote.Battle.TriggerParentCount);

            Tick(owner, remote, link);
            var actual = Children(remote, Child);
            var owned = Children(owner, Child);
            CollectionAssert.AreEqual(owned.Select(b => b.Id), actual.Select(b => b.Id));
            for (var i = 0; i < owned.Length; ++i)
            {
                Assert.AreEqual(owned[i].Position.X, actual[i].Position.X, Tolerance);
                Assert.AreEqual(owned[i].Position.Y, actual[i].Position.Y, Tolerance);
                Assert.AreEqual(owned[i].LinearVelocity.X, actual[i].LinearVelocity.X, Tolerance);
                Assert.AreEqual(owned[i].LinearVelocity.Y, actual[i].LinearVelocity.Y, Tolerance);
                Assert.AreEqual(owned[i].ExpiryTick, actual[i].ExpiryTick);
            }
            Assert.AreEqual(0, remote.Battle.TriggerParentCount, "The parent is forgotten a tick after its removal.");
        }

        [Test]
        public void ALateChildRecordReplaysToThePresent()
        {
            var (owner, remote, link) = Pair(Bolt(Spawn(BulletTriggerConditions.Expired)));
            for (var i = 0; i < 5 && owner.Battle.Snapshot.Bullets.Length == 0; ++i) Tick(owner, remote, link);
            var expiry = owner.Battle.Snapshot.Bullets[0].ExpiryTick;
            while (owner.Tick < expiry) Tick(owner, remote, link);

            // The children's records and the parent's removal arrive 4 ticks late; the copy has expired here meanwhile.
            for (var i = 0; i < 4; ++i) Tick(owner, remote, link, deliver: false);
            Assert.IsEmpty(Children(remote, Child));
            link.Deliver(remote);
            owner.Step();
            link.Capture(owner);
            remote.Step();

            var owned = Children(owner, Child).Single();
            var actual = Children(remote, Child).Single();
            Assert.AreEqual(owned.Id, actual.Id);
            Assert.AreEqual(owned.Position.X, actual.Position.X, Tolerance);
            Assert.AreEqual(owned.Position.Y, actual.Position.Y, Tolerance);
            Assert.AreEqual(0, remote.Battle.TriggerParentCount);
        }

        [Test]
        public void AParentRemovedByPredictionGetsNoLocalChildren()
        {
            // The receiver's copy of the owner's bolt stops at the receiver's ship (N2); only the owner's records add
            // the children.
            var d = new Driver(new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.CannonShip(
                    Bolt(Spawn(BulletTriggerConditions.Hit), Spawn(BulletTriggerConditions.Detonated, Other)), cooldown: 10f), 20),
            }, localOwners: new[] { 10 }));
            d.Battle.DeploySlot(d.Slot(1));
            d.Battle.DeploySlot(d.Slot(101));
            d.Step();
            d.Place(1, new Vec2(46f, 50f), Vec2.Zero);
            d.Place(101, new Vec2(50f, 50f), Vec2.Zero, (float)Math.PI);
            d.Control(101, course: (float)Math.PI, systems: 1);

            var events = d.StepCollecting(15);
            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreEqual(BattleEventAuthority.Predicted, removal.Authority);
            Assert.AreEqual(1, Spawns(events).Count, "No local children of a remote bullet.");
            Assert.IsEmpty(d.Battle.Snapshot.OutgoingOperations.ToArray());
        }

        [Test]
        public void AChildRecordNeedsALaunchPoseAndAKnownSpawnTrigger()
        {
            var (owner, remote, link) = Pair(Bolt(Spawn(BulletTriggerConditions.Expired)));
            Tick(owner, remote, link);
            var parent = remote.Battle.Snapshot.Bullets[0].Id;
            var tick = remote.Tick;
            ulong sequence = 1000;

            bool Applies(OwnerOperation operation)
            {
                Assert.IsTrue(remote.Battle.Submit(RemoteOwner, operation));
                remote.Step();
                return remote.Battle.Snapshot.AppliedOperations.ToArray().Any(o => o.EntityId == operation.EntityId);
            }

            OwnerOperation Record(int trigger, bool launch)
            {
                var id = SpawnIdentity.CreateChild(parent, tick, (uint)trigger, 0, 0);
                return launch
                    ? OwnerOperation.BulletFired(remote.BattleId, RemoteOwner, ++sequence, tick, id, parent, trigger, 0, 0,
                        new Vec2(60f, 50f), 0f)
                    : OwnerOperation.BulletFired(remote.BattleId, RemoteOwner, ++sequence, tick, id, parent, trigger, 0, 0);
            }

            Assert.IsFalse(Applies(Record(0, launch: false)));
            ++tick;
            Assert.IsFalse(Applies(Record(1, launch: true)), "The bolt has one trigger.");
            ++tick;
            Assert.IsTrue(Applies(Record(0, launch: true)));
        }

        [TestCase(0)]
        [TestCase(12)]
        public void BeamChildrenArriveOnceEvenAfterTheBeamExpired(int delay)
        {
            var child = new BulletDefinition(0.1f, 0f, 0f, 0f, lifetime: 3f, impact: BulletImpact.OverTime);
            var beam = BeamDefinition.Shot(0, 10f, 0f, 20f, 0f, 0.1f,
                triggers: new[] { BulletTriggerDefinition.Spawn(BulletTriggerConditions.Hit, child, cooldown: 1f) });
            var (owner, remote, link) = Pair(Fixtures.ShipWith(
                barrels: new[] { new BarrelDefinition(Vec2.Zero, 0f) }, devices: new[] { beam }));
            remote.Deploy(1);
            remote.Step();
            foreach (var op in remote.Battle.Snapshot.OutgoingOperations) owner.Battle.Submit(op.Owner, op);
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            foreach (var driver in new[] { owner, remote })
            {
                driver.Place(101, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(1, new Vec2(60f, 50f), Vec2.Zero);
            }
            for (var i = 0; i < delay + 3; ++i) Tick(owner, remote, link, deliver: false);
            Assert.AreEqual(1, Children(owner, child).Length);
            Assert.IsEmpty(Children(remote, child));
            link.Deliver(remote);
            Tick(owner, remote, link);
            var expected = Children(owner, child).Single();
            var actual = Children(remote, child).Single();
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Position.X, actual.Position.X, Tolerance);
            Assert.AreEqual(expected.Position.Y, actual.Position.Y, Tolerance);
            Assert.AreEqual(expected.ExpiryTick, actual.ExpiryTick);
        }

        private sealed class Link
        {
            private readonly List<OwnerOperation> _held = new List<OwnerOperation>(256);
            private ulong _lastSequence;
            private long _through = -1;

            public void Capture(Driver owner)
            {
                foreach (var operation in owner.Battle.Snapshot.OutgoingOperations)
                {
                    _held.Add(operation);
                    _lastSequence = operation.Sequence;
                }
                _through = owner.Battle.Snapshot.Tick;
            }

            public void Deliver(Driver remote)
            {
                foreach (var operation in _held)
                    if (!remote.Battle.Submit(operation.Owner, operation)) throw new InvalidOperationException("Rejected record.");
                _held.Clear();
                if (!remote.Battle.Submit(RemoteOwner, new OwnerWatermark(remote.BattleId, RemoteOwner, _through, _lastSequence)))
                    throw new InvalidOperationException("Rejected watermark.");
            }
        }
    }
}
