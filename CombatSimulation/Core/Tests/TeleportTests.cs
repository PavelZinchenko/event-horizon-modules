using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 6c: the rift's six target cases, contact entry, and target-owner delivery.
    public class TeleportTests
    {
        private const float Tolerance = 0.001f;
        private static readonly EngineDefinition IdleEngine = new EngineDefinition(0f, 0f, 0f, 0f);

        private static BulletDefinition Rift(float distance = 100f, BulletImpact impact = BulletImpact.AllTargets,
            float radius = 2.5f) => new BulletDefinition(radius, 0f, 0f, 50f, impact: impact, lifetime: 0.5f,
                effects: new ImpactEffects(teleport: distance));

        private static ShipDefinition Target(bool immune = false, bool worm = false) =>
            new ShipDefinition(1f, 10f, IdleEngine, 1000f, new ResourceDefinition(0f), new ResourceDefinition(100f),
                devices: worm ? new DeviceDefinition[] { new WormTailDefinition(0.3f, 10, 5f, 0.45f, 0.35f, 0.4419455f) } : null,
                teleportImmune: immune);

        private static ShipDefinition Shooter(BulletDefinition rift = null, Vec2? muzzle = null) =>
            Fixtures.CannonShip(rift ?? Rift(), cooldown: 10f, energyCost: 0f, muzzle: muzzle);

        private static void Near(Vec2 expected, Vec2 actual)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance);
            Assert.AreEqual(expected.Y, actual.Y, Tolerance);
        }

        [TestCase(0f)]
        [TestCase(90f)]
        public void RiftMovesAlongHeadingWrapsStopsVelocityAndKeepsSpin(float degrees)
        {
            var driver = CannonTests.Duel(Shooter(), Target());
            driver.Place(1, new Vec2(150f, 150f), Vec2.Zero);
            driver.Place(101, new Vec2(153f, 150f), new Vec2(1f, 2f), degrees * Fixtures.Degrees);
            var world = driver.Battle.World;
            var body = driver.Ship(101).Body;
            world.SetAngularVelocity(body, 0.25f);
            driver.Control(1, systems: 1);
            driver.Step(2); // The initial barrel wait ends at tick 2.
            var beforeTeleport = new Vec2(153f, 150f) + new Vec2(1f, 2f) * (2f * Battle.TickDuration);
            var heading = world.GetRotation(body).Rotate(new Vec2(1f, 0f));
            Near(world.Torus.Wrap(beforeTeleport + 100f * heading), world.GetPosition(body));
            Near(Vec2.Zero, world.GetLinearVelocity(body));
            Assert.AreEqual(0.25f, world.GetAngularVelocity(body), Tolerance);
            Assert.AreEqual(950f, driver.Ship(101).Armor.Value);
        }

        [Test]
        public void ImmuneShipTakesDamageWithoutMovingOrStopping()
        {
            var driver = CannonTests.Duel(Shooter(), Target(immune: true));
            driver.Place(101, new Vec2(53f, 50f), new Vec2(1f, 0f));
            driver.Control(1, systems: 1);
            driver.Step(2);
            Near(new Vec2(53.04f, 50f), driver.Battle.World.GetPosition(driver.Ship(101).Body));
            Near(new Vec2(1f, 0f), driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body));
            Assert.AreEqual(950f, driver.Ship(101).Armor.Value);
        }

        [TestCase(BulletImpact.AllTargets)]
        [TestCase(BulletImpact.OverTime)]
        public void TeleportRunsOnEntryAndAgainOnlyAfterSeparation(BulletImpact impact)
        {
            var driver = CannonTests.Duel(Shooter(Rift(0.25f, impact)), Target());
            driver.Place(101, new Vec2(53f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(3);
            Near(new Vec2(53.25f, 50f), driver.Battle.World.GetPosition(driver.Ship(101).Body));
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Step();
            driver.Place(101, new Vec2(53f, 50f), Vec2.Zero);
            driver.Step();
            Near(new Vec2(53.25f, 50f), driver.Battle.World.GetPosition(driver.Ship(101).Body));
        }

        [Test]
        public void DroneTakesDamageAndTeleports()
        {
            var driver = DroneBayTests.Start(enemy: Shooter());
            var drone = DroneBayTests.Launch(driver);
            DroneBayTests.Place(driver, drone, new Vec2(103f, 100f));
            driver.Control(101, systems: 1);
            driver.Step();
            Near(new Vec2(3f, 100f), driver.Battle.World.GetPosition(drone.Body));
            Assert.AreEqual(50f, drone.Armor.Value);
        }

        [Test]
        public void HeadHitMovesTheWholeTailWithItsOffsetsIntact()
        {
            var driver = CannonTests.Duel(Shooter(), Target(worm: true));
            var world = driver.Battle.World;
            world.TeleportGroup(driver.Ship(101).Body, new Vec2(80f, 50f), (float)Math.PI);
            driver.Step(30);
            world.TeleportGroup(driver.Ship(101).Body, new Vec2(53f, 50f));
            var segments = driver.Battle.Snapshot.Segments.ToArray();
            Assert.AreEqual(10, segments.Length);
            var offsets = segments.Select(s => world.Torus.Delta(world.GetPosition(driver.Ship(101).Body),
                world.GetPosition(driver.Battle.FindSegment(s.Id).Body))).ToArray();
            driver.Control(1, systems: 1);
            driver.Step();
            Near(new Vec2(153f, 50f), world.GetPosition(driver.Ship(101).Body));
            for (var i = 0; i < segments.Length; ++i)
            {
                var segment = driver.Battle.FindSegment(segments[i].Id);
                Assert.IsNotNull(segment);
                Assert.IsFalse(segment.Detached);
                Near(offsets[i], world.Torus.Delta(world.GetPosition(driver.Ship(101).Body), world.GetPosition(segment.Body)));
            }
        }

        [Test]
        public void DirectSegmentHitDestroysItAndDetachesTheRest()
        {
            var driver = CannonTests.Duel(Shooter(Rift(radius: 0.05f), new Vec2(0f, -3f)), Target(worm: true));
            var world = driver.Battle.World;
            var segmentId = HitSegment(driver, 4);
            Assert.IsNull(driver.Battle.FindSegment(segmentId));
            Assert.AreEqual(3, driver.Battle.Snapshot.Segments.ToArray().Count(s => !s.Detached));
            Assert.AreEqual(6, driver.Battle.Snapshot.Segments.ToArray().Count(s => s.Detached));
            Near(new Vec2(80f, 50f), world.GetPosition(driver.Ship(101).Body));
        }

        // Legacy FixedWormSegment.Affect ignores CollisionEffect.Destroy: the fixed segment survives and nothing moves.
        [TestCase(1)]
        [TestCase(2)]
        public void FixedSegmentIgnoresTeleport(int number)
        {
            var driver = CannonTests.Duel(Shooter(Rift(radius: 0.05f), new Vec2(0f, -3f)), Target(worm: true));
            var world = driver.Battle.World;
            var segmentId = HitSegment(driver, number);
            Assert.IsNotNull(driver.Battle.FindSegment(segmentId));
            Assert.IsFalse(driver.Battle.FindSegment(segmentId).Detached);
            Assert.AreEqual(0, driver.Battle.Snapshot.Segments.ToArray().Count(s => s.Detached));
            Near(new Vec2(80f, 50f), world.GetPosition(driver.Ship(101).Body));
        }

        // Moves the worm clear of the shooter, then fires one rift at the given segment. Returns the segment's ID.
        private static EntityId HitSegment(Driver driver, int number)
        {
            var world = driver.Battle.World;
            world.TeleportGroup(driver.Ship(101).Body, new Vec2(80f, 50f), 0f);
            driver.Step(30);
            var segment = driver.Battle.Snapshot.Segments.ToArray().Select(s => driver.Battle.FindSegment(s.Id))
                .Single(s => s.Number == number);
            driver.Place(1, world.GetPosition(segment.Body) + new Vec2(0f, 3f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step();
            return segment.Id;
        }

        [Test]
        public void MissileTakesDamageWithoutTeleporting()
        {
            var missile = new BulletDefinition(0.2f, 15f, 30f, 0f, hitPoints: 1000f);
            var driver = CannonTests.Duel(Shooter(), Shooter(missile));
            driver.Place(101, new Vec2(58f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(1, systems: 1);
            driver.Control(101, systems: 1);
            for (var tick = 0; tick < 20; ++tick)
            {
                driver.Step();
                var bullets = driver.Battle.Bullets;
                for (var i = 0; i < bullets.Capacity; ++i)
                    if (bullets.IsAlive(i) && bullets[i].Definition == missile && bullets[i].HitPoints < 1000f)
                    {
                        Assert.AreEqual(950f, bullets[i].HitPoints);
                        Assert.Less(driver.Battle.World.GetBulletPosition(bullets[i].Body).X, 58f);
                        Assert.Greater(driver.Battle.World.GetBulletPosition(bullets[i].Body).X, 50f);
                        return;
                    }
            }
            Assert.Fail("The missile never touched the rift.");
        }

        [Test]
        public void ChargeDoesNotScaleTeleportDistance()
        {
            var rift = Rift().WithCharge(0.5f);
            Assert.AreEqual(100f, rift.Effects.Teleport);
            Assert.Less(rift.Damage, 50f);
        }

        [Test]
        public void RemoteOwnerAppliesTeleportOnceAndCorrectionsNeverTurnItIntoDamage()
        {
            var pair = new NetPair(Shooter(), Target());
            foreach (var driver in new[] { pair.A, pair.B })
            {
                driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(101, new Vec2(53f, 50f), Vec2.Zero);
            }
            pair.HoldHitsFromB = true;
            pair.B.Control(1, systems: 1);
            pair.Step(3);
            var teleport = pair.HeldHits.Single(h => h.Effect == HitEffect.Teleport);
            Assert.AreEqual(100f, teleport.Amount);
            Assert.AreEqual(HitImpactKind.Energy, teleport.ImpactKind);
            Near(new Vec2(53f, 50f), pair.B.Battle.World.GetPosition(pair.B.Ship(101).Body));
            Near(new Vec2(53f, 50f), pair.A.Battle.World.GetPosition(pair.A.Ship(101).Body));
            Assert.AreEqual(1000f, pair.A.Ship(101).Armor.Value);
            pair.Correct(pair.A, pair.B);
            pair.Step();
            Assert.AreEqual(950f, pair.B.Ship(101).Armor.Value, Tolerance);
            pair.ReleaseHits();
            Assert.IsFalse(pair.A.Battle.Submit(new InboundHit(NetPair.OwnerB, teleport)), "Duplicate delivery.");
            pair.HoldHitsFromB = false;
            pair.Step(2);
            Near(new Vec2(153f, 50f), pair.A.Battle.World.GetPosition(pair.A.Ship(101).Body));
            Assert.AreEqual(950f, pair.A.Ship(101).Armor.Value, Tolerance);
            pair.Correct(pair.A, pair.B);
            pair.Step();
            Near(new Vec2(153f, 50f), pair.B.Battle.World.GetPosition(pair.B.Ship(101).Body));
            Assert.AreEqual(950f, pair.B.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(0, pair.B.Battle.PredictedHitCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemoteSegmentDestructionCountsIfEitherDeviceSeesTheContact(bool ownerSeesContact)
        {
            var pair = new NetPair(Shooter(Rift(radius: 0.05f), new Vec2(0f, -3f)), Target(worm: true));
            foreach (var driver in new[] { pair.A, pair.B })
                driver.Battle.World.TeleportGroup(driver.Ship(101).Body, new Vec2(80f, 50f), 0f);
            pair.Step(30);
            var segment = pair.B.Battle.Snapshot.Segments.ToArray().Select(s => pair.B.Battle.FindSegment(s.Id))
                .Single(s => s.Number == 4);
            var segmentId = segment.Id;
            var muzzle = pair.B.Battle.World.GetPosition(segment.Body);
            foreach (var driver in new[] { pair.A, pair.B })
                driver.Place(1, muzzle + new Vec2(0f, 3f), Vec2.Zero);
            if (!ownerSeesContact)
                pair.A.Battle.World.TeleportGroup(pair.A.Ship(101).Body, new Vec2(120f, 100f));
            pair.HoldHitsFromB = true;
            pair.B.Control(1, systems: 1);
            pair.Step(3);
            var hit = pair.HeldHits.Single(h => h.Effect == HitEffect.Teleport);
            Assert.IsTrue(hit.TargetIsBullet);
            Assert.AreEqual(segmentId, hit.TargetId);
            Assert.AreEqual(ownerSeesContact, pair.A.Battle.FindSegment(segmentId) == null);
            pair.ReleaseHits();
            pair.HoldHitsFromB = false;
            pair.Step(2);
            Assert.IsNull(pair.A.Battle.FindSegment(segmentId));
            Assert.IsNull(pair.B.Battle.FindSegment(segmentId));
            Assert.AreEqual(3, pair.A.Battle.Snapshot.Segments.ToArray().Count(s => !s.Detached));
            Assert.AreEqual(6, pair.A.Battle.Snapshot.Segments.ToArray().Count(s => s.Detached));
        }
    }
}
