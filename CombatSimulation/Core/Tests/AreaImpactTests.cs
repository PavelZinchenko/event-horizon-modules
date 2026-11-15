using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 3: HitAllTargets and DamageOverTime bullets, falloff and radial push (E6), and bullets attached to
    // their ship. The shooter stands at (50, 50); its muzzle and every bullet here start at (51, 50) without velocity.
    public class AreaImpactTests
    {
        private const float Tolerance = 1e-3f;

        private static readonly Vec2 Muzzle = new Vec2(51f, 50f);

        // 0.3 s: 16 ticks.
        private static BulletDefinition Area(float radius = 3f, float damage = 10f, bool falloff = false,
            float radialPush = 0f, float radialPushWithFalloff = 0f, float scale = 0f) =>
            new BulletDefinition(radius, 0f, 0f, damage, impact: BulletImpact.AllTargets, damageFalloff: falloff,
                radialPush: radialPush, radialPushWithFalloff: radialPushWithFalloff, lifetime: 0.3f, scale: scale);

        // 10 per second for 0.2 s: 11 ticks.
        private static BulletDefinition Cloud(float radius = 3f) =>
            new BulletDefinition(radius, 0f, 0f, 10f, impact: BulletImpact.OverTime, lifetime: 0.2f);

        // No shield, no resistances; mass 2.
        private static ShipDefinition Target(IReadOnlyList<Circle> extraHullParts = null,
            IReadOnlyList<DeviceDefinition> devices = null) =>
            Fixtures.ShipWith(mass: 2f, armor: 200f, shield: new ResourceDefinition(0f), extraHullParts: extraHullParts,
                devices: devices);

        private static Driver Fire(BulletDefinition bullet, Vec2 target, ShipDefinition targetShip = null)
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(bullet, cooldown: 10f, energyCost: 0f), targetShip ?? Target());
            driver.Place(101, target, Vec2.Zero);
            driver.Control(1, systems: 1);
            return driver;
        }

        private static List<BattleEvent> Hits(IEnumerable<BattleEvent> events) =>
            events.Where(e => e.Kind == BattleEventKind.Hit).ToList();

        [Test]
        public void AnAreaHitsATargetOnceWhileItStaysInside()
        {
            var driver = Fire(Area(), new Vec2(53f, 50f));
            var events = driver.StepCollecting(30);

            Assert.AreEqual(1, Hits(events).Count);
            Assert.AreEqual(10f, Hits(events)[0].Damage, Tolerance);
            Assert.AreEqual(190f, driver.Ship(101).Armor.Value, Tolerance);
            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason, "An area does not stop at a hit.");
        }

        [Test]
        public void AnAreaHitsATargetThatEntersLater()
        {
            var driver = Fire(Area(), new Vec2(60f, 50f));
            driver.StepCollecting(4);
            Assert.IsTrue(driver.Battle.Snapshot.Bullets.Length == 1, "The area is alive.");

            driver.Place(101, new Vec2(53f, 50f), Vec2.Zero);
            var hits = Hits(driver.StepCollecting(20));
            Assert.AreEqual(1, hits.Count);
        }

        [Test]
        public void FalloffScalesTheDamageAndTheRadialPush()
        {
            // Legacy GetFalloff: R = scale / 2 = 5, target scale 2 (hull radius 1), distance 4: (5 + 2 - 4) / 5 = 0.6.
            var driver = Fire(Area(radius: 5f, falloff: true, radialPushWithFalloff: 4f, scale: 10f), new Vec2(55f, 50f));
            var hits = Hits(driver.StepCollecting(10));

            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(6f, hits[0].Damage, Tolerance);
            Assert.AreEqual(194f, driver.Ship(101).Armor.Value, Tolerance);
            // 4 x 0.6 away from the centre on mass 2; the line passes through the origin, so no spin.
            var body = driver.Ship(101).Body;
            Assert.AreEqual(1.2f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Y, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(body), Tolerance);
        }

        [Test]
        public void FalloffIsZeroWhenTheTargetEdgeLeavesTheRadius()
        {
            var definition = Area(radius: 5f, falloff: true, scale: 10f);
            Assert.AreEqual(1f, definition.Falloff(0f, 2f));
            Assert.AreEqual(0.6f, definition.Falloff(4f, 2f), Tolerance);
            Assert.AreEqual(0f, definition.Falloff(7f, 2f));
            Assert.AreEqual(0f, definition.Falloff(9f, 2f));
        }

        [Test]
        public void PlainDamageAndPullIgnoreTheDistance()
        {
            // PullToCenter 2 on mass 2: 1 u/s toward the centre.
            var driver = Fire(Area(radius: 5f, radialPush: -2f, scale: 10f), new Vec2(55f, 50f));
            var hits = Hits(driver.StepCollecting(10));

            Assert.AreEqual(10f, hits.Single().Damage, Tolerance);
            Assert.AreEqual(-1f, driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body).X, Tolerance);
        }

        [Test]
        public void DamageOverTimeDealsOneTickPerEntityAndStep()
        {
            // Two hull circles inside the cloud: one contact per step (D16).
            var hull = new[] { new Circle(new Vec2(0f, 0.5f), 0.5f) };
            var driver = Fire(Cloud(), new Vec2(53f, 50f), Target(hull));
            var events = driver.StepCollecting(30);

            var hits = Hits(events);
            var ticks = Cloud().LifetimeTicks;
            Assert.AreEqual(ticks, hits.Count);
            Assert.AreEqual(ticks, hits.Select(h => h.Tick).Distinct().Count());
            foreach (var hit in hits) Assert.AreEqual(0.2f, hit.Damage, Tolerance);
            Assert.AreEqual(200f - 0.2f * ticks, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(RemovalReason.Expiry, CannonTests.BulletRemovals(events).Single().RemovalReason);
        }

        [Test]
        public void PointDefenseIgnoresAnArea()
        {
            var defense = new PointDefenseDefinition(10f, 0f, 0f);
            var driver = Fire(Cloud(), new Vec2(53f, 50f), Target(devices: new DeviceDefinition[] { defense }));
            driver.Control(101, systems: 1);
            var events = driver.StepCollecting(30);

            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.Disarm));
            Assert.AreEqual(RemovalReason.Expiry, CannonTests.BulletRemovals(events).Single().RemovalReason);
        }

        [Test]
        public void ARemoteTargetGetsOneDamageOverTimeInterval()
        {
            var p = new NetPair(Fixtures.CannonShip(Cloud(), cooldown: 10f, energyCost: 0f), Target());
            foreach (var driver in new[] { p.A, p.B })
            {
                driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(101, new Vec2(53f, 50f), Vec2.Zero);
            }
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            p.Step(30);

            var records = p.HeldHits.Where(h => !h.TargetIsBullet).ToList();
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(HitContactState.Start, records[0].ContactState);
            Assert.AreEqual(HitContactState.End, records[1].ContactState);
            foreach (var record in records)
            {
                Assert.AreEqual(HitImpactKind.DamageOverTime, record.ImpactKind);
                Assert.AreEqual(10f, record.Amount, Tolerance, "The rate per second.");
                Assert.AreEqual(0, record.TargetShape);
            }
            Assert.AreEqual(Cloud().LifetimeTicks, records[1].Tick - records[0].Tick);

            // The owner catches up the whole interval (N7).
            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step();
            Assert.AreEqual(200f - 0.2f * Cloud().LifetimeTicks, p.A.Ship(101).Armor.Value, Tolerance);
        }

        [Test]
        public void ARemoteTargetGetsTheFalloffDamageAndPush()
        {
            var bullet = Area(radius: 5f, falloff: true, radialPushWithFalloff: 4f, scale: 10f);
            var p = new NetPair(Fixtures.CannonShip(bullet, cooldown: 10f, energyCost: 0f), Target());
            foreach (var driver in new[] { p.A, p.B })
            {
                driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            }
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            for (var i = 0; i < 20 && p.HeldHits.Count == 0; ++i) p.Step();

            var record = p.HeldHits.Single();
            Assert.AreEqual(6f, record.Amount, Tolerance);
            Assert.AreEqual(2.4f, record.Impulse.X, Tolerance);
            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step();
            Assert.AreEqual(194f, p.A.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(1.2f, p.A.Battle.World.GetLinearVelocity(p.A.Ship(101).Body).X, Tolerance);
        }

        [Test]
        public void AnAttachedBulletRidesOnTheBarrelAndLeavesWithItsShipWithoutTriggers()
        {
            var child = new BulletDefinition(0.1f, 10f, 2f, 1f);
            var carrier = new BulletDefinition(0.5f, 0f, 0f, 0f, lifetime: 2f, attachedToParent: true,
                triggers: new[]
                {
                    BulletTriggerDefinition.Spawn(BulletTriggerConditions.Expired | BulletTriggerConditions.Disarmed |
                        BulletTriggerConditions.Destroyed, child),
                });
            var driver = Fire(carrier, new Vec2(150f, 150f));
            driver.StepCollecting(3);
            var id = driver.Battle.Snapshot.Bullets[0].Id;

            driver.Place(1, new Vec2(50f, 50f), new Vec2(5f, 0f), (float)(Math.PI / 2.0));
            driver.Control(1, systems: 0);
            driver.Step(5);
            var ship = driver.Battle.Snapshot.Ships.ToArray().Single(s => s.Id == driver.Entity(1));
            var bullet = driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.Id == id);
            // The barrel at (1, 0) turns with the ship.
            Assert.AreEqual(ship.Position.X, bullet.Position.X, Tolerance);
            Assert.AreEqual(ship.Position.Y + 1f, bullet.Position.Y, Tolerance);

            driver.Kill(1);
            var events = driver.StepCollecting();
            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(id, removal.EntityId);
            Assert.AreEqual(RemovalReason.Disposal, removal.RemovalReason);
            Assert.AreEqual(0, CannonTests.BulletSpawns(events).Count, "No trigger runs.");
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
        }

        [Test]
        public void TheRemoteCopyOfAnAttachedBulletRidesOnTheShipCopy()
        {
            var carrier = new BulletDefinition(0.5f, 0f, 0f, 0f, lifetime: 2f, attachedToParent: true);
            var p = new NetPair(Fixtures.CannonShip(carrier, cooldown: 10f, energyCost: 0f), Target());
            p.B.Control(1, systems: 1);
            p.Step(4);
            p.B.Control(1, systems: 0);
            p.B.Place(1, new Vec2(100f, 150f), new Vec2(5f, 0f));
            p.Step(10);

            var owner = p.B.Battle.Snapshot.Bullets[0];
            var copy = p.A.Battle.Snapshot.Bullets.ToArray().Single(b => b.Id == owner.Id);
            var ship = p.A.Battle.Snapshot.Ships.ToArray().Single(s => s.Id == p.A.Entity(1));
            Assert.AreEqual(ship.Position.X + 1f, copy.Position.X, Tolerance);
            Assert.AreEqual(ship.Position.Y, copy.Position.Y, Tolerance);
        }

        [Test]
        public void InvalidCombinationsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new BulletDefinition(1f, 0f, 0f, 1f));
            Assert.Throws<ArgumentException>(() =>
                new BulletDefinition(1f, 0f, 0f, 1f, push: 1f, impact: BulletImpact.OverTime, lifetime: 1f));
            Assert.Throws<ArgumentException>(() =>
                new BulletDefinition(1f, 0f, 0f, 1f, damageFalloff: true, impact: BulletImpact.OverTime, lifetime: 1f));
            Assert.Throws<ArgumentException>(() => new BulletDefinition(1f, 0f, 0f, 1f, impact: BulletImpact.OverTime,
                lifetime: 1f, extraDamage: new[] { new DamageAmount(1, 1f) }));
            var attached = new BulletDefinition(1f, 0f, 0f, 1f, lifetime: 1f, attachedToParent: true);
            Assert.Throws<ArgumentException>(() =>
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Created, attached));
        }
    }
}
