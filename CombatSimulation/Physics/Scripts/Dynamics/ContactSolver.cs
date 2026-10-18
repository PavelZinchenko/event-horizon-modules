using System;

namespace CombatSimulation.Physics
{
    // Soft-step contact solver. Adapted from Box2D v3 (MIT, Erin Catto): b2SolverStage order,
    // soft contacts (b2MakeSoft), relax pass and restitution pass.
    //
    // Per step: prepare once, then for each substep integrate velocities, warm start, solve with bias,
    // integrate positions and relax without bias. Restitution runs once after the substeps.
    //
    // A body pair in whole-body depenetration (Separation) has its contacts suppressed. It gets one constraint
    // instead: the normal is the separation direction, the anchors are the centres of mass, friction and
    // restitution are 0, and the separation is minus the remaining clearance.
    internal sealed class ContactSolver
    {
        public void Solve(Body[] bodies, int bodyCount, Contact[] contacts, int contactCount, Separation[] separations, int separationCount,
            float dt, WorldSettings settings)
        {
            var substeps = settings.Substeps;
            var h = dt / substeps;
            var invH = 1f / h;

            var contactHertz = Math.Min(settings.ContactHertz, 0.25f * substeps / dt);
            var contactSoftness = Softness.Make(contactHertz, settings.DampingRatio, h);
            var staticSoftness = Softness.Make(2f * contactHertz, settings.DampingRatio, h);

            for (var i = 0; i < bodyCount; ++i)
            {
                bodies[i].DeltaPosition = Vec2.Zero;
                bodies[i].DeltaRotation = Rot.Identity;
            }

            Prepare(bodies, contacts, contactCount, contactSoftness, staticSoftness, settings.WarmStarting);
            PrepareSeparations(bodies, separations, separationCount, contactSoftness, staticSoftness, settings.WarmStarting);

            for (var i = 0; i < substeps; ++i)
            {
                IntegrateVelocities(bodies, bodyCount, h);
                WarmStart(bodies);
                SolveContacts(bodies, invH, settings.PushMaxVelocity, true);
                IntegratePositions(bodies, bodyCount, h);
                SolveContacts(bodies, invH, settings.PushMaxVelocity, false);
            }

            ApplyRestitution(bodies, settings.RestitutionThreshold);
            StoreImpulses(contacts, separations);
        }

        private void Prepare(Body[] bodies, Contact[] contacts, int contactCount, Softness contactSoftness, Softness staticSoftness, bool warmStarting)
        {
            if (_constraints.Length < contactCount) Array.Resize(ref _constraints, Math.Max(contactCount, _constraints.Length * 2));

            _constraintCount = 0;
            for (var i = 0; i < contactCount; ++i)
            {
                ref var contact = ref contacts[i];
                if (contact.Manifold.PointCount == 0 || contact.Suppressed) continue;

                ref var bodyA = ref bodies[contact.IndexA];
                ref var bodyB = ref bodies[contact.IndexB];
                ref var constraint = ref _constraints[_constraintCount++];

                constraint.ContactIndex = i;
                constraint.BodyA = contact.IndexA;
                constraint.BodyB = contact.IndexB;
                constraint.Normal = contact.Manifold.Normal;
                constraint.Friction = contact.Friction;
                constraint.Restitution = contact.Restitution;
                constraint.PointCount = contact.Manifold.PointCount;
                constraint.Softness = bodyA.InvMass == 0f || bodyB.InvMass == 0f ? staticSoftness : contactSoftness;

                var mA = bodyA.InvMass;
                var iA = bodyA.InvInertia;
                var mB = bodyB.InvMass;
                var iB = bodyB.InvInertia;
                var normal = constraint.Normal;
                var tangent = Vec2.RightPerp(normal);

                for (var j = 0; j < constraint.PointCount; ++j)
                {
                    ref var mp = ref Manifold.Point(ref contact.Manifold, j);
                    ref var cp = ref ConstraintPoint.Get(ref constraint, j);

                    cp.AnchorA = mp.AnchorA;
                    cp.AnchorB = mp.AnchorB;
                    cp.NormalImpulse = warmStarting ? mp.NormalImpulse : 0f;
                    cp.TangentImpulse = warmStarting ? mp.TangentImpulse : 0f;
                    cp.MaxNormalImpulse = 0f;

                    // Separation at the start of the step, without the anchor offset.
                    // The current separation is BaseSeparation + dot(d + rB' - rA', normal).
                    cp.BaseSeparation = mp.Separation - Vec2.Dot(cp.AnchorB - cp.AnchorA, normal);

                    var rnA = Vec2.Cross(cp.AnchorA, normal);
                    var rnB = Vec2.Cross(cp.AnchorB, normal);
                    var kNormal = mA + mB + iA * rnA * rnA + iB * rnB * rnB;
                    cp.NormalMass = kNormal > 0f ? 1f / kNormal : 0f;

                    var rtA = Vec2.Cross(cp.AnchorA, tangent);
                    var rtB = Vec2.Cross(cp.AnchorB, tangent);
                    var kTangent = mA + mB + iA * rtA * rtA + iB * rtB * rtB;
                    cp.TangentMass = kTangent > 0f ? 1f / kTangent : 0f;

                    var dv = bodyB.V + Vec2.Cross(bodyB.W, cp.AnchorB) - bodyA.V - Vec2.Cross(bodyA.W, cp.AnchorA);
                    cp.RelativeVelocity = Vec2.Dot(normal, dv);
                }
            }
        }

