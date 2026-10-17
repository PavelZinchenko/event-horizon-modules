using System;
using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class WorldTests
    {
        private const float Dt = 0.02f;

        [Test]
        public void BodyMovesAndWrapsAcrossTheSeam()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var body = world.CreateBody(new BodyDef { Position = new Vec2(199.5f, 10f), Mass = 1f, LinearVelocity = new Vec2(50f, 0f) });
            world.AddCircle(body, new Circle(Vec2.Zero, 0.5f));

            world.Step(Dt);

            AssertVec(new Vec2(0.5f, 10f), world.GetPosition(body), 1e-4f);
        }

        [Test]
        public void ContactAcrossTheSeamIsFoundOnce()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = CreateCircle(world, new Vec2(199.8f, 199.8f), 0.5f, 1f);
            var b = CreateCircle(world, new Vec2(0.2f, 0.2f), 0.5f, 1f);

            world.Step(Dt);

            Assert.AreEqual(1, world.ContactCount);
            ref readonly var contact = ref world.GetContact(0);
            Assert.IsTrue(contact.Touching);
            Assert.AreEqual(a, contact.BodyA);
            Assert.AreEqual(b, contact.BodyB);
            Assert.AreEqual(1, world.BeginEvents.Length);
        }

        [Test]
        public void LargeAndSmallBodiesPairOnceAcrossManyCells()
        {
            var world = new PhysicsWorld(new WorldSettings());
            CreateCircle(world, new Vec2(100f, 100f), 9f, 10f, BodyFlags.FixedPosition);
            for (var i = 0; i < 12; ++i)
            {
                var angle = 2f * Utilites.MathEx.PI * i / 12f;
                CreateCircle(world, new Vec2(100f + 9.1f * (float)Math.Cos(angle), 100f + 9.1f * (float)Math.Sin(angle)), 0.2f, 0.14f);
            }

            world.Step(Dt);

            var withLarge = 0;
            for (var i = 0; i < world.ContactCount; ++i)
                if (world.GetContact(i).BodyA.Index == 0) ++withLarge;

            Assert.AreEqual(12, withLarge);
        }

        [Test]
        public void FixedPositionBodyKeepsItsExactPosition()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var position = new Vec2(37.123456f, 81.654321f);
            var fixedBody = CreateCircle(world, position, 6f, 10f, BodyFlags.FixedPosition);
            var drone = CreateCircle(world, new Vec2(30f, 81.4f), 0.25f, 0.14f);
            world.SetLinearVelocity(drone, new Vec2(40f, 0f));

            for (var i = 0; i < 100; ++i)
            {
                world.Step(Dt);
                Assert.AreEqual(position.X, world.GetPosition(fixedBody).X);
                Assert.AreEqual(position.Y, world.GetPosition(fixedBody).Y);
            }

            Assert.Less(world.GetPosition(drone).X, position.X - 6f, "The drone must bounce, not pass through");
        }

        [Test]
        public void MomentumIsConservedInATwoBodyHit()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var light = CreateCircle(world, new Vec2(90f, 100f), 0.25f, 0.14f);
            var heavy = CreateCircle(world, new Vec2(100f, 100f), 6f, 14f);
            world.SetLinearVelocity(light, new Vec2(40f, 0f));

            var before = 0.14f * world.GetLinearVelocity(light) + 14f * world.GetLinearVelocity(heavy);
            for (var i = 0; i < 50; ++i) world.Step(Dt);
            var after = 0.14f * world.GetLinearVelocity(light) + 14f * world.GetLinearVelocity(heavy);

            Assert.Less(world.GetLinearVelocity(light).X, 0f, "The light body must bounce back");
            AssertVec(before, after, 1e-3f);
        }

        [Test]
        public void OffCentreImpulseGivesTheCorrectSpin()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var body = world.CreateBody(BodyDef.Create(new Vec2(50f, 50f), 2f));
            world.AddPolygon(body, Polygon.Box(1f, 0.5f));

            var impulse = new Vec2(0f, 3f);
            world.ApplyImpulse(body, impulse, new Vec2(51f, 50f));

            var inertia = 2f * (4f + 1f) / 12f;
            Assert.AreEqual(inertia, world.GetInertia(body), 1e-5f);
            Assert.AreEqual(3f / inertia, world.GetAngularVelocity(body), 1e-5f);
            AssertVec(new Vec2(0f, 1.5f), world.GetLinearVelocity(body));
        }

        [Test]
        public void ImpulseUsesTheCentreOfMassNotThePivot()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var body = world.CreateBody(BodyDef.Create(new Vec2(50f, 50f), 1f));
            world.AddCircle(body, new Circle(new Vec2(1f, 0f), 0.5f));

            AssertVec(new Vec2(51f, 50f), world.GetWorldCenter(body));
            world.ApplyImpulse(body, new Vec2(0f, 1f), new Vec2(51f, 50f));
            Assert.AreEqual(0f, world.GetAngularVelocity(body));
        }

        [Test]
        public void FixedRotationBodyDoesNotTurn()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var body = world.CreateBody(BodyDef.Create(new Vec2(50f, 50f), 2f, BodyFlags.FixedPosition | BodyFlags.FixedRotation));
            world.AddPolygon(body, Polygon.Box(1f, 1f));

            world.ApplyImpulse(body, new Vec2(0f, 3f), new Vec2(51f, 50f));

            Assert.AreEqual(0f, world.GetAngularVelocity(body));
            AssertVec(Vec2.Zero, world.GetLinearVelocity(body));
        }

        [Test]
        public void TouchingEndsWithAnEndEvent()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = CreateCircle(world, new Vec2(50f, 50f), 0.5f, 1f);
            var b = CreateCircle(world, new Vec2(50.9f, 50f), 0.5f, 1f);

            world.Step(Dt);
            Assert.AreEqual(1, world.BeginEvents.Length);

            world.SetPose(b, new Vec2(60f, 50f), 0f);
            world.Step(Dt);
            Assert.AreEqual(1, world.EndEvents.Length);
            Assert.AreEqual(a, world.EndEvents[0].BodyA);
        }

        [Test]
        public void DestroyingATouchingBodyReportsAnEndEvent()
        {
            var world = new PhysicsWorld(new WorldSettings());
            CreateCircle(world, new Vec2(50f, 50f), 0.5f, 1f);
            var b = CreateCircle(world, new Vec2(50.9f, 50f), 0.5f, 1f);
            world.Step(Dt);

            world.DestroyBody(b);
            Assert.AreEqual(0, world.ContactCount);
            Assert.IsFalse(world.IsAlive(b));

            world.Step(Dt);
            Assert.AreEqual(1, world.EndEvents.Length);
            Assert.AreEqual(b, world.EndEvents[0].BodyB);
        }

        [Test]
        public void FreePositionOnAnEmptyWorldIsTheTarget()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var body = CreateCircle(world, new Vec2(10f, 10f), 0.5f, 1f);

            Assert.IsTrue(world.TryFindFreePosition(body, new Vec2(80f, 80f), 10f, out var position));
            AssertVec(new Vec2(80f, 80f), position);
        }

        [Test]
        public void FreePositionAvoidsABlockedTarget()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var mothership = world.CreateBody(BodyDef.Create(new Vec2(80f, 80f), 1.5f));
            world.AddPolygon(mothership, Polygon.Regular(6, 2f));
            var clone = world.CreateBody(BodyDef.Create(new Vec2(10f, 10f), 1.5f));
            world.AddPolygon(clone, Polygon.Regular(6, 2f));

            Assert.Greater(world.GetOverlap(clone, new Vec2(81f, 80f), 0f), 0f);
            Assert.IsTrue(world.TryFindFreePosition(clone, new Vec2(81f, 80f), 20f, out var position));
            Assert.LessOrEqual(world.GetOverlap(clone, position, 0f), 0f);
            Assert.Less((position - new Vec2(81f, 80f)).Length, 6.1f);
        }

        [Test]
        public void FreePositionFailsWhenEverythingIsBlocked()
        {
            var world = new PhysicsWorld(new WorldSettings());
            CreateCircle(world, new Vec2(80f, 80f), 20f, 100f, BodyFlags.FixedPosition);
            var body = CreateCircle(world, new Vec2(10f, 10f), 1f, 1f);

            Assert.IsFalse(world.TryFindFreePosition(body, new Vec2(80f, 80f), 5f, out var position));
            Assert.Greater(world.GetOverlap(body, position, 0f), 0f);
        }

        [Test]
        public void SameInputGivesBitIdenticalResults()
        {
            Assert.AreEqual(RunCrowd(), RunCrowd());
        }

        private static string RunCrowd()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var target = world.CreateBody(BodyDef.Create(new Vec2(100f, 100f), 1.5f));
            world.AddPolygon(target, Polygon.Regular(5, 2f));

            var random = new Utilites.PcgRandom(7);
            var drones = new BodyId[40];
            for (var i = 0; i < drones.Length; ++i)
            {
                var angle = 2f * Utilites.MathEx.PI * i / drones.Length;
                var position = new Vec2(100f + 8f * (float)Math.Cos(angle), 100f + 8f * (float)Math.Sin(angle));
                drones[i] = world.CreateBody(BodyDef.Create(position, 0.14f));
                if (i % 3 == 0) world.AddPolygon(drones[i], Polygon.Regular(4, 0.15f + 0.2f * random.NextFloat()));
                else world.AddCircle(drones[i], new Circle(Vec2.Zero, 0.15f + 0.2f * random.NextFloat()));
            }

            for (var step = 0; step < 100; ++step)
            {
                foreach (var drone in drones)
                    world.ApplyForceToCenter(drone, 20f * world.Torus.Delta(world.GetWorldCenter(drone), new Vec2(100f, 100f)).Normalized);
                world.Step(Dt);
            }

            var hash = new System.Text.StringBuilder();
            foreach (var drone in drones)
            {
                var p = world.GetPosition(drone);
                hash.Append(BitConverter.SingleToInt32Bits(p.X)).Append(',').Append(BitConverter.SingleToInt32Bits(p.Y)).Append(';');
            }

            return hash.ToString();
        }

        private static BodyId CreateCircle(PhysicsWorld world, Vec2 position, float radius, float mass, BodyFlags flags = BodyFlags.None)
        {
            var body = world.CreateBody(BodyDef.Create(position, mass, flags));
            world.AddCircle(body, new Circle(Vec2.Zero, radius));
            return body;
        }
    }
}
