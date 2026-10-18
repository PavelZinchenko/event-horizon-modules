using NUnit.Framework;

namespace CombatSimulation.Physics.Tests
{
    public class SeparationTests
    {
        private const float Dt = 0.02f;
        private const int MaxSteps = 100;

        [Test]
        public void CircleInsideACompoundBarLeavesAlongTheShortAxis()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var bar = CreateBar(world, new Vec2(50f, 50f), BodyFlags.FixedPosition | BodyFlags.FixedRotation);
            var drone = CreateCircle(world, new Vec2(50f, 50f), 0.25f, 0.14f);

            Assert.IsTrue(world.Separate(drone, bar));
            var steps = StepUntilApart(world, drone, bar);

            Assert.Less(steps, MaxSteps, "The drone must leave the bar");
            Assert.LessOrEqual(world.GetPairOverlap(drone, bar), world.Settings.LinearSlop);
            var moved = world.GetPosition(drone) - new Vec2(50f, 50f);
            Assert.Less(System.Math.Abs(moved.X), 0.01f, "Shortest way out is across the bar");
            Assert.Greater(System.Math.Abs(moved.Y), 0.7f);
            Assert.Less(world.GetLinearVelocity(drone).Length, world.Settings.PushMaxVelocity + 0.01f, "No launch");
        }

        [Test]
        public void DeepContactStartsSeparationOnItsOwn()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var bar = CreateBar(world, new Vec2(50f, 50f), BodyFlags.FixedPosition | BodyFlags.FixedRotation);
            var drone = CreateCircle(world, new Vec2(50.3f, 50.1f), 0.25f, 0.14f);

            var started = -1;
            for (var i = 0; i < MaxSteps && started < 0; ++i)
            {
                world.Step(Dt);
                if (world.IsSeparating(drone, bar)) started = i;
            }

            Assert.GreaterOrEqual(started, world.Settings.SeparationSteps, "Only after the trigger time");
            Assert.Less(StepUntilApart(world, drone, bar), MaxSteps);
            Assert.AreEqual(0, world.FailedSeparationCount);
        }

        [Test]
        public void SingleShapePairsKeepTheirContacts()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = CreateCircle(world, new Vec2(50f, 50f), 0.25f, 0.14f);
            var b = CreateCircle(world, new Vec2(50.2f, 50f), 0.25f, 0.14f);

            // Pressed together, so the contact stays deep for longer than the trigger time.
            for (var i = 0; i < 2 * world.Settings.SeparationSteps; ++i)
            {
                world.ApplyForceToCenter(a, new Vec2(400f, 0f));
                world.ApplyForceToCenter(b, new Vec2(-400f, 0f));
                world.Step(Dt);
                Assert.IsFalse(world.IsSeparating(a, b));
            }
        }

        [Test]
        public void SeparationIsRepeatable()
        {
            Vec2 Run()
            {
                var world = new PhysicsWorld(new WorldSettings());
                var bar = CreateBar(world, new Vec2(20f, 30f), BodyFlags.None, angle: 0.3f);
                var drone = CreateCircle(world, new Vec2(20.4f, 30.05f), 0.25f, 0.14f);
                world.Separate(drone, bar);
                for (var i = 0; i < 60; ++i) world.Step(Dt);
                return world.GetPosition(drone);
            }

            var first = Run();
            var second = Run();
            Assert.AreEqual(first.X, second.X);
            Assert.AreEqual(first.Y, second.Y);
        }

        [Test]
        public void FixedBodyKeepsItsExactPosition()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var position = new Vec2(37.123456f, 81.654321f);
            var bar = CreateBar(world, position, BodyFlags.FixedPosition);
            var drone = CreateCircle(world, position + new Vec2(-0.2f, 0f), 0.25f, 0.14f);

            world.Separate(drone, bar);
            for (var i = 0; i < MaxSteps; ++i)
            {
                world.Step(Dt);
                Assert.AreEqual(position.X, world.GetPosition(bar).X);
                Assert.AreEqual(position.Y, world.GetPosition(bar).Y);
            }

            Assert.IsFalse(world.IsSeparating(drone, bar));
        }

        [Test]
        public void SidePressureDoesNotStopTheSeparation()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var bar = CreateBar(world, new Vec2(50f, 50f), BodyFlags.FixedPosition | BodyFlags.FixedRotation);
            var drone = CreateCircle(world, new Vec2(50f, 50f), 0.25f, 0.14f);
            var pusher = CreateCircle(world, new Vec2(50.5f, 50f), 0.25f, 0.14f);

            world.Separate(drone, bar);
            world.Separate(pusher, bar);
            var steps = 0;
            for (; steps < MaxSteps && (world.IsSeparating(drone, bar) || world.GetPairOverlap(drone, bar) > world.Settings.LinearSlop); ++steps)
            {
                world.ApplyForceToCenter(pusher, new Vec2(-0.14f * 400f, 0f));
                world.Step(Dt);
            }

            Assert.Less(steps, MaxSteps);
            Assert.AreEqual(0, world.FailedSeparationCount);
        }

        [Test]
        public void ApartBodiesDoNotStartASeparation()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var bar = CreateBar(world, new Vec2(50f, 50f), BodyFlags.None);
            var drone = CreateCircle(world, new Vec2(50f, 52f), 0.25f, 0.14f);

            Assert.IsFalse(world.Separate(drone, bar));
            Assert.AreEqual(0, world.SeparationCount);
        }

        // A 4 × 1 bar made of two 2 × 1 parts, so a circle at its centre sits on the cut.
        private static BodyId CreateBar(PhysicsWorld world, Vec2 position, BodyFlags flags, float angle = 0f)
        {
            var body = world.CreateBody(new BodyDef { Position = position, Angle = angle, Mass = 4f, Flags = flags });
            world.AddPolygon(body, Polygon.Box(1f, 0.5f, new Vec2(-1f, 0f), 0f));
            world.AddPolygon(body, Polygon.Box(1f, 0.5f, new Vec2(1f, 0f), 0f));
            return body;
        }

        private static BodyId CreateCircle(PhysicsWorld world, Vec2 position, float radius, float mass)
        {
            var body = world.CreateBody(BodyDef.Create(position, mass));
            world.AddCircle(body, new Circle(Vec2.Zero, radius));
            return body;
        }

        private static int StepUntilApart(PhysicsWorld world, BodyId a, BodyId b)
        {
            var steps = 0;
            while (steps < MaxSteps && world.IsSeparating(a, b))
            {
                world.Step(Dt);
                ++steps;
            }

            return steps;
        }
    }
}