        // One constraint per separating body pair. ContactIndex is -(separation index + 1).
        private void PrepareSeparations(Body[] bodies, Separation[] separations, int separationCount, Softness contactSoftness, Softness staticSoftness, bool warmStarting)
        {
            if (_constraints.Length < _constraintCount + separationCount)
                Array.Resize(ref _constraints, Math.Max(_constraintCount + separationCount, _constraints.Length * 2));

            for (var i = 0; i < separationCount; ++i)
            {
                ref var separation = ref separations[i];
                ref var bodyA = ref bodies[separation.Reference];
                ref var bodyB = ref bodies[separation.Moving];
                ref var constraint = ref _constraints[_constraintCount++];

                constraint.ContactIndex = -(i + 1);
                constraint.BodyA = separation.Reference;
                constraint.BodyB = separation.Moving;
                constraint.Normal = separation.Normal;
                constraint.Friction = 0f;
                constraint.Restitution = 0f;
                constraint.PointCount = 1;
                constraint.Softness = bodyA.InvMass == 0f || bodyB.InvMass == 0f ? staticSoftness : contactSoftness;

                ref var cp = ref constraint.Point0;
                cp.AnchorA = Vec2.Zero;
                cp.AnchorB = Vec2.Zero;
                cp.NormalImpulse = warmStarting ? separation.NormalImpulse : 0f;
                cp.TangentImpulse = 0f;
                cp.MaxNormalImpulse = 0f;
                cp.BaseSeparation = -separation.Clearance;

                var k = bodyA.InvMass + bodyB.InvMass;
                cp.NormalMass = k > 0f ? 1f / k : 0f;
                cp.TangentMass = cp.NormalMass;
                cp.RelativeVelocity = Vec2.Dot(separation.Normal, bodyB.V - bodyA.V);
            }
        }

        private static void IntegrateVelocities(Body[] bodies, int bodyCount, float h)
        {
            for (var i = 0; i < bodyCount; ++i)
            {
                ref var body = ref bodies[i];
                if (!body.Alive) continue;

                if (body.InvMass > 0f)
                    body.V = (body.V + h * body.InvMass * body.Force) * (1f / (1f + h * body.LinearDamping));

                if (body.InvInertia > 0f)
                    body.W = (body.W + h * body.InvInertia * body.Torque) * (1f / (1f + h * body.AngularDamping));
            }
        }

        private static void IntegratePositions(Body[] bodies, int bodyCount, float h)
        {
            for (var i = 0; i < bodyCount; ++i)
            {
                ref var body = ref bodies[i];
                if (!body.Alive) continue;

                if (body.W != 0f) body.DeltaRotation = Rot.Integrate(body.DeltaRotation, h * body.W);
                if (body.InvMass > 0f) body.DeltaPosition += h * body.V;
            }
        }

