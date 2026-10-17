using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class CollideTests
    {
        [Test]
        public void OverlappingCircles()
        {
            Collide.Circles(new Circle(Vec2.Zero, 1f), Pose(0f, 0f), new Circle(Vec2.Zero, 1f), Pose(1.5f, 0f), 0f, out var m);

            Assert.AreEqual(1, m.PointCount);
            AssertVec(new Vec2(1f, 0f), m.Normal);
            Assert.AreEqual(-0.5f, m.Point0.Separation, 1e-6f);
            AssertVec(new Vec2(0.75f, 0f), m.Point0.Point);
        }

        [Test]
        public void SpeculativePointAppearsOnlyWithinMaxDistance()
        {
            var a = new Circle(Vec2.Zero, 1f);
            var b = new Circle(Vec2.Zero, 1f);

            Collide.Circles(a, Pose(0f, 0f), b, Pose(2.4f, 0f), 0.5f, out var near);
            Assert.AreEqual(1, near.PointCount);
            Assert.AreEqual(0.4f, near.Point0.Separation, 1e-5f);

            Collide.Circles(a, Pose(0f, 0f), b, Pose(2.6f, 0f), 0.5f, out var far);
            Assert.AreEqual(0, far.PointCount);
        }

        [Test]
        public void CirclesWithTheSameCentreUseTheFallbackNormal()
        {
            Collide.Circles(new Circle(Vec2.Zero, 1f), Pose(5f, 5f), new Circle(Vec2.Zero, 0.5f), Pose(5f, 5f), 0f, out var m);
            Assert.AreEqual(1, m.PointCount);
            AssertVec(Collide.FallbackNormal, m.Normal);
            Assert.AreEqual(-1.5f, m.Point0.Separation, 1e-6f);
        }

        [Test]
        public void CircleAgainstPolygonFace()
        {
            Collide.PolygonAndCircle(Polygon.Box(1f, 1f), Pose(0f, 0f), new Circle(Vec2.Zero, 0.5f), Pose(0f, 1.25f), 0f, out var m);

            Assert.AreEqual(1, m.PointCount);
            AssertVec(new Vec2(0f, 1f), m.Normal);
            Assert.AreEqual(-0.25f, m.Point0.Separation, 1e-6f);
        }

        [Test]
        public void CircleAgainstPolygonCorner()
        {
            var d = 1f + 0.4f / 1.41421356f;
            Collide.PolygonAndCircle(Polygon.Box(1f, 1f), Pose(0f, 0f), new Circle(Vec2.Zero, 0.5f), Pose(d, d), 0f, out var m);

            Assert.AreEqual(1, m.PointCount);
            AssertVec(new Vec2(0.70710678f, 0.70710678f), m.Normal, 1e-5f);
            Assert.AreEqual(-0.1f, m.Point0.Separation, 1e-5f);
        }

        [Test]
        public void CircleInsidePolygonIsPushedOutThroughTheNearestFace()
        {
            Collide.PolygonAndCircle(Polygon.Box(2f, 1f), Pose(0f, 0f), new Circle(Vec2.Zero, 0.25f), Pose(0.5f, 0.6f), 0f, out var m);

            Assert.AreEqual(1, m.PointCount);
            AssertVec(new Vec2(0f, 1f), m.Normal);
            Assert.AreEqual(-0.65f, m.Point0.Separation, 1e-5f);
        }

        [Test]
        public void StackedBoxesGiveTwoPoints()
        {
            Collide.Polygons(Polygon.Box(1f, 1f), Pose(0f, 0f), Polygon.Box(0.5f, 0.5f), Pose(0.2f, 1.4f), 0f, out var m);

            Assert.AreEqual(2, m.PointCount);
            AssertVec(new Vec2(0f, 1f), m.Normal, 1e-6f);
            Assert.AreEqual(-0.1f, m.Point0.Separation, 1e-5f);
            Assert.AreEqual(-0.1f, m.Point1.Separation, 1e-5f);
            Assert.AreNotEqual(m.Point0.Id, m.Point1.Id);
        }

        [Test]
        public void PolygonNormalPointsFromAToBWhenBIsTheReference()
        {
            // The small box is A. The face of the large box B is the better reference, so the result is flipped.
            Collide.Polygons(Polygon.Box(0.5f, 0.5f), Pose(0.2f, 1.4f), Polygon.Box(1f, 1f), Pose(0f, 0f), 0f, out var m);

            Assert.AreEqual(2, m.PointCount);
            AssertVec(new Vec2(0f, -1f), m.Normal, 1e-6f);
        }

        [Test]
        public void SeparatedPolygonsGiveSpeculativePointsOnlyWithinMaxDistance()
        {
            var box = Polygon.Box(1f, 1f);

            Collide.Polygons(box, Pose(0f, 0f), box, Pose(2.3f, 0f), 0.5f, out var near);
            Assert.AreEqual(2, near.PointCount);
            Assert.AreEqual(0.3f, near.Point0.Separation, 1e-5f);

            Collide.Polygons(box, Pose(0f, 0f), box, Pose(2.6f, 0f), 0.5f, out var far);
            Assert.AreEqual(0, far.PointCount);
        }

        [Test]
        public void CornerApproachGetsASpeculativePoint()
        {
            // Box B approaches the corner of box A diagonally; neither incident vertex lies within the face.
            var box = Polygon.Box(1f, 1f);
            Collide.Polygons(box, Pose(0f, 0f), box, Pose(2.2f, 2.2f), 0.5f, out var m);

            Assert.Greater(m.PointCount, 0);
            Assert.AreEqual(0.2f, m.MinSeparation, 1e-5f);
        }

        [Test]
        public void HullsWithTheSameCentreGiveAFiniteManifold()
        {
            var hull = Polygon.Regular(6, 2f);
            Collide.Polygons(hull, Pose(3f, 3f), hull, Pose(3f, 3f), 0f, out var m);

            Assert.Greater(m.PointCount, 0);
            Assert.IsTrue(m.Normal.IsFinite);
            Assert.AreEqual(1f, m.Normal.Length, 1e-5f);
            Assert.Less(m.MinSeparation, -3f);
        }

        private static Pose Pose(float x, float y) => new Pose(new Vec2(x, y), Rot.Identity);
    }
}
