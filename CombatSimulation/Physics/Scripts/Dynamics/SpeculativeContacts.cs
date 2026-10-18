using System;

namespace CombatSimulation.Physics
{
    public sealed partial class PhysicsWorld
    {
        // Speculative contacts checked with a time of impact in the last step.
        public int TimeOfImpactCount { get; private set; }

        // Speculative contacts cleared in the last step: their shapes do not come within the speculative distance.
        public int DroppedSpeculativeCount { get; private set; }

        // Before the solve (docs/combat_simulation.md, "Ghost passes"). The manifold normal of a speculative contact
        // comes from the closest features at the start of the step, and the solver stops any approach along it.
        // A body that passes close by but does not touch would stop too (ghost collision). Such a contact is cleared
        // for this step when the time of impact shows that the shapes do not come within the speculative distance.
        //
        // The time of impact uses the free motion of both bodies. Two kinds of contacts keep their manifold anyway:
        // 1. The solver cannot act on them: the gap is larger than any closing in the step. The result is the same.
        // 2. The bodies are in an island with a contact that the solver acts on (touching, deep, a time-of-impact
        //    hit, or a whole-body depenetration). An island is a group of movable bodies linked by contacts with
        //    manifold points. Velocity changes spread through the island within the step, so the free motion is
        //    no longer a safe prediction: a crowd pushes a drone into its neighbour, or a hull that hits with one part
        //    turns another part into the target.
        private void CheckSpeculativeContacts(float dt)
        {
            TimeOfImpactCount = 0;
            DroppedSpeculativeCount = 0;

            if (_islands.Length < _bodyCount)
            {
                _islands = new int[Math.Max(_bodyCount, 2 * _islands.Length)];
                _islandFlags = new bool[_islands.Length];
            }

            for (var i = 0; i < _bodyCount; ++i)
            {
                _islands[i] = i;
                _islandFlags[i] = false;
            }

            var speculative = Settings.SpeculativeDistance;
            var candidates = 0;
            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                contact.SpeculativeState = SpeculativeState.None;
                if (contact.Manifold.PointCount == 0) continue;

                Link(contact.IndexA, contact.IndexB);
                if (contact.Suppressed || contact.Manifold.MinSeparation <= speculative) continue;
                if (!CanClose(contact, dt)) continue;

                contact.SpeculativeState = SpeculativeState.Check;
                ++candidates;
            }

            if (candidates == 0) return;

            // Islands with a contact that the solver acts on with any motion.
            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                if (contact.Manifold.PointCount > 0 && (contact.Suppressed || contact.Manifold.MinSeparation <= speculative))
                    MarkIsland(contact);
            }

            // Time of impact in the other islands. A hit marks the island after all checks, so the order of the
            // contacts does not matter.
            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                if (contact.SpeculativeState != SpeculativeState.Check || IsMarked(contact)) continue;

