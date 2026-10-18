using NUnit.Framework;

namespace CombatSimulation.Physics.Tests
{
    public class TimeOfImpactTests
    {
        private const float Dt = 0.02f;
        private const float Target = 0.02f;
        private const float Tolerance = 0.00125f;

        [Test]
        public void HeadOnHitGivesTheTimeAtTheTargetGap()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));
            var moving = Moving(new Vec2(-2f, 0f), new Vec2(100f, 0f), 0f);
            var resting = Moving(Vec2.Zero, Vec2.Zero, 0f);

            var state = TimeOfImpact.Compute(box, moving, box, resting, Target, Tolerance, 0f, 1f, out var fraction);

            // The gap of 1 closes by 2 in the step; it reaches 0.02 at 0.49.
            Assert.AreEqual(TimeOfImpactState.Hit, state);
            Assert.AreEqual(0.49f, fraction, 1e-3f);
        }

        [Test]
        public void PassCloseByDoesNotHit()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));
            var circle = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.5f));
            var resting = Moving(Vec2.Zero, Vec2.Zero, 0f);

            // 0.05 above the top face, 4 u along it within the step.
            var passingBox = Moving(new Vec2(-2f, 1.05f), new Vec2(200f, 0f), 0f);
            Assert.AreEqual(TimeOfImpactState.Separated, TimeOfImpact.Compute(box, passingBox, box, resting, Target, Tolerance, 0f, 1f, out _));

            var passingCircle = Moving(new Vec2(-2f, 1.05f), new Vec2(200f, 0f), 0f);
            Assert.AreEqual(TimeOfImpactState.Separated, TimeOfImpact.Compute(circle, passingCircle, box, resting, Target, Tolerance, 0f, 1f, out _));
        }

        [Test]
        public void PassCloserThanTheTargetHits()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));
            var passing = Moving(new Vec2(-2f, 1.01f), new Vec2(200f, 0f), 0f);
            var resting = Moving(Vec2.Zero, Vec2.Zero, 0f);

            var state = TimeOfImpact.Compute(box, passing, box, resting, Target, Tolerance, 0f, 1f, out var fraction);

            // The corners meet when the passing box reaches x = -1, a quarter of the way.
            Assert.AreEqual(TimeOfImpactState.Hit, state);
            Assert.AreEqual(0.25f, fraction, 0.01f);
        }

        [Test]
        public void SpinningBarHitsACircle()
        {
            var bar = DistanceProxy.FromPolygon(Polygon.Box(1f, 0.05f));
            var circle = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.1f));
            var spinning = Moving(Vec2.Zero, Vec2.Zero, 100f);
            var resting = Moving(new Vec2(0f, 0.8f), Vec2.Zero, 0f);

            var state = TimeOfImpact.Compute(bar, spinning, circle, resting, Target, Tolerance, 0f, 1f, out var fraction);

            // The bar turns 2 rad in the step; it meets the circle before it is upright.
            Assert.AreEqual(TimeOfImpactState.Hit, state);
            Assert.Greater(fraction, 0.3f);
            Assert.Less(fraction, 0.8f);
            var gap = Distance.Shapes(bar, spinning.GetPose(fraction), circle, resting.GetPose(fraction), out _, out _);
            Assert.AreEqual(Target, gap, 2f * Tolerance);
        }

        [Test]
        public void SpinningBarShorterThanTheGapMisses()
        {
            var bar = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.05f));
            var circle = DistanceProxy.FromCircle(new Circle(Vec2.Zero, 0.1f));
            var spinning = Moving(Vec2.Zero, Vec2.Zero, 100f);
            var resting = Moving(new Vec2(0f, 0.8f), Vec2.Zero, 0f);

            Assert.AreEqual(TimeOfImpactState.Separated, TimeOfImpact.Compute(bar, spinning, circle, resting, Target, Tolerance, 0f, 1f, out _));
        }

        [Test]
        public void OverlapAtTheStart()
        {
            var box = DistanceProxy.FromPolygon(Polygon.Box(0.5f, 0.5f));
            var a = Moving(Vec2.Zero, new Vec2(10f, 0f), 0f);
            var b = Moving(new Vec2(0.5f, 0f), Vec2.Zero, 0f);

            var state = TimeOfImpact.Compute(box, a, box, b, Target, Tolerance, 0f, 1f, out var fraction);

            Assert.AreEqual(TimeOfImpactState.Overlapped, state);
            Assert.AreEqual(0f, fraction);
        }

        private static Sweep Moving(Vec2 position, Vec2 velocity, float angularVelocity)
        {
            var sweep = new Sweep(8);
            sweep.SetLinear(Vec2.Zero, position, Rot.Identity, velocity, angularVelocity, Dt);
            return sweep;
        }
    }
}
