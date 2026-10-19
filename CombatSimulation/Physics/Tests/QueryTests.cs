using System;
using System.Collections.Generic;
using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class QueryTests
    {
        private static readonly Filter All = default;

        [Test]
        public void RayHitsAConcaveHullOnceAtItsOuterEntry()
        {
            // U shape: the ray along y = 1 enters the left arm, crosses the gap and the right arm; the ray along
            // y = -0.5 crosses the cuts between the parts of the base. Each is one hull, so one hit.
            var outline = new List<Vec2>
            {
                new Vec2(-3f, -1f), new Vec2(3f, -1f), new Vec2(3f, 2f), new Vec2(2f, 2f), new Vec2(2f, 0f),
                new Vec2(-2f, 0f), new Vec2(-2f, 2f), new Vec2(-3f, 2f),
            };
            var world = new PhysicsWorld(new WorldSettings());
            var hull = world.CreateBody(BodyDef.Create(new Vec2(100f, 100f), 1f));
            var parts = ConvexDecomposition.Decompose(outline);
            ConvexDecomposition.AttachOutline(parts, outline);
            foreach (var part in parts) world.AddPolygon(hull, part);
            Assert.Greater(parts.Count, 1);

            var hits = new RayHit[8];
            foreach (var y in new[] { 101f, 99.5f })
            {
                var count = world.RayCast(new Vec2(90f, y), new Vec2(20f, 0f), All, default, true, hits, out var overflow);

                Assert.AreEqual(1, count, $"y = {y}");
                Assert.IsFalse(overflow);
                Assert.AreEqual(hull, hits[0].Target.Body);
                AssertVec(new Vec2(97f, y), hits[0].Point, 1e-4f);
                AssertVec(new Vec2(-1f, 0f), hits[0].Normal, 1e-4f);
            }

            // From inside the hull: one hit at the start.
            Assert.AreEqual(1, world.RayCast(new Vec2(100f, 99.5f), new Vec2(20f, 0f), All, default, true, hits, out _));
            Assert.AreEqual(0f, hits[0].Fraction);
        }

        [Test]
        public void PassThroughRayKeepsTheNearestHitsAndFlagsOverflow()
        {
            var world = new PhysicsWorld(new WorldSettings());
            for (var i = 0; i < 10; ++i)
            {
                var body = world.CreateBody(BodyDef.Create(new Vec2(110f + 5f * i, 100f), 1f));
                world.AddCircle(body, new Circle(Vec2.Zero, 1f));
            }

            var hits = new RayHit[8];
            var count = world.RayCast(new Vec2(100f, 100f), new Vec2(80f, 0f), All, default, true, hits, out var overflow);
            Assert.AreEqual(8, count);
            Assert.IsTrue(overflow);
            for (var i = 0; i < count; ++i) Assert.AreEqual(109f + 5f * i, hits[i].Point.X, 1e-3f);

            count = world.RayCast(new Vec2(100f, 100f), new Vec2(80f, 0f), All, default, false, hits, out overflow);
            Assert.AreEqual(1, count);
            Assert.IsFalse(overflow);
            Assert.AreEqual(109f, hits[0].Point.X, 1e-3f);
        }

        [Test]
        public void LongRayIsClippedAndHitsEachTargetOnceAtItsNearestImage()
        {
            var world = new PhysicsWorld(new WorldSettings { Width = 50f, Height = 50f });
            var shooter = world.CreateBody(BodyDef.Create(new Vec2(10f, 25f), 1f));
            world.AddCircle(shooter, new Circle(Vec2.Zero, 1f));
            var target = world.CreateBody(BodyDef.Create(new Vec2(40f, 25f), 1f));
            world.AddCircle(target, new Circle(Vec2.Zero, 1f));

            // Length 300 is clipped to 50: the ray from 11 reaches 61 (11 across the wrap). It meets the target at 39
            // once; the shooter's image at 59 is ignored.
            var hits = new RayHit[8];
            var count = world.RayCast(new Vec2(11f, 25f), new Vec2(300f, 0f), All, shooter, true, hits, out _);
            Assert.AreEqual(1, count);
            Assert.AreEqual(target, hits[0].Target.Body);
            Assert.AreEqual(28f / 50f, hits[0].Fraction, 1e-4f);

            // Backwards across the wrap: the nearest image of the target is at -10.
            count = world.RayCast(new Vec2(9f, 25f), new Vec2(-300f, 0f), All, shooter, true, hits, out _);
            Assert.AreEqual(1, count);
            Assert.AreEqual(18f / 50f, hits[0].Fraction, 1e-4f);
            AssertVec(new Vec2(41f, 25f), hits[0].Point, 1e-3f);
        }

        [Test]
        public void RayAcrossTheCornerSeamHitsATargetOnTheOtherSide()
        {
            var world = new PhysicsWorld(new WorldSettings { Width = 50f, Height = 50f });
            var target = world.CreateBody(BodyDef.Create(new Vec2(2f, 2f), 1f));
            world.AddPolygon(target, Polygon.Box(0.5f, 0.5f));

            var hits = new RayHit[8];
            var count = world.RayCast(new Vec2(47f, 47f), new Vec2(10f, 10f), All, default, false, hits, out _);
            Assert.AreEqual(1, count);
            AssertVec(new Vec2(1.5f, 1.5f), hits[0].Point, 1e-3f);
        }

        [Test]
        public void RayFilterSkipsIgnoredCategories()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var ship = BulletTests.CreateShieldedShip(world, new Vec2(100f, 100f), out var shield);

            var hits = new RayHit[8];
            var count = world.RayCast(new Vec2(90f, 100f), new Vec2(20f, 0f), new Filter(0, 2), default, false, hits, out _);
            Assert.AreEqual(1, count);
            Assert.AreEqual(ship, hits[0].Target.Body);
            Assert.AreNotEqual(shield, hits[0].Target.Shape);
            Assert.AreEqual(98.9f, hits[0].Point.X, 1e-3f);
        }

        [Test]
        public void RaysAndOverlapsSeeBullets()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var missile = world.CreateBullet(new BulletDef { Position = new Vec2(105f, 100f) }, new Circle(Vec2.Zero, 0.3f));

            var hits = new RayHit[8];
            Assert.AreEqual(1, world.RayCast(new Vec2(100f, 100f), new Vec2(10f, 0f), All, default, false, hits, out _));
            Assert.AreEqual(missile, hits[0].Target.Bullet);

            var results = new OverlapHit[32];
            Assert.AreEqual(1, world.OverlapCircle(new Vec2(100f, 100f), 5f, All, results, out _));
            Assert.AreEqual(4.7f, results[0].Distance, 1e-4f);

            world.SetBulletPose(missile, new Vec2(120f, 100f), 0f);
            Assert.AreEqual(0, world.OverlapCircle(new Vec2(100f, 100f), 5f, All, results, out _));
        }

        [Test]
        public void OverlapTestsPartsAcrossTheSeamAndKeepsTheNearest()
        {
            var world = new PhysicsWorld(new WorldSettings { Width = 50f, Height = 50f });

            // An L-shaped hull of two parts.
            var hull = world.CreateBody(BodyDef.Create(new Vec2(2f, 25f), 1f));
            world.AddPolygon(hull, Polygon.Box(2f, 0.25f, new Vec2(0f, 2f), 0f));
            world.AddPolygon(hull, Polygon.Box(0.25f, 2f, new Vec2(2f, 0f), 0f));

            // Overlap centre across the wrap at x = 49: the nearest part edge is the vertical bar's left side at x = 3.75,
            // 4.75 away; the horizontal bar is 2.06 away.
            var results = new OverlapHit[32];
            Assert.AreEqual(0, world.OverlapCircle(new Vec2(49f, 25f), 1.5f, All, results, out _));
            Assert.AreEqual(1, world.OverlapCircle(new Vec2(49f, 25f), 2.5f, All, results, out _));
            Assert.AreEqual((float)Math.Sqrt(1f + 1.75f * 1.75f), results[0].Distance, 1e-3f);

            for (var i = 0; i < 40; ++i)
            {
                var drone = world.CreateBody(BodyDef.Create(new Vec2(20f + (i % 8), 20f + i / 8), 1f));
                world.AddCircle(drone, new Circle(Vec2.Zero, 0.2f));
            }

            var count = world.OverlapCircle(new Vec2(23.5f, 22f), 10f, All, results, out var overflow);
            Assert.AreEqual(32, count);
            Assert.IsTrue(overflow);
            for (var i = 1; i < count; ++i) Assert.LessOrEqual(results[i - 1].Distance, results[i].Distance);
        }
    }
}
