using System;
using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class BulletTests
    {
        private const float Dt = 0.02f;

        private const ulong Hull = 1;
        private const ulong Shield = 2;
        private const ulong Projectile = 4;

        private static readonly ShapeDef HullDef = new ShapeDef { Filter = new Filter(Hull, 0) };
        private static readonly ShapeDef ShieldDef = new ShapeDef { Filter = new Filter(Shield, 0) };

        [Test]
        public void FastBulletDoesNotTunnelThroughAThinPlate()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var plate = world.CreateBody(BodyDef.Create(new Vec2(100f, 100f), 1f, BodyFlags.FixedPosition));
            world.AddPolygon(plate, Polygon.Box(0.01f, 2f));
            var bullet = world.CreateBullet(new BulletDef { Position = new Vec2(92f, 100.5f), LinearVelocity = new Vec2(1000f, 0f) },
                new Circle(Vec2.Zero, 0.2f));

            world.Step(Dt);

            Assert.AreEqual(1, world.BulletHits.Length);
            var hit = world.BulletHits[0];
            Assert.AreEqual(bullet, hit.Bullet);
            Assert.AreEqual(plate, hit.Target.Body);
            Assert.AreEqual((99.99f - 0.2f - 92f) / 20f, hit.Fraction, 1e-4f);
            AssertVec(new Vec2(99.99f, 100.5f), hit.Point, 1e-3f);
            AssertVec(new Vec2(-1f, 0f), hit.Normal, 1e-4f);
            Assert.IsTrue(hit.Begin);
            AssertVec(new Vec2(112f, 100.5f), world.GetBulletPosition(bullet), 1e-3f);
        }

        [Test]
        public void ShieldStopsABulletBeforeTheHull()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var ship = CreateShieldedShip(world, new Vec2(100f, 100f), out var shield);
            world.CreateBullet(new BulletDef { Position = new Vec2(90f, 100f), LinearVelocity = new Vec2(600f, 0f) }, new Circle(Vec2.Zero, 0.1f));

            world.Step(Dt);

            Assert.AreEqual(1, world.BulletHits.Length);
            Assert.AreEqual(shield, world.BulletHits[0].Target.Shape);
            Assert.AreEqual(ship, world.BulletHits[0].Target.Body);
        }

        [Test]
        public void PassThroughBulletHitsShieldThenHullOnce()
        {
            var world = new PhysicsWorld(new WorldSettings());
            CreateShieldedShip(world, new Vec2(100f, 100f), out var shield);
            world.CreateBullet(new BulletDef { Position = new Vec2(90f, 100f), LinearVelocity = new Vec2(1000f, 0f), PassThrough = true },
                new Circle(Vec2.Zero, 0.1f));

            world.Step(Dt);

            // The hull has 2 parts; the bullet crosses both, but the hull counts once, at its outer entry.
            Assert.AreEqual(2, world.BulletHits.Length);
            Assert.AreEqual(shield, world.BulletHits[0].Target.Shape);
            Assert.AreNotEqual(shield, world.BulletHits[1].Target.Shape);
            Assert.Less(world.BulletHits[0].Fraction, world.BulletHits[1].Fraction);
            Assert.AreEqual(98.9f, world.BulletHits[1].Point.X, 1e-3f);
        }

        [Test]
        public void PiercingBulletSkipsTheShield()
        {
            var world = new PhysicsWorld(new WorldSettings());
            CreateShieldedShip(world, new Vec2(100f, 100f), out var shield);
            world.CreateBullet(new BulletDef
            {
                Position = new Vec2(90f, 100f),
                LinearVelocity = new Vec2(600f, 0f),
                Filter = new Filter(Projectile, Shield),
            }, new Circle(Vec2.Zero, 0.1f));

            world.Step(Dt);

            Assert.AreEqual(1, world.BulletHits.Length);
            Assert.AreNotEqual(shield, world.BulletHits[0].Target.Shape);
        }

        [Test]
        public void BulletInsideAnEnemyShieldHitsAtTimeZeroButNeverItsOwnShip()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var owner = world.CreateBody(BodyDef.Create(new Vec2(100f, 100f), 1f));
            world.AddCircle(owner, new Circle(Vec2.Zero, 1f));
            CreateShieldedShip(world, new Vec2(101.5f, 100f), out var shield);
            world.CreateBullet(new BulletDef { Position = new Vec2(100.2f, 100f), LinearVelocity = new Vec2(-10f, 0f), Owner = owner },
                new Circle(Vec2.Zero, 0.1f));

            world.Step(Dt);

            Assert.AreEqual(1, world.BulletHits.Length);
            Assert.AreEqual(shield, world.BulletHits[0].Target.Shape);
            Assert.AreEqual(0f, world.BulletHits[0].Fraction);
        }

        [Test]
        public void BulletsHitEachOtherOnce()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = world.CreateBullet(new BulletDef { Position = new Vec2(95f, 100f), LinearVelocity = new Vec2(240f, 0f) }, new Circle(Vec2.Zero, 0.2f));
            var b = world.CreateBullet(new BulletDef { Position = new Vec2(105f, 100.1f), LinearVelocity = new Vec2(-240f, 0f) },
                Polygon.Box(0.3f, 0.1f));

            world.Step(Dt);

            Assert.AreEqual(2, world.BulletHits.Length);
            var hitA = world.BulletHits[0].Bullet.Equals(a) ? world.BulletHits[0] : world.BulletHits[1];
            var hitB = world.BulletHits[0].Bullet.Equals(a) ? world.BulletHits[1] : world.BulletHits[0];
            Assert.AreEqual(b, hitA.Target.Bullet);
            Assert.AreEqual(a, hitB.Target.Bullet);
            Assert.AreEqual((10f - 0.5f) / 9.6f, hitA.Fraction, 1e-4f);
            AssertVec(-hitA.Normal, hitB.Normal, 1e-6f);
        }

        [Test]
        public void BulletHitsAcrossTheSeam()
        {
            var world = new PhysicsWorld(new WorldSettings { Width = 50f, Height = 50f });
            var target = world.CreateBody(BodyDef.Create(new Vec2(0.5f, 25f), 1f));
            world.AddCircle(target, new Circle(Vec2.Zero, 0.4f));
            world.CreateBullet(new BulletDef { Position = new Vec2(47f, 25f), LinearVelocity = new Vec2(240f, 0f) }, new Circle(Vec2.Zero, 0.1f));

            world.Step(Dt);

            Assert.AreEqual(1, world.BulletHits.Length);
            Assert.AreEqual((3.5f - 0.5f) / 4.8f, world.BulletHits[0].Fraction, 1e-4f);
            AssertVec(new Vec2(0.1f, 25f), world.BulletHits[0].Point, 1e-3f);
        }

        [Test]
        public void BeginOnlyInTheFirstStepOfAHit()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var target = world.CreateBody(BodyDef.Create(new Vec2(100f, 100f), 1f, BodyFlags.FixedPosition));
            world.AddCircle(target, new Circle(Vec2.Zero, 3f));
            world.CreateBullet(new BulletDef { Position = new Vec2(96.5f, 100f), LinearVelocity = new Vec2(50f, 0f), PassThrough = true },
                new Circle(Vec2.Zero, 0.1f));

            world.Step(Dt);
            Assert.AreEqual(1, world.BulletHits.Length);
            Assert.IsTrue(world.BulletHits[0].Begin);

            world.Step(Dt);
            Assert.AreEqual(1, world.BulletHits.Length);
            Assert.IsFalse(world.BulletHits[0].Begin);
            Assert.AreEqual(0f, world.BulletHits[0].Fraction);
        }

        [Test]
        public void HitOnAMovingTargetUsesItsMotion()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var target = world.CreateBody(new BodyDef { Position = new Vec2(100f, 100f), Mass = 1f, LinearVelocity = new Vec2(-40f, 0f) });
            world.AddPolygon(target, Polygon.Box(0.5f, 0.5f));
            world.CreateBullet(new BulletDef { Position = new Vec2(97f, 100f), LinearVelocity = new Vec2(200f, 0f) }, new Circle(Vec2.Zero, 0.1f));

            world.Step(Dt);

            // Gap 2.4 closes at 240 u/s: 0.01 s, half the step. The face is then at 99.1.
            Assert.AreEqual(1, world.BulletHits.Length);
            Assert.AreEqual(0.5f, world.BulletHits[0].Fraction, 1e-4f);
            AssertVec(new Vec2(99.1f, 100f), world.BulletHits[0].Point, 1e-3f);
        }

        [Test]
        public void FilterRemovesSolidContacts()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = world.CreateBody(BodyDef.Create(new Vec2(100f, 100f), 1f));
            world.AddCircle(a, new Circle(Vec2.Zero, 0.5f), new ShapeDef { Filter = new Filter(1, 2) });
            var b = world.CreateBody(BodyDef.Create(new Vec2(100.6f, 100f), 1f));
            var shape = world.AddCircle(b, new Circle(Vec2.Zero, 0.5f), new ShapeDef { Filter = new Filter(2, 0) });

            world.Step(Dt);
            Assert.AreEqual(0, world.ContactCount);
            AssertVec(new Vec2(100f, 100f), world.GetPosition(a), 0f);

            world.SetShapeFilter(shape, new Filter(4, 0));
            world.Step(Dt);
            Assert.AreEqual(1, world.ContactCount);
        }

        // Hull: two boxes side by side, [-1.1, 1.1] × [-0.5, 0.5]. Shield: circle of radius 2.
        internal static BodyId CreateShieldedShip(PhysicsWorld world, Vec2 position, out ShapeId shield)
        {
            var ship = world.CreateBody(BodyDef.Create(position, 1f));
            world.AddPolygon(ship, Polygon.Box(0.55f, 0.5f, new Vec2(-0.55f, 0f), 0f), HullDef);
            world.AddPolygon(ship, Polygon.Box(0.55f, 0.5f, new Vec2(0.55f, 0f), 0f), HullDef);
            shield = world.AddCircle(ship, new Circle(Vec2.Zero, 2f), ShieldDef);
            return ship;
        }
    }
}
