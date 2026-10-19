using System;

namespace CombatSimulation.Physics
{
    // Joint stages of the soft-step solver. Adapted from Box2D v3 (MIT, Erin Catto):
    // - hinge: revolute_joint.c, b2WarmStartRevoluteJoint and b2SolveRevoluteJoint, without spring and motor (P4 owner
    //   decision 2);
    // - rope: distance_joint.c, b2WarmStartDistanceJoint and b2SolveDistanceJoint with only the upper length limit (no
    //   spring, no lower limit, never rigid). Speculative as the contacts: while slack, it lets the ends reach the rope
    //   length within the substep and no more.
    //
    // PhysicsWorld.PrepareJoints fills the anchors, the centre offset and the joint angle at the start of the step.
    // Within the step the solver measures the joint from those values plus the motion of both bodies in the step
    // (DeltaPosition, DeltaRotation), so a joint across the torus seam needs no wrap.
    internal static class JointSolver
    {
        public static void WarmStart(Body[] bodies, Joint[] joints, int jointCount)
        {
            for (var i = 0; i < jointCount; ++i)
            {
                ref var joint = ref joints[i];
                if (!joint.Alive) continue;

                ref var bodyA = ref bodies[joint.BodyA];
                ref var bodyB = ref bodies[joint.BodyB];
                var rA = bodyA.DeltaRotation.Rotate(joint.AnchorA);
                var rB = bodyB.DeltaRotation.Rotate(joint.AnchorB);

                if (joint.Kind == JointKind.Rope)
                {
                    var axis = Separation(ref joint, ref bodyA, ref bodyB, rA, rB).Normalized;
                    var p = -joint.UpperImpulse * axis;
                    joint.TotalImpulse += p;

                    bodyA.V -= bodyA.InvMass * p;
                    bodyA.W -= bodyA.InvInertia * Vec2.Cross(rA, p);
                    bodyB.V += bodyB.InvMass * p;
                    bodyB.W += bodyB.InvInertia * Vec2.Cross(rB, p);
                    continue;
                }

                var axialImpulse = joint.LowerImpulse - joint.UpperImpulse;
                joint.TotalImpulse += joint.LinearImpulse;

                bodyA.V -= bodyA.InvMass * joint.LinearImpulse;
                bodyA.W -= bodyA.InvInertia * (Vec2.Cross(rA, joint.LinearImpulse) + axialImpulse);
                bodyB.V += bodyB.InvMass * joint.LinearImpulse;
                bodyB.W += bodyB.InvInertia * (Vec2.Cross(rB, joint.LinearImpulse) + axialImpulse);
            }
        }

        // Anchor B minus anchor A now: the offset at the start of the step plus the motion of both bodies in the step.
        private static Vec2 Separation(ref Joint joint, ref Body bodyA, ref Body bodyB, Vec2 rA, Vec2 rB) =>
            bodyB.DeltaPosition - bodyA.DeltaPosition + (rB - rA) + joint.DeltaCenter;

        public static void Solve(Body[] bodies, Joint[] joints, int jointCount, ContactSolver.Softness softness, float invH, bool useBias)
        {
            for (var i = 0; i < jointCount; ++i)
            {
                ref var joint = ref joints[i];
                if (!joint.Alive) continue;

                ref var bodyA = ref bodies[joint.BodyA];
                ref var bodyB = ref bodies[joint.BodyB];
                if (joint.Kind == JointKind.Rope) SolveRope(ref joint, ref bodyA, ref bodyB, softness, invH, useBias);
                else SolveHinge(ref joint, ref bodyA, ref bodyB, softness, invH, useBias);
            }
        }

        private static void SolveRope(ref Joint joint, ref Body bodyA, ref Body bodyB, ContactSolver.Softness softness, float invH, bool useBias)
        {
            var rA = bodyA.DeltaRotation.Rotate(joint.AnchorA);
            var rB = bodyB.DeltaRotation.Rotate(joint.AnchorB);
            var d = Separation(ref joint, ref bodyA, ref bodyB, rA, rB);
            var length = d.Length;
            var axis = d.Normalized;

            // c stays positive while the rope is slack, and the impulse stays positive while the rope pulls.
            var c = joint.Length - length;
            var bias = 0f;
            var massScale = 1f;
            var impulseScale = 0f;
            if (c > 0f)
            {
                bias = c * invH;
            }
            else if (useBias)
            {
                bias = softness.BiasRate * c;
                massScale = softness.MassScale;
                impulseScale = softness.ImpulseScale;
            }

            var vr = bodyB.V + Vec2.Cross(bodyB.W, rB) - bodyA.V - Vec2.Cross(bodyA.W, rA);
            // c falls as the anchors separate.
            var cdot = -Vec2.Dot(axis, vr);
            var impulse = -massScale * joint.AxialMass * (cdot + bias) - impulseScale * joint.UpperImpulse;
            var newImpulse = Math.Max(0f, joint.UpperImpulse + impulse);
            impulse = newImpulse - joint.UpperImpulse;
            joint.UpperImpulse = newImpulse;

            var p = -impulse * axis;
            joint.TotalImpulse += p;
            bodyA.V -= bodyA.InvMass * p;
            bodyA.W -= bodyA.InvInertia * Vec2.Cross(rA, p);
            bodyB.V += bodyB.InvMass * p;
            bodyB.W += bodyB.InvInertia * Vec2.Cross(rB, p);
        }

