using System;
using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class DistanceTests
    {
        [Test]
        public void CirclesGiveTheGapBetweenTheSurfaces()
        {
            var a = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 1f));
            var b = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.5f));

            var distance = Distance.Shapes(a, Pose(0f, 0f), b, Pose(3f, 0f), out var pointA, out var pointB);

            Assert.AreEqual(1.5f, distance, 1e-6f);
            AssertVec(new Vec2(1f, 0f), pointA);
            AssertVec(new Vec2(2.5f, 0f), pointB);
        }

        [Test]
        public void BoxesFaceToFace()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));

            var distance = Distance.Shapes(box, Pose(0f, 0f), box, Pose(2f, 0.3f), out var pointA, out var pointB);

            Assert.AreEqual(1f, distance, 1e-5f);
            Assert.AreEqual(0.5f, pointA.X, 1e-5f);
            Assert.AreEqual(1.5f, pointB.X, 1e-5f);
        }

        [Test]
        public void TurnedBoxCornerToFace()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));
            var turned = new Pose(new Vec2(3f, 0f), Rot.FromAngle((float)(Math.PI / 4.0)));

            var distance = Distance.Shapes(box, Pose(0f, 0f), box, turned, out _, out var pointB);

            Assert.AreEqual(2.5f - (float)Math.Sqrt(0.5), distance, 1e-5f);
            AssertVec(new Vec2(3f - (float)Math.Sqrt(0.5), 0f), pointB, 1e-5f);
        }

        [Test]
        public void BoxCornerToCircle()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(1f, 1f));
            var circle = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.5f));

            var distance = Distance.Shapes(box, Pose(0f, 0f), circle, Pose(2f, 2f), out var pointA, out _);

            Assert.AreEqual((float)Math.Sqrt(2.0) - 0.5f, distance, 1e-5f);
            AssertVec(new Vec2(1f, 1f), pointA, 1e-5f);
        }

        [Test]
        public void CapsuleSideToCircle()
        {
            var capsule = DistanceProxy.FromPolygon(new Capsule(new Vec2(-1f, 0f), new Vec2(1f, 0f), 0.25f).Core);
            var circle = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.5f));

            var distance = Distance.Shapes(capsule, Pose(0f, 0f), circle, Pose(0.3f, 2f), out var pointA, out var pointB);

            Assert.AreEqual(1.25f, distance, 1e-5f);
            AssertVec(new Vec2(0.3f, 0.25f), pointA, 1e-5f);
            AssertVec(new Vec2(0.3f, 1.5f), pointB, 1e-5f);
        }

        [Test]
        public void OverlapGivesZero()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(1f, 1f));
            var circle = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.5f));

            Assert.AreEqual(0f, Distance.Shapes(box, Pose(0f, 0f), circle, Pose(0.2f, 0.1f), out _, out _));
            Assert.AreEqual(0f, Distance.Shapes(box, Pose(0f, 0f), box, Pose(1.5f, 0.5f), out _, out _));
        }

        private static Pose Pose(float x, float y) => new Pose(new Vec2(x, y), Rot.Identity);
    }
}