        private void WarmStart(Body[] bodies)
        {
            for (var i = 0; i < _constraintCount; ++i)
            {
                ref var constraint = ref _constraints[i];
                ref var bodyA = ref bodies[constraint.BodyA];
                ref var bodyB = ref bodies[constraint.BodyB];
                var normal = constraint.Normal;
                var tangent = Vec2.RightPerp(normal);

                for (var j = 0; j < constraint.PointCount; ++j)
                {
                    ref var cp = ref ConstraintPoint.Get(ref constraint, j);
                    var p = cp.NormalImpulse * normal + cp.TangentImpulse * tangent;
                    bodyA.W -= bodyA.InvInertia * Vec2.Cross(cp.AnchorA, p);
                    bodyA.V -= bodyA.InvMass * p;
                    bodyB.W += bodyB.InvInertia * Vec2.Cross(cp.AnchorB, p);
                    bodyB.V += bodyB.InvMass * p;
                }
            }
        }

        private void SolveContacts(Body[] bodies, float invH, float pushMaxVelocity, bool useBias)
        {
            for (var i = 0; i < _constraintCount; ++i)
            {
                ref var constraint = ref _constraints[i];
                ref var bodyA = ref bodies[constraint.BodyA];
                ref var bodyB = ref bodies[constraint.BodyB];

                var mA = bodyA.InvMass;
                var iA = bodyA.InvInertia;
                var mB = bodyB.InvMass;
                var iB = bodyB.InvInertia;
                var vA = bodyA.V;
                var wA = bodyA.W;
                var vB = bodyB.V;
                var wB = bodyB.W;

                var normal = constraint.Normal;
                var dp = bodyB.DeltaPosition - bodyA.DeltaPosition;
                var softness = constraint.Softness;

                for (var j = 0; j < constraint.PointCount; ++j)
                {
                    ref var cp = ref ConstraintPoint.Get(ref constraint, j);

                    // Current separation from the motion of both bodies in this step.
                    var prA = bodyA.DeltaRotation.Rotate(cp.AnchorA);
                    var prB = bodyB.DeltaRotation.Rotate(cp.AnchorB);
                    var s = cp.BaseSeparation + Vec2.Dot(dp + prB - prA, normal);

                    var velocityBias = 0f;
                    var massScale = 1f;
                    var impulseScale = 0f;
                    if (s > 0f)
                    {
                        // Speculative: allow the bodies to close the gap in this substep, but no more.
                        velocityBias = s * invH;
                    }
                    else if (useBias)
                    {
                        velocityBias = Math.Max(softness.BiasRate * s, -pushMaxVelocity);
                        massScale = softness.MassScale;
                        impulseScale = softness.ImpulseScale;
                    }

                    var rA = cp.AnchorA;
                    var rB = cp.AnchorB;
                    var dv = vB + Vec2.Cross(wB, rB) - vA - Vec2.Cross(wA, rA);
                    var vn = Vec2.Dot(dv, normal);

                    var impulse = -cp.NormalMass * massScale * (vn + velocityBias) - impulseScale * cp.NormalImpulse;
                    var newImpulse = Math.Max(cp.NormalImpulse + impulse, 0f);
                    impulse = newImpulse - cp.NormalImpulse;
                    cp.NormalImpulse = newImpulse;
                    if (newImpulse > cp.MaxNormalImpulse) cp.MaxNormalImpulse = newImpulse;

                    var p = impulse * normal;
                    vA -= mA * p;
                    wA -= iA * Vec2.Cross(rA, p);
                    vB += mB * p;
                    wB += iB * Vec2.Cross(rB, p);
                }

                var tangent = Vec2.RightPerp(normal);
                for (var j = 0; j < constraint.PointCount; ++j)
                {
                    ref var cp = ref ConstraintPoint.Get(ref constraint, j);
                    var rA = cp.AnchorA;
                    var rB = cp.AnchorB;
                    var dv = vB + Vec2.Cross(wB, rB) - vA - Vec2.Cross(wA, rA);
                    var vt = Vec2.Dot(dv, tangent);

                    var impulse = -cp.TangentMass * vt;
                    var maxFriction = constraint.Friction * cp.NormalImpulse;
                    var newImpulse = Utilites.MathEx.Clamp(cp.TangentImpulse + impulse, -maxFriction, maxFriction);
                    impulse = newImpulse - cp.TangentImpulse;
                    cp.TangentImpulse = newImpulse;

                    var p = impulse * tangent;
                    vA -= mA * p;
                    wA -= iA * Vec2.Cross(rA, p);
                    vB += mB * p;
                    wB += iB * Vec2.Cross(rB, p);
                }

                bodyA.V = vA;
                bodyA.W = wA;
                bodyB.V = vB;
                bodyB.W = wB;
            }
        }