        private static void SolveHinge(ref Joint joint, ref Body bodyA, ref Body bodyB, ContactSolver.Softness softness, float invH, bool useBias)
        {
            var mA = bodyA.InvMass;
            var iA = bodyA.InvInertia;
            var mB = bodyB.InvMass;
            var iB = bodyB.InvInertia;
            var vA = bodyA.V;
            var wA = bodyA.W;
            var vB = bodyB.V;
            var wB = bodyB.W;

            if (joint.EnableLimit && iA + iB > 0f)
            {
                var jointAngle = PhysicsWorld.UnwindAngle(joint.DeltaAngle + PhysicsWorld.RelativeAngle(bodyB.DeltaRotation, bodyA.DeltaRotation));

                // Lower limit.
                {
                    var c = jointAngle - joint.LowerAngle;
                    var bias = 0f;
                    var massScale = 1f;
                    var impulseScale = 0f;
                    if (c > 0f)
                    {
                        // Speculative: allow the angle to reach the limit in this substep, but no more.
                        bias = c * invH;
                    }
                    else if (useBias)
                    {
                        bias = softness.BiasRate * c;
                        massScale = softness.MassScale;
                        impulseScale = softness.ImpulseScale;
                    }

                    var cdot = wB - wA;
                    var impulse = -joint.AxialMass * massScale * (cdot + bias) - impulseScale * joint.LowerImpulse;
                    var newImpulse = Math.Max(joint.LowerImpulse + impulse, 0f);
                    impulse = newImpulse - joint.LowerImpulse;
                    joint.LowerImpulse = newImpulse;

                    wA -= iA * impulse;
                    wB += iB * impulse;
                }

                // Upper limit. The signs are flipped, so c stays positive while the limit holds and the impulse stays
                // positive while the limit acts.
                {
                    var c = joint.UpperAngle - jointAngle;
                    var bias = 0f;
                    var massScale = 1f;
                    var impulseScale = 0f;
                    if (c > 0f)
                    {
                        bias = c * invH;
                    }
                    else if (useBias)
                    {
                        bias = softness.BiasRate * c;
                        massScale = softness.MassScale;
                        impulseScale = softness.ImpulseScale;
                    }

                    var cdot = wA - wB;
                    var impulse = -joint.AxialMass * massScale * (cdot + bias) - impulseScale * joint.UpperImpulse;
                    var newImpulse = Math.Max(joint.UpperImpulse + impulse, 0f);
                    impulse = newImpulse - joint.UpperImpulse;
                    joint.UpperImpulse = newImpulse;

                    wA += iA * impulse;
                    wB -= iB * impulse;
                }
            }

            // Point constraint.
            {
                var rA = bodyA.DeltaRotation.Rotate(joint.AnchorA);
                var rB = bodyB.DeltaRotation.Rotate(joint.AnchorB);
                var cdot = vB + Vec2.Cross(wB, rB) - vA - Vec2.Cross(wA, rA);

                var bias = Vec2.Zero;
                var massScale = 1f;
                var impulseScale = 0f;
                if (useBias)
                {
                    var separation = Separation(ref joint, ref bodyA, ref bodyB, rA, rB);
                    bias = softness.BiasRate * separation;
                    massScale = softness.MassScale;
                    impulseScale = softness.ImpulseScale;
                }

                var k11 = mA + mB + rA.Y * rA.Y * iA + rB.Y * rB.Y * iB;
                var k12 = -rA.Y * rA.X * iA - rB.Y * rB.X * iB;
                var k22 = mA + mB + rA.X * rA.X * iA + rB.X * rB.X * iB;
                var b = Solve22(k11, k12, k22, cdot + bias);

                var impulse = new Vec2(-massScale * b.X - impulseScale * joint.LinearImpulse.X,
                    -massScale * b.Y - impulseScale * joint.LinearImpulse.Y);
                joint.LinearImpulse += impulse;
                joint.TotalImpulse += impulse;

                vA -= mA * impulse;
                wA -= iA * Vec2.Cross(rA, impulse);
                vB += mB * impulse;
                wB += iB * Vec2.Cross(rB, impulse);
            }

            bodyA.V = vA;
            bodyA.W = wA;
            bodyB.V = vB;
            bodyB.W = wB;
        }

        // Solves the symmetric 2×2 system K x = b. Zero when K is singular (two bodies that can neither move nor turn).
        private static Vec2 Solve22(float k11, float k12, float k22, Vec2 b)
        {
            var det = k11 * k22 - k12 * k12;
            if (det != 0f) det = 1f / det;
            return new Vec2(det * (k22 * b.X - k12 * b.Y), det * (k11 * b.Y - k12 * b.X));
        }
    }
}
