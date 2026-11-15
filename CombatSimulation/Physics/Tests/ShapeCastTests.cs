using System;
using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class ShapeCastTests
    {
        private const float Tolerance = 1e-4f;
        private static readonly Pose Identity = new Pose(Vec2.Zero, Rot.Identity);

        [Test]
        public void CircleHitsBoxFaceAtTheExactTime()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));
            var bullet = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.1f));

            var state = ShapeCast.Cast(box, Identity, bullet, new Pose(new Vec2(-3f, 0.2f), Rot.Identity), new Vec2(4f, 0f), Tolerance,
                out var fraction, out var point, out var normal);

            // The circle surface reaches x = -0.5 after 2.4 of 4.
            Assert.AreEqual(ShapeCastState.Hit, state);
            Assert.AreEqual(0.6f, fraction, 1e-5f);
            AssertVec(new Vec2(-0.5f, 0.2f), point, 1e-4f);
            AssertVec(new Vec2(-1f, 0f), normal, 1e-5f);
        }

        [Test]
        public void RayHitsCircleAtTheEntryPoint()
        {
            var circle = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 1f));
            var point = DistanceProxy.FromPoint(Vec2.Zero);

            var state = ShapeCast.Cast(circle, Identity, point, new Pose(new Vec2(-5f, 0.6f), Rot.Identity), new Vec2(10f, 0f), Tolerance,
                out var fraction, out var hit, out var normal);

            // Entry at x = -0.8.
            Assert.AreEqual(ShapeCastState.Hit, state);
            Assert.AreEqual(0.42f, fraction, 1e-5f);
            AssertVec(new Vec2(-0.8f, 0.6f), hit, 1e-4f);
            AssertVec(new Vec2(-0.8f, 0.6f), normal, 1e-4f);
        }

        [Test]
        public void StartInsideIsOverlappedAndMovingAwayIsAMiss()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));
            var bullet = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.1f));

            Assert.AreEqual(ShapeCastState.Overlapped, ShapeCast.Cast(box, Identity, bullet, new Pose(new Vec2(0.2f, 0f), Rot.Identity),
                new Vec2(4f, 0f), Tolerance, out var fraction, out _, out _));
            Assert.AreEqual(0f, fraction);

            Assert.AreEqual(ShapeCastState.Miss, ShapeCast.Cast(box, Identity, bullet, new Pose(new Vec2(1f, 0f), Rot.Identity),
                new Vec2(4f, 0f), Tolerance, out _, out _, out _));
        }

        [Test]
        public void CapsuleBulletHitsRotatedPolygonFromAllSides()
        {
            var target = DistanceProxy.FromPolygon(Polygon.FromPoints(new[]
            {
                new Vec2(-1f, -0.3f), new Vec2(0.8f, -0.6f), new Vec2(1.2f, 0.4f), new Vec2(0f, 0.9f), new Vec2(-0.9f, 0.5f),
            }));
            var xfTarget = new Pose(Vec2.Zero, Rot.FromAngle(0.7f));
            var bullet = DistanceProxy.FromPolygon(new Capsule(new Vec2(-0.15f, 0f), new Vec2(0.15f, 0f), 0.05f).Core);

            for (var degrees = 0; degrees < 360; degrees += 7)
            {
                var angle = degrees * Utilites.MathEx.PI / 180f;
                var direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
                var side = Vec2.LeftPerp(direction) * (0.3f * (float)Math.Sin(3f * angle));
                var xfBullet = new Pose(-4f * direction + side, Rot.FromAngle(angle));
                var translation = 8f * direction;

                var state = ShapeCast.Cast(target, xfTarget, bullet, xfBullet, translation, Tolerance, out var fraction, out var point, out var normal);
                var reference = BruteForce(target, xfTarget, bullet, xfBullet, translation);

                Assert.AreEqual(ShapeCastState.Hit, state, $"{degrees}°");
                Assert.AreEqual(reference, fraction, 1.5f / Samples, $"{degrees}°");

                // At the hit the bullet touches the target, and the normal is the separating direction.
                var at = new Pose(xfBullet.P + fraction * translation, xfBullet.Q);
                Assert.Less(Distance.Shapes(target, xfTarget, bullet, at, out _, out _), 2f * Tolerance, $"{degrees}°");

                // A face against a face touches along a segment, so any point on it is right: the point must touch both.
                var probe = DistanceProxy.FromPoint(Vec2.Zero);
                Assert.Less(Distance.Shapes(target, xfTarget, probe, new Pose(point, Rot.Identity), out _, out _), 1e-4f, $"{degrees}°");
                Assert.Less(Distance.Shapes(bullet, at, probe, new Pose(point, Rot.Identity), out _, out _), 2f * Tolerance, $"{degrees}°");
                Assert.Less(Vec2.Dot(normal, direction), 1e-3f, $"{degrees}°: the normal must face the bullet");
            }
        }

        [Test]
        public void ThinBoxIsNeverTunnelledAtStressSpeed()
        {
            // 1,000 u/s for 0.02 s: 20 u in one cast against a 0.02 thick plate.
            var plate = DistanceProxy.FromPolygon(Polygon.Box(0.01f, 2f));
            var bullet = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.2f));
            for (var y = -2.15f; y <= 2.15f; y += 0.05f)
            {
                var state = ShapeCast.Cast(plate, Identity, bullet, new Pose(new Vec2(-10f, y), Rot.Identity), new Vec2(20f, 0f), Tolerance,
                    out var fraction, out _, out _);
                Assert.AreEqual(ShapeCastState.Hit, state, $"y = {y}");
                Assert.Less(fraction, 0.5f, $"y = {y}");
            }

            Assert.AreEqual(ShapeCastState.Miss, ShapeCast.Cast(plate, Identity, bullet, new Pose(new Vec2(-10f, 2.25f), Rot.Identity),
                new Vec2(20f, 0f), Tolerance, out _, out _, out _));
        }

        private const int Samples = 4000;

        // First sampled fraction where the shapes overlap.
        private static float BruteForce(in DistanceProxy a, in Pose xfA, in DistanceProxy b, in Pose xfB, Vec2 translation)
        {
            for (var i = 0; i <= Samples; ++i)
            {
                var t = (float)i / Samples;
                if (Distance.Shapes(a, xfA, b, new Pose(xfB.P + t * translation, xfB.Q), out _, out _) <= 0f) return t;
            }

            return 1f;
        }
    }
}
