using System;
using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class JointTests
    {
        private const float Dt = 0.02f;
        private const float Deg = Utilites.MathEx.Deg2Rad;

        [Test]
        public void HingeHoldsAWhirlingBody()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var pivot = CreateCircle(world, new Vec2(100f, 100f), 0.5f, 1f, BodyFlags.FixedPosition | BodyFlags.FixedRotation);
            var bob = CreateCircle(world, new Vec2(102f, 100f), 0.3f, 1f);
            world.SetLinearVelocity(bob, new Vec2(0f, 10f));
            var joint = world.CreateHinge(new HingeDef { BodyA = pivot, BodyB = bob, LocalAnchorB = new Vec2(-2f, 0f) });

            var maxGap = 0f;
            for (var i = 0; i < 250; ++i)
            {
                world.Step(Dt);
                maxGap = Math.Max(maxGap, world.GetJointGap(joint));
            }

            Assert.Less(maxGap, 1e-3f);
            Assert.AreEqual(2f, world.Torus.Delta(world.GetPosition(pivot), world.GetPosition(bob)).Length, 1e-3f);
            Assert.AreEqual(10f, world.GetLinearVelocity(bob).Length, 0.2f);
            Assert.AreEqual(new Vec2(100f, 100f), world.GetPosition(pivot));
        }

        [TestCase(10f, 40f)]
        [TestCase(-10f, -75f)]
        public void AngleLimitStopsTheTurn(float spin, float limitDegrees)
        {
            var world = new PhysicsWorld(new WorldSettings());
            var anchor = CreateCircle(world, new Vec2(50f, 50f), 0.2f, 1f, BodyFlags.FixedPosition | BodyFlags.FixedRotation);
            var body = CreateBox(world, new Vec2(51f, 50f), 1f);
            world.SetAngularVelocity(body, spin);
            var joint = world.CreateHinge(new HingeDef
            {
                BodyA = anchor, BodyB = body, LocalAnchorA = new Vec2(1f, 0f),
                EnableLimit = true, LowerAngle = -75f * Deg, UpperAngle = 40f * Deg,
            });

            var extreme = 0f;
            for (var i = 0; i < 100; ++i)
            {
                world.Step(Dt);
                var angle = world.GetJointAngle(joint);
                if (Math.Abs(angle) > Math.Abs(extreme)) extreme = angle;
            }

            Assert.AreEqual(limitDegrees, extreme / Deg, 0.5f);
            Assert.Less(world.GetJointGap(joint), 1e-3f);
        }

        [Test]
        public void ReferenceAngleIsTheCallersInput()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = CreateCircle(world, new Vec2(50f, 50f), 0.2f, 1f, BodyFlags.FixedPosition | BodyFlags.FixedRotation);
            var b = CreateBox(world, new Vec2(50f, 50f), 1f, 0.5f);
            var joint = world.CreateHinge(new HingeDef { BodyA = a, BodyB = b, EnableLimit = true, LowerAngle = -0.1f, UpperAngle = 0.1f });

            Assert.AreEqual(0.5f, world.GetJointAngle(joint), 1e-6f);

            for (var i = 0; i < 50; ++i) world.Step(Dt);

            Assert.AreEqual(0.1f, world.GetJointAngle(joint), 0.01f);
        }

        [Test]
        public void JointAngleWrapsIntoHalfOpenRange()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = CreateBox(world, new Vec2(50f, 50f), 1f, 3f);
            var b = CreateBox(world, new Vec2(52f, 50f), 1f, -3f);
            var joint = world.CreateHinge(new HingeDef { BodyA = a, BodyB = b, ReferenceAngle = 0f });

            Assert.AreEqual(2f * Math.PI - 6.0, world.GetJointAngle(joint), 1e-5);
            Assert.AreEqual(Utilites.MathEx.PI, PhysicsWorld.UnwindAngle(-Utilites.MathEx.PI));
            Assert.AreEqual(Utilites.MathEx.PI, PhysicsWorld.UnwindAngle(Utilites.MathEx.PI));
        }

        // The chain hits its limits and spins, so it is chaotic. Across the seam each torus offset rounds at the precision
        // of coordinates near the map size (about 1.5e-5 on map 200), and the chaos grows that difference (to about
        // 5e-3 after 60 steps; 3e-4 away from the seam). 20 steps keep it below the tolerance.
        [Test]
        public void ChainAcrossTheSeamMatchesTheChainInTheMiddle()
        {
            var middle = new PhysicsWorld(new WorldSettings());
            var seam = new PhysicsWorld(new WorldSettings());
            var chainMiddle = CreateChain(middle, new Vec2(100f, 100f), 6);
            var chainSeam = CreateChain(seam, new Vec2(199.5f, 199.7f), 6);

            foreach (var (world, chain) in new[] { (middle, chainMiddle), (seam, chainSeam) })
            {
                world.SetLinearVelocity(chain[0], new Vec2(20f, 15f));
                world.SetAngularVelocity(chain[0], 3f);
            }

            for (var i = 0; i < 20; ++i)
            {
                middle.Step(Dt);
                seam.Step(Dt);
            }

            for (var i = 1; i < chainMiddle.Length; ++i)
            {
                var offsetMiddle = middle.Torus.Delta(middle.GetPosition(chainMiddle[0]), middle.GetPosition(chainMiddle[i]));
                var offsetSeam = seam.Torus.Delta(seam.GetPosition(chainSeam[0]), seam.GetPosition(chainSeam[i]));
                AssertVec(offsetMiddle, offsetSeam, 2e-3f);
                Assert.AreEqual(middle.GetAngle(chainMiddle[i]), seam.GetAngle(chainSeam[i]), 1e-3f);
            }

            var moved = middle.Torus.Delta(new Vec2(100f, 100f), middle.GetPosition(chainMiddle[0]));
            AssertVec(moved, seam.Torus.Delta(new Vec2(199.5f, 199.7f), seam.GetPosition(chainSeam[0])), 1e-3f);
            Assert.Greater(moved.Length, 2f);
        }

        [TestCase(150.3f, 20.7f)]
        [TestCase(199.9f, 0.05f)]
        public void TeleportMovesTheHingedGroup(float x, float y)
        {
            var world = new PhysicsWorld(new WorldSettings());
            var chain = CreateChain(world, new Vec2(30f, 40f), 5);
            var other = CreateCircle(world, new Vec2(10f, 10f), 0.5f, 1f);
            world.SetLinearVelocity(chain[0], new Vec2(8f, -3f));
            world.SetAngularVelocity(chain[0], 2f);
            for (var i = 0; i < 10; ++i) world.Step(Dt);

            var head = chain[0];
            var offsets = new Vec2[chain.Length];
            var rotations = new Rot[chain.Length];
            var velocities = new Vec2[chain.Length];
            var spins = new float[chain.Length];
            for (var i = 0; i < chain.Length; ++i)
            {
                offsets[i] = world.Torus.Delta(world.GetPosition(head), world.GetPosition(chain[i]));
                rotations[i] = world.GetRotation(chain[i]);
                velocities[i] = world.GetLinearVelocity(chain[i]);
                spins[i] = world.GetAngularVelocity(chain[i]);
            }

            var otherPosition = world.GetPosition(other);
            world.TeleportGroup(head, new Vec2(x, y));

            AssertVec(world.Torus.Wrap(new Vec2(x, y)), world.GetPosition(head), 0f);
            for (var i = 0; i < chain.Length; ++i)
            {
                var offset = world.Torus.Delta(world.GetPosition(head), world.GetPosition(chain[i]));
                AssertVec(offsets[i], offset, 2f * Ulp(Math.Max(x, y)));
                Assert.AreEqual(rotations[i].C, world.GetRotation(chain[i]).C);
                Assert.AreEqual(rotations[i].S, world.GetRotation(chain[i]).S);
                Assert.AreEqual(velocities[i], world.GetLinearVelocity(chain[i]));
                Assert.AreEqual(spins[i], world.GetAngularVelocity(chain[i]));
            }

            Assert.AreEqual(otherPosition, world.GetPosition(other));
        }

        [Test]
        public void TeleportWithATurnTurnsTheGroup()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var chain = CreateChain(world, new Vec2(30f, 40f), 4);
            world.SetLinearVelocity(chain[2], new Vec2(0f, 5f));
            var offset = world.Torus.Delta(world.GetPosition(chain[0]), world.GetPosition(chain[3]));

            world.TeleportGroup(chain[0], new Vec2(80f, 90f), 0.5f * Utilites.MathEx.PI);

            AssertVec(new Vec2(-offset.Y, offset.X), world.Torus.Delta(world.GetPosition(chain[0]), world.GetPosition(chain[3])), 1e-4f);
            Assert.AreEqual(0.5f * Utilites.MathEx.PI, world.GetAngle(chain[3]), 1e-5f);
            AssertVec(new Vec2(-5f, 0f), world.GetLinearVelocity(chain[2]), 1e-5f);
        }

        [Test]
        public void JoinedBodiesDoNotCollideUnlessAsked()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var a = CreateCircle(world, new Vec2(50f, 50f), 0.5f, 1f);
            var b = CreateCircle(world, new Vec2(50.6f, 50f), 0.5f, 1f);
            world.Step(Dt);
            Assert.AreEqual(1, world.ContactCount);
            Assert.AreEqual(1, world.BeginEvents.Length);

            var joint = world.CreateHinge(new HingeDef { BodyA = a, BodyB = b, LocalAnchorA = new Vec2(0.3f, 0f), LocalAnchorB = new Vec2(-0.3f, 0f) });
            Assert.AreEqual(0, world.ContactCount);
            world.Step(Dt);
            Assert.AreEqual(0, world.ContactCount);
            Assert.AreEqual(1, world.EndEvents.Length);

            world.DestroyJoint(joint);
            Assert.IsFalse(world.IsAlive(joint));
            world.Step(Dt);
            Assert.AreEqual(1, world.ContactCount);

            world.CreateHinge(new HingeDef { BodyA = a, BodyB = b, LocalAnchorA = new Vec2(0.3f, 0f), LocalAnchorB = new Vec2(-0.3f, 0f), CollideConnected = true });
            world.Step(Dt);
            Assert.AreEqual(1, world.ContactCount);
        }

        [Test]
        public void DestroyingABodyDestroysItsJoints()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var chain = CreateChain(world, new Vec2(30f, 40f), 3);
            var first = world.GetJointId(0);
            var second = world.GetJointId(1);

            world.DestroyBody(chain[1]);

            Assert.IsFalse(world.IsAlive(first));
            Assert.IsFalse(world.IsAlive(second));

            var joint = world.CreateHinge(new HingeDef { BodyA = chain[0], BodyB = chain[2] });
            world.Step(Dt);
            Assert.IsTrue(world.IsAlive(joint));
            world.DestroyBody(chain[0]);
            Assert.IsFalse(world.IsAlive(joint));
            world.Step(Dt);
        }

        [Test]
        public void TotalContactImpulseEqualsTheMomentumChange()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var mover = CreateCircle(world, new Vec2(50f, 50f), 0.5f, 2f);
            CreateCircle(world, new Vec2(51.2f, 50.1f), 0.5f, 1f, BodyFlags.FixedPosition);
            world.SetLinearVelocity(mover, new Vec2(10f, 0f));

            var pushed = 0;
            for (var step = 0; step < 20; ++step)
            {
                var before = world.GetLinearVelocity(mover);
                world.Step(Dt);
                var change = 2f * (world.GetLinearVelocity(mover) - before);

                var impulse = Vec2.Zero;
                for (var i = 0; i < world.ContactCount; ++i)
                {
                    ref readonly var contact = ref world.GetContact(i);
                    var manifold = contact.Manifold;
                    var tangent = Vec2.RightPerp(manifold.Normal);
                    for (var j = 0; j < manifold.PointCount; ++j)
                    {
                        var point = Manifold.Point(ref manifold, j);
                        var p = point.TotalNormalImpulse * manifold.Normal + point.TotalTangentImpulse * tangent;
                        impulse += contact.BodyB.Equals(mover) ? p : -p;
                    }
                }

                if (impulse.Length > 0f) ++pushed;
                AssertVec(change, impulse, 1e-4f * Math.Max(1f, change.Length));
            }

            Assert.Greater(pushed, 0);
        }

        [Test]
        public void RopePullsOnlyWhenTaut()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var owner = CreateCircle(world, new Vec2(50f, 50f), 0.5f, 1f);
            var target = CreateCircle(world, new Vec2(52f, 50f), 0.5f, 1f);
            var rope = world.CreateRope(new RopeDef { BodyA = owner, BodyB = target, Length = 3f });
            Assert.AreEqual(JointKind.Rope, world.GetJointKind(rope));

            // Slack: the target closes in, the rope does nothing.
            world.SetLinearVelocity(target, new Vec2(-5f, 0f));
            world.Step(Dt);
            Assert.AreEqual(Vec2.Zero, world.GetJointImpulse(rope));
            AssertVec(new Vec2(-5f, 0f), world.GetLinearVelocity(target), 0f);

            // Taut: the target leaves at 20 u/s and drags the owner.
            world.SetLinearVelocity(target, new Vec2(20f, 0f));
            var maxGap = 0f;
            for (var i = 0; i < 50; ++i)
            {
                world.Step(Dt);
                maxGap = Math.Max(maxGap, world.GetJointGap(rope));
            }

            Assert.Less(maxGap, 3f * 1.05f);
            Assert.AreEqual(3f, world.GetJointGap(rope), 0.01f);
            AssertVec(world.GetLinearVelocity(owner), world.GetLinearVelocity(target), 0.05f);
            Assert.AreEqual(10f, world.GetLinearVelocity(owner).X, 0.05f);
        }

        [Test]
        public void RopeImpulseEqualsTheMomentumChange()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var owner = CreateBox(world, new Vec2(40f, 40f), 100f);
            var target = CreateBox(world, new Vec2(43f, 40.5f), 1f);
            var rope = world.CreateRope(new RopeDef { BodyA = owner, BodyB = target, LocalAnchorA = new Vec2(0.4f, 0.1f), LocalAnchorB = new Vec2(-0.3f, 0f), Length = 2f });
            world.SetLinearVelocity(target, new Vec2(15f, 5f));

            var start = 100f * world.GetLinearVelocity(owner) + world.GetLinearVelocity(target);
            for (var i = 0; i < 30; ++i)
            {
                var before = world.GetLinearVelocity(target);
                world.Step(Dt);
                var change = world.GetLinearVelocity(target) - before;
                AssertVec(change, world.GetJointImpulse(rope), 1e-4f * Math.Max(1f, change.Length));
            }

            AssertVec(start, 100f * world.GetLinearVelocity(owner) + world.GetLinearVelocity(target), 1e-3f * start.Length);
        }

        [Test]
        public void RopeToAnImmovableBodyNeverMovesIt()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var starbase = CreateCircle(world, new Vec2(199.8f, 0.1f), 1f, 10f, BodyFlags.FixedPosition);
            var drone = CreateCircle(world, new Vec2(2f, 0.1f), 0.3f, 1f);
            var rope = world.CreateRope(new RopeDef { BodyA = starbase, BodyB = drone, Length = 2.5f });
            world.SetLinearVelocity(drone, new Vec2(30f, 10f));

            var maxGap = 0f;
            for (var i = 0; i < 100; ++i)
            {
                world.Step(Dt);
                maxGap = Math.Max(maxGap, world.GetJointGap(rope));
            }

            Assert.AreEqual(new Vec2(199.8f, 0.1f), world.GetPosition(starbase));
            Assert.AreEqual(Vec2.Zero, world.GetLinearVelocity(starbase));
            Assert.Less(maxGap, 2.5f * 1.25f);
            Assert.AreEqual(2.5f, world.GetJointGap(rope), 0.02f);
        }

        [Test]
        public void ShorterRopeReelsIn()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var owner = CreateCircle(world, new Vec2(50f, 50f), 0.5f, 1f, BodyFlags.FixedPosition);
            var target = CreateCircle(world, new Vec2(55f, 50f), 0.5f, 1f);
            var rope = world.CreateRope(new RopeDef { BodyA = owner, BodyB = target, Length = 5f });

            world.SetRopeLength(rope, 3f);
            for (var i = 0; i < 50; ++i) world.Step(Dt);

            Assert.AreEqual(3f, world.GetRopeLength(rope));
            Assert.AreEqual(3f, world.GetJointGap(rope), 0.05f);
            Assert.Throws<ArgumentException>(() => world.SetRopeLength(world.CreateHinge(new HingeDef { BodyA = owner, BodyB = target }), 1f));
        }

        [Test]
        public void TeleportLeavesTheRopesOtherEnd()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var chain = CreateChain(world, new Vec2(30f, 40f), 3);
            var target = CreateCircle(world, new Vec2(34f, 40f), 0.5f, 1f);
            world.CreateRope(new RopeDef { BodyA = chain[0], BodyB = target, Length = 4f });

            world.TeleportGroup(chain[0], new Vec2(130f, 40f));

            Assert.AreEqual(new Vec2(34f, 40f), world.GetPosition(target));
            AssertVec(new Vec2(128f, 40f), world.GetPosition(chain[2]), 1e-4f);
        }

        // ω from the rope impulse through an off-centre anchor: r × J / I (the P2 spin limit, 1%). The step spreads the
        // impulse over its substeps while the body turns, so r turns too; a lever arm across the pull and a slow jerk
        // keep that effect small.
        [Test]
        public void OffCentrePullTurnsTheBody()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var anchor = CreateCircle(world, new Vec2(60f, 50f), 0.5f, 1f, BodyFlags.FixedPosition);
            var hull = CreateBox(world, new Vec2(50f, 50f), 2f);
            var local = new Vec2(0f, 0.2f);
            var rope = world.CreateRope(new RopeDef { BodyA = anchor, BodyB = hull, LocalAnchorB = local, Length = 10f });
            world.SetLinearVelocity(hull, new Vec2(-2f, 0f));

            var r = world.GetRotation(hull).Rotate(local - world.GetLocalCenter(hull));
            world.Step(Dt);
            var impulse = world.GetJointImpulse(rope);

            Assert.Greater(impulse.Length, 0.5f);
            var expected = Vec2.Cross(r, impulse) / world.GetInertia(hull);
            Assert.AreEqual(expected, world.GetAngularVelocity(hull), 0.01f * Math.Abs(expected));
        }

        [Test]
        public void AttachedBulletKeepsItsLocalPose()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var hull = CreateBox(world, new Vec2(199.5f, 100f), 1f);
            world.SetLinearVelocity(hull, new Vec2(12f, 3f));
            world.SetAngularVelocity(hull, 4f);
            var hook = world.CreateBullet(new BulletDef { Position = new Vec2(199.9f, 100.1f), Angle = 1f }, new Circle(Vec2.Zero, 0.05f));
            world.AttachBullet(hook, hull);
            var local = world.GetBulletLocalPose(hook);

            for (var i = 0; i < 50; ++i)
            {
                world.Step(Dt);
                foreach (var hit in world.BulletHits) Assert.AreNotEqual(hull, hit.Target.Body);

                var pose = new Pose(world.GetPosition(hull), world.GetRotation(hull));
                Assert.AreEqual(world.Torus.Wrap(pose.TransformPoint(local.P)), world.GetBulletPosition(hook));
                Assert.AreEqual(Rot.Mul(pose.Q, local.Q).C, world.GetBulletRotation(hook).C);
                Assert.AreEqual(Rot.Mul(pose.Q, local.Q).S, world.GetBulletRotation(hook).S);
            }

            Assert.AreEqual(local.P, world.GetBulletLocalPose(hook).P);
            Assert.AreEqual(hull, world.GetBulletParent(hook));

            world.TeleportGroup(hull, new Vec2(20f, 20f));
            AssertVec(world.Torus.Wrap(new Pose(world.GetPosition(hull), world.GetRotation(hull)).TransformPoint(local.P)), world.GetBulletPosition(hook), 0f);
            Assert.Throws<InvalidOperationException>(() => world.SetBulletPose(hook, Vec2.Zero, 0f));
        }

        [Test]
        public void AttachedBulletHitsWhatItsParentMeets()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var hull = CreateBox(world, new Vec2(50f, 50f), 1f);
            var wall = CreateCircle(world, new Vec2(53f, 50f), 1f, 1f, BodyFlags.FixedPosition);
            world.SetLinearVelocity(hull, new Vec2(20f, 0f));
            var mine = world.CreateBullet(new BulletDef(), new Circle(Vec2.Zero, 0.1f));
            world.AttachBullet(mine, hull, new Pose(new Vec2(0.6f, 0f), Rot.Identity));

            var hitWall = false;
            for (var i = 0; i < 20 && !hitWall; ++i)
            {
                world.Step(Dt);
                foreach (var hit in world.BulletHits) hitWall |= hit.Target.Body.Equals(wall);
            }

            Assert.IsTrue(hitWall);
        }

        [Test]
        public void BulletComesFreeWhenItsParentIsDestroyed()
        {
            var world = new PhysicsWorld(new WorldSettings());
            var hull = CreateBox(world, new Vec2(50f, 50f), 1f);
            world.SetLinearVelocity(hull, new Vec2(10f, 0f));
            var hook = world.CreateBullet(new BulletDef(), new Circle(Vec2.Zero, 0.05f));
            world.AttachBullet(hook, hull, new Pose(new Vec2(0.4f, 0f), Rot.Identity));
            world.Step(Dt);

            var position = world.GetBulletPosition(hook);
            world.DestroyBody(hull);
            world.Step(Dt);

            Assert.IsTrue(world.GetBulletParent(hook).IsNull);
            AssertVec(position + Dt * new Vec2(10f, 0f), world.GetBulletPosition(hook), 1e-4f);
            world.DetachBullet(hook);
        }

        // A chain of boxes along -x, 1 u apart, each joined to the one before at their touching ends, ±40°.
        private static BodyId[] CreateChain(PhysicsWorld world, Vec2 head, int count)
        {
            var chain = new BodyId[count];
            for (var i = 0; i < count; ++i)
            {
                chain[i] = CreateBox(world, head - new Vec2(i, 0f), 1f - 0.05f * i);
                if (i == 0) continue;

                world.CreateHinge(new HingeDef
                {
                    BodyA = chain[i - 1], BodyB = chain[i],
                    LocalAnchorA = new Vec2(-0.5f, 0f), LocalAnchorB = new Vec2(0.5f, 0f),
                    EnableLimit = true, LowerAngle = -40f * Deg, UpperAngle = 40f * Deg,
                });
            }

            return chain;
        }

        private static BodyId CreateBox(PhysicsWorld world, Vec2 position, float mass, float angle = 0f)
        {
            var body = world.CreateBody(new BodyDef { Position = position, Angle = angle, Mass = mass });
            world.AddPolygon(body, Polygon.FromPoints(new[] { new Vec2(-0.45f, -0.2f), new Vec2(0.45f, -0.2f), new Vec2(0.45f, 0.2f), new Vec2(-0.45f, 0.2f) }));
            return body;
        }

        private static BodyId CreateCircle(PhysicsWorld world, Vec2 position, float radius, float mass, BodyFlags flags = BodyFlags.None)
        {
            var body = world.CreateBody(BodyDef.Create(position, mass, flags));
            world.AddCircle(body, new Circle(Vec2.Zero, radius));
            return body;
        }

        // Distance from a positive float to the next one.
        private static float Ulp(float value) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value) + 1) - value;
    }
}