        private void ApplyRestitution(Body[] bodies, float threshold)
        {
            for (var i = 0; i < _constraintCount; ++i)
            {
                ref var constraint = ref _constraints[i];
                if (constraint.Restitution == 0f) continue;

                ref var bodyA = ref bodies[constraint.BodyA];
                ref var bodyB = ref bodies[constraint.BodyB];
                var normal = constraint.Normal;

                for (var j = 0; j < constraint.PointCount; ++j)
                {
                    ref var cp = ref ConstraintPoint.Get(ref constraint, j);

                    // Bounce only points that approached fast enough and pushed in this step.
                    if (cp.RelativeVelocity > -threshold || cp.MaxNormalImpulse == 0f) continue;

                    var rA = cp.AnchorA;
                    var rB = cp.AnchorB;
                    var dv = bodyB.V + Vec2.Cross(bodyB.W, rB) - bodyA.V - Vec2.Cross(bodyA.W, rA);
                    var vn = Vec2.Dot(dv, normal);

                    var impulse = -cp.NormalMass * (vn + constraint.Restitution * cp.RelativeVelocity);
                    var newImpulse = Math.Max(cp.NormalImpulse + impulse, 0f);
                    impulse = newImpulse - cp.NormalImpulse;
                    cp.NormalImpulse = newImpulse;
                    if (newImpulse > cp.MaxNormalImpulse) cp.MaxNormalImpulse = newImpulse;

                    var p = impulse * normal;
                    bodyA.V -= bodyA.InvMass * p;
                    bodyA.W -= bodyA.InvInertia * Vec2.Cross(rA, p);
                    bodyB.V += bodyB.InvMass * p;
                    bodyB.W += bodyB.InvInertia * Vec2.Cross(rB, p);
                }
            }
        }

        private void StoreImpulses(Contact[] contacts, Separation[] separations)
        {
            for (var i = 0; i < _constraintCount; ++i)
            {
                ref var constraint = ref _constraints[i];
                if (constraint.ContactIndex < 0)
                {
                    separations[-constraint.ContactIndex - 1].NormalImpulse = constraint.Point0.NormalImpulse;
                    continue;
                }

                ref var manifold = ref contacts[constraint.ContactIndex].Manifold;
                for (var j = 0; j < constraint.PointCount; ++j)
                {
                    ref var cp = ref ConstraintPoint.Get(ref constraint, j);
                    ref var mp = ref Manifold.Point(ref manifold, j);
                    mp.NormalImpulse = cp.NormalImpulse;
                    mp.TangentImpulse = cp.TangentImpulse;
                    mp.MaxNormalImpulse = cp.MaxNormalImpulse;
                }
            }
        }

        private struct Softness
        {
            public float BiasRate;
            public float MassScale;
            public float ImpulseScale;

            // Adapted from Box2D v3 (MIT, Erin Catto), b2MakeSoft.
            public static Softness Make(float hertz, float dampingRatio, float h)
            {
                if (hertz == 0f) return new Softness { BiasRate = 0f, MassScale = 1f, ImpulseScale = 0f };

                var omega = 2f * Utilites.MathEx.PI * hertz;
                var a1 = 2f * dampingRatio + h * omega;
                var a2 = h * omega * a1;
                var a3 = 1f / (1f + a2);
                return new Softness { BiasRate = omega / a1, MassScale = a2 * a3, ImpulseScale = a3 };
            }
        }

        private struct ConstraintPoint
        {
            public Vec2 AnchorA;
            public Vec2 AnchorB;
            public float BaseSeparation;
            public float RelativeVelocity;
            public float NormalImpulse;
            public float TangentImpulse;
            public float MaxNormalImpulse;
            public float NormalMass;
            public float TangentMass;

            public static ref ConstraintPoint Get(ref Constraint constraint, int index)
            {
                if (index == 0) return ref constraint.Point0;
                return ref constraint.Point1;
            }
        }

        private struct Constraint
        {
            public int ContactIndex;
            public int BodyA;
            public int BodyB;
            public Vec2 Normal;
            public float Friction;
            public float Restitution;
            public Softness Softness;
            public int PointCount;
            public ConstraintPoint Point0;
            public ConstraintPoint Point1;
        }

        private Constraint[] _constraints = new Constraint[64];
        private int _constraintCount;
    }
}
