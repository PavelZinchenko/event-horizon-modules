using System;
using NUnit.Framework;

namespace CombatSimulation.Physics.Tests
{
    public class GeometryTests
    {
        [Test]
        public void TorusWrapKeepsCanonicalValuesExact()
        {
            Assert.AreEqual(0f, Torus.Wrap(0f, 200f));
            Assert.AreEqual(199.5f, Torus.Wrap(199.5f, 200f));
            Assert.AreEqual(0.5f, Torus.Wrap(200.5f, 200f));
            Assert.AreEqual(199.5f, Torus.Wrap(-0.5f, 200f));
            Assert.AreEqual(10f, Torus.Wrap(410f, 200f));

            var tiny = Torus.Wrap(-1e-9f, 200f);
            Assert.IsTrue(tiny >= 0f && tiny < 200f);
        }

        [Test]
        public void TorusDeltaTakesTheShortestWay()
        {
            var torus = new Torus(200f, 100f);
            AssertVec(new Vec2(2f, 0f), torus.Delta(new Vec2(199f, 50f), new Vec2(1f, 50f)));
            AssertVec(new Vec2(-2f, 0f), torus.Delta(new Vec2(1f, 50f), new Vec2(199f, 50f)));
            AssertVec(new Vec2(0f, -3f), torus.Delta(new Vec2(10f, 1f), new Vec2(10f, 98f)));
            AssertVec(new Vec2(50f, 20f), torus.Delta(new Vec2(10f, 10f), new Vec2(60f, 30f)));
        }

        [Test]
        public void CircleMassMatchesClosedForm()
        {
            var data = new Circle(new Vec2(1f, 2f), 0.5f).ComputeMass();
            var area = Utilites.MathEx.PI * 0.25f;
            Assert.AreEqual(area, data.Area, 1e-6f);
            AssertVec(new Vec2(1f, 2f), data.Centroid);
            Assert.AreEqual(area * (0.5f * 0.25f + 5f), data.InertiaAboutOrigin, 1e-5f);
        }

        [Test]
        public void BoxMassMatchesClosedForm()
        {
            // Box 4 × 2: area 8, inertia about the centre m(w² + h²)/12.
            var data = Polygon.Box(2f, 1f).ComputeMass();
            Assert.AreEqual(8f, data.Area, 1e-5f);
            AssertVec(Vec2.Zero, data.Centroid);
            Assert.AreEqual(8f * (16f + 4f) / 12f, data.InertiaAboutOrigin, 1e-4f);
        }

        [Test]
        public void OffCentrePolygonHasItsCentroidAndShiftedInertia()
        {
            var data = Polygon.Box(1f, 1f, new Vec2(3f, -2f), 0f).ComputeMass();
            Assert.AreEqual(4f, data.Area, 1e-5f);
            AssertVec(new Vec2(3f, -2f), data.Centroid, 1e-5f);
            Assert.AreEqual(4f * 8f / 12f + 4f * 13f, data.InertiaAboutOrigin, 1e-3f);
        }

        [Test]
        public void CapsuleMassMatchesNumericIntegration()
        {
            var capsule = new Capsule(new Vec2(-0.5f, 1f), new Vec2(1.5f, 1f), 0.4f);
            var data = capsule.ComputeMass();

            // Midpoint rule on a fine grid over the bounding box.
            const int steps = 1200;
            double minX = -0.9, maxX = 1.9, minY = 0.6, maxY = 1.4;
            double dx = (maxX - minX) / steps, dy = (maxY - minY) / steps;
            double area = 0, inertia = 0;
            for (var i = 0; i < steps; ++i)
            for (var j = 0; j < steps; ++j)
            {
                var x = minX + (i + 0.5) * dx;
                var y = minY + (j + 0.5) * dy;
                var cx = Math.Max(-0.5, Math.Min(1.5, x));
                if ((x - cx) * (x - cx) + (y - 1) * (y - 1) > 0.16) continue;
                area += dx * dy;
                inertia += (x * x + y * y) * dx * dy;
            }

            Assert.AreEqual(area, data.Area, 1e-3 * area);
            AssertVec(new Vec2(0.5f, 1f), data.Centroid);
            Assert.AreEqual(inertia, data.InertiaAboutOrigin, 1e-3 * inertia);
        }

        [Test]
        public void CapsuleFromUnitySize()
        {
            var vertical = Capsule.FromSize(new Vec2(1f, 0f), 0.4f, 2f, true);
            AssertVec(new Vec2(1f, -0.8f), vertical.Center1);
            AssertVec(new Vec2(1f, 0.8f), vertical.Center2);
            Assert.AreEqual(0.2f, vertical.Radius, 1e-6f);

            var horizontal = Capsule.FromSize(Vec2.Zero, 2f, 0.4f, false);
            AssertVec(new Vec2(-0.8f, 0f), horizontal.Center1);
            Assert.AreEqual(0.2f, horizontal.Radius, 1e-6f);
        }

        [Test]
        public void TriangleCentroid()
        {
            var polygon = Polygon.FromPoints(new[] { new Vec2(0f, 0f), new Vec2(3f, 0f), new Vec2(0f, 3f) });
            AssertVec(new Vec2(1f, 1f), polygon.Centroid, 1e-5f);
        }

        [Test]
        public void PolygonHullRemovesInnerAndCollinearPoints()
        {
            var polygon = Polygon.FromPoints(new[]
            {
                new Vec2(0f, 0f), new Vec2(1f, 0f), new Vec2(2f, 0f), new Vec2(2f, 2f), new Vec2(0f, 2f), new Vec2(1f, 1f),
            });

            Assert.AreEqual(4, polygon.Count);
        }

        [Test]
        public void PolygonRejectsDegenerateInput()
        {
            Assert.Throws<System.ArgumentException>(() => Polygon.FromPoints(new[] { new Vec2(0f, 0f), new Vec2(1f, 0f), new Vec2(2f, 0f) }));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => Polygon.Regular(9, 1f));
        }

        [Test]
        public void RotationIntegrationStaysNormalized()
        {
            var q = Rot.Identity;
            for (var i = 0; i < 1000; ++i)
                q = Rot.Integrate(q, 0.1f);

            Assert.AreEqual(1f, q.C * q.C + q.S * q.S, 1e-5f);
        }

        internal static void AssertVec(Vec2 expected, Vec2 actual, float tolerance = 1e-6f)
        {
            Assert.AreEqual(expected.X, actual.X, tolerance, $"X of {actual}");
            Assert.AreEqual(expected.Y, actual.Y, tolerance, $"Y of {actual}");
        }
    }
}
