using System;
using System.Collections.Generic;
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
        public void CornerApproachGetsItsTrueGap()
        {
            // Box B approaches the corner of box A diagonally; the incident edge lies beside the reference face.
            // The point measures the corner-to-corner distance, not the gap to the face plane (0.2).
            var box = Polygon.Box(1f, 1f);
            Collide.Polygons(box, Pose(0f, 0f), box, Pose(2.2f, 2.2f), 0.5f, out var m);

            Assert.AreEqual(1, m.PointCount);
            Assert.AreEqual(0.2f * 1.41421356f, m.Point0.Separation, 1e-5f);
            AssertVec(new Vec2(0.70710678f, 0.70710678f), m.Normal, 1e-5f);
            AssertVec(new Vec2(1.1f, 1.1f), m.Point0.Point, 1e-5f);
        }

        [Test]
        public void CornerPassingBesideAFaceIsNotStoppedEarly()
        {
            // B's corner lies 0.3 past the end of A's top face and 0.1 above it: true gap 0.316, plane gap 0.1.
            var box = Polygon.Box(1f, 1f);
            Collide.Polygons(box, Pose(0f, 0f), box, Pose(2.3f, 2.1f), 1f, out var m);

            Assert.AreEqual(1, m.PointCount);
            Assert.AreEqual((float)Math.Sqrt(0.3 * 0.3 + 0.1 * 0.1), m.Point0.Separation, 1e-5f);
        }

        [Test]
        public void ParallelFacesApartKeepTwoPoints()
        {
            // Equal boxes face to face: the closest features are end vertices, but the faces overlap fully.
            var box = Polygon.Box(1f, 1f);
            Collide.Polygons(box, Pose(0f, 0f), box, Pose(0f, 2.5f), 1f, out var m);

            Assert.AreEqual(2, m.PointCount);
            Assert.AreEqual(0.5f, m.Point0.Separation, 1e-5f);
            Assert.AreEqual(0.5f, m.Point1.Separation, 1e-5f);
        }

        [Test]
        public void CircleBesideCapsuleSide()
        {
            var capsule = new Capsule(new Vec2(-1f, 0f), new Vec2(1f, 0f), 0.5f);
            Collide.CapsuleAndCircle(capsule, Pose(0f, 0f), new Circle(Vec2.Zero, 0.25f), Pose(0.4f, 0.6f), 0f, out var m);

            Assert.AreEqual(1, m.PointCount);
            AssertVec(new Vec2(0f, 1f), m.Normal, 1e-6f);
            Assert.AreEqual(-0.15f, m.Point0.Separation, 1e-5f);
        }

        [Test]
        public void CircleAgainstCapsuleCap()
        {
            var capsule = new Capsule(new Vec2(-1f, 0f), new Vec2(1f, 0f), 0.5f);
            var d = 0.7f / 1.41421356f;
            Collide.CapsuleAndCircle(capsule, Pose(0f, 0f), new Circle(Vec2.Zero, 0.25f), Pose(1f + d, d), 0f, out var m);

            Assert.AreEqual(1, m.PointCount);
            AssertVec(new Vec2(0.70710678f, 0.70710678f), m.Normal, 1e-5f);
            Assert.AreEqual(-0.05f, m.Point0.Separation, 1e-5f);
        }

        [Test]
        public void ParallelCapsulesGiveTwoPoints()
        {
            var capsule = new Capsule(new Vec2(-1f, 0f), new Vec2(1f, 0f), 0.25f);
            Collide.Capsules(capsule, Pose(0f, 0f), capsule, Pose(0.5f, 0.45f), 0f, out var m);

            Assert.AreEqual(2, m.PointCount);
            AssertVec(new Vec2(0f, 1f), m.Normal, 1e-6f);
            Assert.AreEqual(-0.05f, m.Point0.Separation, 1e-5f);
            Assert.AreEqual(-0.05f, m.Point1.Separation, 1e-5f);
            Assert.AreNotEqual(m.Point0.Id, m.Point1.Id);
        }

        [Test]
        public void CapsuleStandingOnACapsuleUsesItsCap()
        {
            // B is vertical and stands on its lower cap: its segment ends 0.6 above A's axis, and the radii take 0.5.
            var capsule = new Capsule(new Vec2(-1f, 0f), new Vec2(1f, 0f), 0.25f);
            var standing = new Pose(new Vec2(0f, 1.6f), Rot.FromAngle(0.5f * Utilites.MathEx.PI));
            Collide.Capsules(capsule, Pose(0f, 0f), capsule, standing, 1f, out var m);

            Assert.Greater(m.PointCount, 0);
            Assert.AreEqual(0.1f, m.MinSeparation, 1e-5f);
            AssertVec(new Vec2(0f, 1f), m.Normal, 1e-5f);
        }

        [Test]
        public void CapsuleLyingOnABoxGivesTwoPoints()
        {
            var capsule = new Capsule(new Vec2(-0.5f, 0f), new Vec2(0.5f, 0f), 0.25f);
            Collide.PolygonAndCapsule(Polygon.Box(1f, 1f), Pose(0f, 0f), capsule, Pose(0.2f, 1.2f), 0f, out var m);

            Assert.AreEqual(2, m.PointCount);
            AssertVec(new Vec2(0f, 1f), m.Normal, 1e-6f);
            Assert.AreEqual(-0.05f, m.Point0.Separation, 1e-5f);
            Assert.AreEqual(-0.05f, m.Point1.Separation, 1e-5f);
            Assert.AreEqual(1f - 0.025f, m.Point0.Point.Y, 1e-5f);
        }

        [Test]
        public void CapsuleCapNearABoxCornerUsesTheTrueDistance()
        {
            // The capsule points at the box corner along the diagonal; the cap centre is 0.5 from the corner.
            var capsule = new Capsule(new Vec2(0f, 0f), new Vec2(1f, 0f), 0.2f);
            var d = 0.5f / 1.41421356f;
            var pose = new Pose(new Vec2(1f + d, 1f + d), Rot.FromAngle(0.25f * Utilites.MathEx.PI));
            Collide.PolygonAndCapsule(Polygon.Box(1f, 1f), Pose(0f, 0f), capsule, pose, 1f, out var m);

            Assert.AreEqual(1, m.PointCount);
            Assert.AreEqual(0.3f, m.Point0.Separation, 1e-5f);
            AssertVec(new Vec2(0.70710678f, 0.70710678f), m.Normal, 1e-5f);
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

        [Test]
        public void BoxApproachingAStraightCutDoesNotCatch()
        {
            var plain = StraightBar();
            var parts = Attached(plain);
            var probe = Polygon.Box(0.25f, 0.25f);

            // The probe is on the right part, 0.01 past the cut, and sinks 0.004 into the top face.
            Collide.Polygons(plain[0], Pose(0f, 0f), probe, Pose(0.26f, 0.746f), 0.1f, out var caught);
            AssertVec(new Vec2(1f, 0f), caught.Normal);

            Collide.Polygons(parts[0], Pose(0f, 0f), probe, Pose(0.26f, 0.746f), 0.1f, out var m);
            Assert.AreEqual(0, m.PointCount);
        }

        [Test]
        public void BoxApartAcrossACutGetsItsPointFromTheNeighbour()
        {
            var parts = Attached(StraightBar());
            var probe = Polygon.Box(0.25f, 0.25f);

            // The probe is 0.1 past the cut and 0.05 above the top face: the cut gives the larger gap, and the true
            // direction from the left part leaves its top corner sideways. The right part holds the contact.
            Collide.Polygons(parts[0], Pose(0f, 0f), probe, Pose(0.35f, 0.8f), 0.2f, out var left);
            Assert.AreEqual(0, left.PointCount);

            Collide.Polygons(parts[1], Pose(0f, 0f), probe, Pose(0.35f, 0.8f), 0.2f, out var right);
            Assert.Greater(right.PointCount, 0);
            AssertVec(new Vec2(0f, 1f), right.Normal);
            Assert.AreEqual(0.05f, right.MinSeparation, 1e-5f);
        }

        [Test]
        public void BoxOverAStraightCutGetsTheTopFace()
        {
            var plain = StraightBar();
            var parts = Attached(plain);
            var probe = Polygon.Box(0.25f, 0.25f);

            // The probe reaches 0.002 over the cut into the left part, less than it sinks into the top face.
            Collide.Polygons(plain[0], Pose(0f, 0f), probe, Pose(0.248f, 0.746f), 0.1f, out var caught);
            AssertVec(new Vec2(1f, 0f), caught.Normal);

            Collide.Polygons(parts[0], Pose(0f, 0f), probe, Pose(0.248f, 0.746f), 0.1f, out var m);
            Assert.Greater(m.PointCount, 0);
            AssertVec(new Vec2(0f, 1f), m.Normal);
            Assert.AreEqual(-0.004f, m.MinSeparation, 1e-5f);
        }

        [Test]
        public void BoxApproachingASlantedCutDoesNotCatch()
        {
            // Bar [-2, 2] × [-0.5, 0.5], cut from (0, -0.5) to (1, 0.5).
            var plain = new[]
            {
                Polygon.FromPoints(new[] { new Vec2(-2f, -0.5f), new Vec2(0f, -0.5f), new Vec2(1f, 0.5f), new Vec2(-2f, 0.5f) }),
                Polygon.FromPoints(new[] { new Vec2(0f, -0.5f), new Vec2(2f, -0.5f), new Vec2(2f, 0.5f), new Vec2(1f, 0.5f) }),
            };
            var parts = Attached(plain);
            var probe = Polygon.Box(0.25f, 0.25f);

            // The probe slides right on the left part; its corner is 0.01 before the seam at (1, 0.5).
            Collide.Polygons(plain[1], Pose(0f, 0f), probe, Pose(0.74f, 0.746f), 0.1f, out var caught);
            AssertVec(new Vec2(-0.70710678f, 0.70710678f), caught.Normal, 1e-5f);

            Collide.Polygons(parts[1], Pose(0f, 0f), probe, Pose(0.74f, 0.746f), 0.1f, out var m);
            Assert.AreEqual(0, m.PointCount);

            Collide.Polygons(parts[0], Pose(0f, 0f), probe, Pose(0.74f, 0.746f), 0.1f, out var top);
            AssertVec(new Vec2(0f, 1f), top.Normal);
        }

        [Test]
        public void CircleAtAStraightSeamGetsTheFaceNormal()
        {
            var plain = StraightBar();
            var parts = Attached(plain);
            var circle = new Circle(Vec2.Zero, 0.25f);

            // The centre is 0.05 before the right part, so the corner of that part is its nearest feature.
            Collide.PolygonAndCircle(plain[1], Pose(0f, 0f), circle, Pose(-0.05f, 0.746f), 0.1f, out var caught);
            Assert.Less(caught.Normal.X, -0.1f);

            Collide.PolygonAndCircle(parts[1], Pose(0f, 0f), circle, Pose(-0.05f, 0.746f), 0.1f, out var m);
            Assert.AreEqual(1, m.PointCount);
            AssertVec(new Vec2(0f, 1f), m.Normal);
            Assert.AreEqual(-0.004f, m.Point0.Separation, 1e-5f);
        }

        [Test]
        public void CircleBelowANotchIsPushedOutThroughTheWalls()
        {
            // A notch at (0, 0) between two walls; the cut runs straight down from it.
            var outline = new[] { new Vec2(-2f, -1f), new Vec2(2f, -1f), new Vec2(2f, 1f), new Vec2(0f, 0f), new Vec2(-2f, 1f) };
            var plain = new[]
            {
                Polygon.FromPoints(new[] { new Vec2(-2f, -1f), new Vec2(0f, -1f), new Vec2(0f, 0f), new Vec2(-2f, 1f) }),
                Polygon.FromPoints(new[] { new Vec2(0f, -1f), new Vec2(2f, -1f), new Vec2(2f, 1f), new Vec2(0f, 0f) }),
            };
            var parts = new List<Polygon>(plain);
            ConvexDecomposition.AttachOutline(parts, outline);
            var circle = new Circle(Vec2.Zero, 0.1f);
            var root5 = (float)Math.Sqrt(5.0);

            Collide.PolygonAndCircle(plain[0], Pose(0f, 0f), circle, Pose(0f, -0.2f), 0f, out var caught);
            AssertVec(new Vec2(1f, 0f), caught.Normal);

            Collide.PolygonAndCircle(parts[0], Pose(0f, 0f), circle, Pose(0f, -0.2f), 0f, out var left);
            Collide.PolygonAndCircle(parts[1], Pose(0f, 0f), circle, Pose(0f, -0.2f), 0f, out var right);
            AssertVec(new Vec2(1f / root5, 2f / root5), left.Normal, 1e-5f);
            AssertVec(new Vec2(-1f / root5, 2f / root5), right.Normal, 1e-5f);
            Assert.AreEqual(-0.4f / root5 - 0.1f, left.Point0.Separation, 1e-5f);
            Assert.AreEqual(-0.4f / root5 - 0.1f, right.Point0.Separation, 1e-5f);
        }

        [Test]
        public void PartWithOnlyCutsPushesOutAlongAnOutlineNormal()
        {
            // Six-pointed star: the inner hexagon touches the outline only at its corners.
            var outline = new Vec2[12];
            for (var i = 0; i < 12; ++i)
            {
                var angle = Math.PI * i / 6.0;
                var radius = i % 2 == 0 ? 2.0 : 2.0 / Math.Sqrt(3.0);
                outline[i] = new Vec2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
            }

            var parts = new List<Polygon> { Polygon.FromPoints(new[] { outline[1], outline[3], outline[5], outline[7], outline[9], outline[11] }) };
            for (var i = 0; i < 12; i += 2)
                parts.Add(Polygon.FromPoints(new[] { outline[(i + 11) % 12], outline[i], outline[i + 1] }));
            ConvexDecomposition.AttachOutline(parts, outline);

            var hexagon = parts[0];
            Assert.AreEqual(0b111111, hexagon.CutEdges);

            Collide.PolygonAndCircle(hexagon, Pose(0f, 0f), new Circle(Vec2.Zero, 0.2f), Pose(0.1f, 0.05f), 0f, out var m);
            Assert.AreEqual(1, m.PointCount);
            var outlineNormal = false;
            for (var i = 0; i < hexagon.Count; ++i)
                outlineNormal |= Vec2.Dot(m.Normal, hexagon.OutlineNormalIn(i)) > 0.9999f || Vec2.Dot(m.Normal, hexagon.OutlineNormalOut(i)) > 0.9999f;
            Assert.IsTrue(outlineNormal);
        }

        // Bar [-2, 2] × [-0.5, 0.5] made of two boxes with a cut at x = 0.
        private static Polygon[] StraightBar() => new[]
        {
            Polygon.Box(1f, 0.5f, new Vec2(-1f, 0f), 0f),
            Polygon.Box(1f, 0.5f, new Vec2(1f, 0f), 0f),
        };

        private static List<Polygon> Attached(Polygon[] bar)
        {
            var parts = new List<Polygon>(bar);
            ConvexDecomposition.AttachOutline(parts, new[] { new Vec2(-2f, -0.5f), new Vec2(2f, -0.5f), new Vec2(2f, 0.5f), new Vec2(-2f, 0.5f) });
            return parts;
        }

        private static Pose Pose(float x, float y) => new Pose(new Vec2(x, y), Rot.Identity);
    }
}