                ++TimeOfImpactCount;
                contact.SpeculativeState = ShapesMeet(contact, dt) ? SpeculativeState.Hit : SpeculativeState.Miss;
            }

            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                if (contact.SpeculativeState == SpeculativeState.Hit) MarkIsland(contact);
            }

            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                if (contact.SpeculativeState != SpeculativeState.Miss || IsMarked(contact)) continue;

                contact.Manifold.PointCount = 0;
                ++DroppedSpeculativeCount;
            }
        }

        // Union of the islands of two bodies. A body that can neither move nor turn passes no velocity on, so it
        // links nothing. The lower index becomes the root.
        private void Link(int a, int b)
        {
            if (!CanMove(a) || !CanMove(b)) return;

            a = FindIsland(a);
            b = FindIsland(b);
            if (a < b) _islands[b] = a;
            else if (b < a) _islands[a] = b;
        }

        private int FindIsland(int index)
        {
            var root = index;
            while (_islands[root] != root) root = _islands[root];

            while (_islands[index] != root)
            {
                var next = _islands[index];
                _islands[index] = root;
                index = next;
            }

            return root;
        }

        private void MarkIsland(in Contact contact)
        {
            if (CanMove(contact.IndexA)) _islandFlags[FindIsland(contact.IndexA)] = true;
            if (CanMove(contact.IndexB)) _islandFlags[FindIsland(contact.IndexB)] = true;
        }

        private bool IsMarked(in Contact contact) =>
            CanMove(contact.IndexA) && _islandFlags[FindIsland(contact.IndexA)] ||
            CanMove(contact.IndexB) && _islandFlags[FindIsland(contact.IndexB)];

        private bool CanMove(int index) => _bodies[index].InvMass > 0f || _bodies[index].InvInertia > 0f;

        // True when a manifold point may close its gap with the velocities, forces and damping of both bodies.
        // The horizon is the step plus one substep: the relax pass after the last substep already stops an approach
        // that would close the gap in the next substep.
        private bool CanClose(in Contact contact, float dt)
        {
            ref var bodyA = ref _bodies[contact.IndexA];
            ref var bodyB = ref _bodies[contact.IndexB];
            var normal = contact.Manifold.Normal;
            var horizon = dt + dt / Settings.Substeps;

            // Bounds of the change of the linear velocity and of the angular speed within the step.
            var deltaA = dt * (bodyA.Force.Length * bodyA.InvMass + bodyA.LinearDamping * bodyA.V.Length);
            var deltaB = dt * (bodyB.Force.Length * bodyB.InvMass + bodyB.LinearDamping * bodyB.V.Length);
            var spinA = Math.Abs(bodyA.W) + dt * Math.Abs(bodyA.Torque) * bodyA.InvInertia;
            var spinB = Math.Abs(bodyB.W) + dt * Math.Abs(bodyB.Torque) * bodyB.InvInertia;
            var approach = Math.Max(0f, Vec2.Dot(bodyA.V - bodyB.V, normal)) + deltaA + deltaB;

            for (var j = 0; j < contact.Manifold.PointCount; ++j)
            {
                var point = j == 0 ? contact.Manifold.Point0 : contact.Manifold.Point1;
                var closing = horizon * (approach + spinA * point.AnchorA.Length + spinB * point.AnchorB.Length);
                if (closing >= point.Separation) return true;
            }

            return false;
        }

        // Time of impact of the two shapes over the free motion of their bodies in this step.
        private bool ShapesMeet(in Contact contact, float dt)
        {
            var substeps = Settings.Substeps;
            if (_sweepA == null || _sweepA.Substeps != substeps)
            {
                _sweepA = new Sweep(substeps);
                _sweepB = new Sweep(substeps);
            }

            ref var bodyA = ref _bodies[contact.IndexA];
            ref var bodyB = ref _bodies[contact.IndexB];
            ref var shapeA = ref _shapes[(int)(contact.Key >> 32)];
            ref var shapeB = ref _shapes[(int)(contact.Key & 0xffffffff)];

            // B in the torus image nearest to A, as in UpdateManifold.
            var originB = bodyA.Origin + Torus.Delta(bodyA.Origin, bodyB.Origin);
            _sweepA.SetFree(bodyA, bodyA.Center, dt);
            _sweepB.SetFree(bodyB, originB + bodyB.Q.Rotate(bodyB.LocalCenter), dt);

            var proxyA = DistanceProxy.FromShape(shapeA);
            var proxyB = DistanceProxy.FromShape(shapeB);
            var target = Settings.SpeculativeDistance;
            var tolerance = 0.25f * Settings.LinearSlop;

            // One line each: the separation is concave over the whole step.
            if (_sweepA.Straight && _sweepB.Straight)
                return TimeOfImpact.Compute(proxyA, _sweepA, proxyB, _sweepB, target, tolerance, 0f, 1f, out _) != TimeOfImpactState.Separated;

            // Otherwise one interval per substep (the centres move on a line within a substep), split further while
            // the bodies turn. The rotation bends the path within an interval; its largest dip is added to the target.
            var segmentsPerSubstep = 1;
            var dip = 0f;
            var spin = _sweepA.MaxAngularSpeed + _sweepB.MaxAngularSpeed;
            if (spin > 0f)
            {
                var extent = ShapeExtent(shapeA, bodyA) + ShapeExtent(shapeB, bodyB);
                var speed = _sweepA.MaxSpeed + _sweepB.MaxSpeed;
                var distance = (_sweepB.Center - _sweepA.Center).Length + speed * dt;

                // Bound of the second time derivative of a support point, seen along an axis that may turn with
                // either body: centripetal, Coriolis and the turn of the line between the centres.
                var curvature = 0.125f * (spin * spin * (distance + 3f * extent) + 2f * spin * speed);
                var h = dt / substeps;
                var needed = h * (float)Math.Sqrt(curvature / MaxDip);
                segmentsPerSubstep = Math.Min(MaxSegmentsPerSubstep, Math.Max(1, (int)Math.Ceiling(needed)));
                var tau = h / segmentsPerSubstep;
                dip = curvature * tau * tau;
            }

            var segments = substeps * segmentsPerSubstep;
            for (var k = 0; k < segments; ++k)
            {
                var state = TimeOfImpact.Compute(proxyA, _sweepA, proxyB, _sweepB, target + dip, tolerance,
                    (float)k / segments, (float)(k + 1) / segments, out _);
                if (state != TimeOfImpactState.Separated) return true;
            }

            return false;
        }

        // Farthest point of the shape from the body's centre of mass.
        private static float ShapeExtent(in Shape shape, in Body body) => (shape.LocalBoundCenter - body.LocalCenter).Length + shape.BoundRadius;

        // Dip of the path within one interval that the splitting aims for, and the most intervals per substep.
        // A faster spin keeps a larger dip, so it keeps more contacts (the old behaviour).
        private const float MaxDip = 0.005f;
        private const int MaxSegmentsPerSubstep = 4;

        private int[] _islands = new int[64];
        private bool[] _islandFlags = new bool[64];
        private Sweep _sweepA;
        private Sweep _sweepB;
    }

    internal enum SpeculativeState : byte
    {
        None,

        // The solver may act on the contact; the time of impact decides when the bodies move freely.
        Check,

        // The shapes meet within the step.
        Hit,

        // The shapes do not meet within the step.
        Miss,
    }
}
