using System;

namespace CombatSimulation.Physics
{
    // Free motion of a body over one step: its poses at the solver substep boundaries. Between two boundaries the
    // centre moves on a line and the rotation is interpolated (normalized lerp). One substep turns at most a few
    // tenths of a radian, so the path stays close to the real arc even for a fast spin.
    internal sealed class Sweep
    {
        public Sweep(int substeps)
        {
            Substeps = substeps;
            _centers = new Vec2[substeps + 1];
            _rotations = new Rot[substeps + 1];
        }

        public int Substeps { get; }
        public Vec2 LocalCenter { get; private set; }

        // No rotation and one velocity for the whole step: the centre moves on one line.
        public bool Straight { get; private set; }

        // Largest linear and angular speed within the step.
        public float MaxSpeed { get; private set; }
        public float MaxAngularSpeed { get; private set; }

        public Vec2 Center => _centers[0];

        // The motion the solver gives a body without contacts: velocity, then force and damping in each substep,
        // in the order of ContactSolver.IntegrateVelocities and IntegratePositions. The centre is given in the
        // torus image that the caller uses.
        public void SetFree(in Body body, Vec2 center, float dt)
        {
            var h = dt / Substeps;
            var v = body.V;
            var w = body.W;
            var position = Vec2.Zero;
            var rotation = Rot.Identity;

            LocalCenter = body.LocalCenter;
            _centers[0] = center;
            _rotations[0] = body.Q;
            Straight = true;
            MaxSpeed = 0f;
            MaxAngularSpeed = 0f;

            for (var i = 1; i <= Substeps; ++i)
            {
                if (body.InvMass > 0f)
                    v = (v + h * body.InvMass * body.Force) * (1f / (1f + h * body.LinearDamping));

                if (body.InvInertia > 0f)
                    w = (w + h * body.InvInertia * body.Torque) * (1f / (1f + h * body.AngularDamping));

                if (w != 0f) rotation = Rot.Integrate(rotation, h * w);
                if (body.InvMass > 0f) position += h * v;

                _centers[i] = center + position;
                _rotations[i] = Rot.Mul(rotation, body.Q);

                Straight &= w == 0f && (body.InvMass == 0f || v.Equals(body.V));
                MaxSpeed = Math.Max(MaxSpeed, body.InvMass > 0f ? v.Length : 0f);
                MaxAngularSpeed = Math.Max(MaxAngularSpeed, Math.Abs(w));
            }
        }

        // Constant linear and angular velocity over dt, from the origin pose. For tests.
        public void SetLinear(Vec2 localCenter, Vec2 origin, Rot q, Vec2 velocity, float angularVelocity, float dt)
        {
            var h = dt / Substeps;
            var rotation = Rot.Identity;
            var center = origin + q.Rotate(localCenter);

            LocalCenter = localCenter;
            _centers[0] = center;
            _rotations[0] = q;
            Straight = angularVelocity == 0f;
            MaxSpeed = velocity.Length;
            MaxAngularSpeed = Math.Abs(angularVelocity);
            for (var i = 1; i <= Substeps; ++i)
            {
                if (angularVelocity != 0f) rotation = Rot.Integrate(rotation, h * angularVelocity);
                _centers[i] = center + (i * h) * velocity;
                _rotations[i] = Rot.Mul(rotation, q);
            }
        }

        // Body pose at the fraction t of the step, 0 ≤ t ≤ 1.
        public Pose GetPose(float t)
        {
            var s = t * Substeps;
            var i = (int)s;
            if (i >= Substeps) i = Substeps - 1;
            if (i < 0) i = 0;
            var f = s - i;

            var center = Vec2.Lerp(_centers[i], _centers[i + 1], f);
            var q0 = _rotations[i];
            var q1 = _rotations[i + 1];
            var q = Rot.Normalize(q0.C + f * (q1.C - q0.C), q0.S + f * (q1.S - q0.S));
            return new Pose(center - q.Rotate(LocalCenter), q);
        }

        private readonly Vec2[] _centers;
        private readonly Rot[] _rotations;
    }

    internal enum TimeOfImpactState
    {
        // The root finder ran out of iterations. Treat as a hit.
        Failed,

        // The shapes overlap at the start.
        Overlapped,

        // The shapes come within the target separation; Fraction is the time.
        Hit,

        // The shapes stay farther apart than the target separation for the whole step.
        Separated,
    }

    // First time two moving convex shapes come within a target separation, between the fractions tMin and tMax of the
    // step (conservative advancement on separating axes). Adapted from Box2D (MIT, Erin Catto): b2TimeOfImpact,
    // b2SeparationFunction.
    //
    // A separation along a fixed axis is concave in time while both shapes only translate on a line, so its values at
    // the two ends of an interval bound it in between. Turning shapes and bends of the path break that: the caller
    // splits the step at the substeps and adds the largest dip of the path to the target separation.
    internal static class TimeOfImpact
    {
        public static TimeOfImpactState Compute(in DistanceProxy proxyA, Sweep sweepA, in DistanceProxy proxyB, Sweep sweepB,
            float targetSeparation, float tolerance, float tMin, float tMax, out float fraction)
        {
            // Distances below are between the point sets; the target adds both radii.
            var target = proxyA.Radius + proxyB.Radius + targetSeparation;
            var t1 = tMin;
            var cache = default(SimplexCache);

            for (var iteration = 0;; ++iteration)
            {
                var xfA = sweepA.GetPose(t1);
                var xfB = sweepB.GetPose(t1);
                var distance = Distance.Cores(proxyA, xfA, proxyB, xfB, ref cache, out _, out _);

                if (distance <= 0f || cache.Count == 3)
                {
                    fraction = t1;
                    return TimeOfImpactState.Overlapped;
                }

                if (distance < target + tolerance)
                {
                    fraction = t1;
                    return TimeOfImpactState.Hit;
                }

                var function = new SeparationFunction(cache, proxyA, sweepA, proxyB, sweepB, t1);

                // Resolve the deepest point on the separating axis, one witness pair at a time. Bounded by the vertex count.
                var t2 = tMax;
                for (var pushBack = 0; pushBack < Polygon.MaxVertices; ++pushBack)
                {
                    var s2 = function.FindMinSeparation(t2, out var indexA, out var indexB);

                    // Separated at the end of the interval along this axis.
                    if (s2 > target + tolerance)
                    {
                        fraction = tMax;
                        return TimeOfImpactState.Separated;
                    }

                    // Reached the target: advance the sweeps.
                    if (s2 > target - tolerance)
                    {
                        t1 = t2;
                        break;
                    }

                    var s1 = function.Evaluate(indexA, indexB, t1);

                    // The root finder may have run out of iterations.
                    if (s1 < target - tolerance)
                    {
                        fraction = t1;
                        return TimeOfImpactState.Failed;
                    }

                    if (s1 <= target + tolerance)
                    {
                        fraction = t1;
                        return TimeOfImpactState.Hit;
                    }

                    // Root of s(t) = target: bisection and secant steps in turn.
                    var a1 = t1;
                    var a2 = t2;
                    for (var root = 0; root < MaxRootIterations; ++root)
                    {
                        var t = (root & 1) != 0 ? a1 + (target - s1) * (a2 - a1) / (s2 - s1) : 0.5f * (a1 + a2);
                        var s = function.Evaluate(indexA, indexB, t);

                        if (Math.Abs(s - target) < tolerance)
                        {
                            t2 = t;
                            break;
                        }

                        if (s > target)
                        {
                            a1 = t;
                            s1 = s;
                        }
                        else
                        {
                            a2 = t;
                            s2 = s;
                        }
                    }
                }

                if (iteration + 1 == MaxIterations)
                {
                    fraction = t1;
                    return TimeOfImpactState.Failed;
                }
            }
        }

        private enum SeparationKind
        {
            Points,
            FaceA,
            FaceB,
        }

        // Separation along an axis taken from the closest features at the start time: the line between two points
        // (fixed in the world), or the normal of an edge of A or B (turns with that body).
        private readonly struct SeparationFunction
        {
            public SeparationFunction(in SimplexCache cache, in DistanceProxy proxyA, Sweep sweepA, in DistanceProxy proxyB, Sweep sweepB, float t1)
            {
                _proxyA = proxyA;
                _proxyB = proxyB;
                _sweepA = sweepA;
                _sweepB = sweepB;
                _localPoint = Vec2.Zero;

                var xfA = sweepA.GetPose(t1);
                var xfB = sweepB.GetPose(t1);

                if (cache.Count == 1)
                {
                    _kind = SeparationKind.Points;
                    var pointA = xfA.TransformPoint(proxyA.Vertex(cache.IndexA0));
                    var pointB = xfB.TransformPoint(proxyB.Vertex(cache.IndexB0));
                    _axis = (pointB - pointA).Normalized;
                    return;
                }

                if (cache.IndexA0 == cache.IndexA1)
                {
                    // Two points on B and one on A.
                    _kind = SeparationKind.FaceB;
                    var b1 = proxyB.Vertex(cache.IndexB0);
                    var b2 = proxyB.Vertex(cache.IndexB1);
                    var axis = Vec2.RightPerp(b2 - b1).Normalized;
                    var normal = xfB.Q.Rotate(axis);
                    _localPoint = Vec2.Lerp(b1, b2, 0.5f);
                    var pointB = xfB.TransformPoint(_localPoint);
                    var pointA = xfA.TransformPoint(proxyA.Vertex(cache.IndexA0));
                    _axis = Vec2.Dot(pointA - pointB, normal) < 0f ? -axis : axis;
                }
                else
                {
                    // Two points on A and one or two on B.
                    _kind = SeparationKind.FaceA;
                    var a1 = proxyA.Vertex(cache.IndexA0);
                    var a2 = proxyA.Vertex(cache.IndexA1);
                    var axis = Vec2.RightPerp(a2 - a1).Normalized;
                    var normal = xfA.Q.Rotate(axis);
                    _localPoint = Vec2.Lerp(a1, a2, 0.5f);
                    var pointA = xfA.TransformPoint(_localPoint);
                    var pointB = xfB.TransformPoint(proxyB.Vertex(cache.IndexB0));
                    _axis = Vec2.Dot(pointB - pointA, normal) < 0f ? -axis : axis;
                }
            }

            // The deepest pair along the axis at time t, and its separation.
            public float FindMinSeparation(float t, out int indexA, out int indexB)
            {
                var xfA = _sweepA.GetPose(t);
                var xfB = _sweepB.GetPose(t);

                switch (_kind)
                {
                    case SeparationKind.Points:
                    {
                        indexA = _proxyA.Support(xfA.Q.InvRotate(_axis));
                        indexB = _proxyB.Support(xfB.Q.InvRotate(-_axis));
                        var pointA = xfA.TransformPoint(_proxyA.Vertex(indexA));
                        var pointB = xfB.TransformPoint(_proxyB.Vertex(indexB));
                        return Vec2.Dot(pointB - pointA, _axis);
                    }

                    case SeparationKind.FaceA:
                    {
                        var normal = xfA.Q.Rotate(_axis);
                        var pointA = xfA.TransformPoint(_localPoint);
                        indexA = -1;
                        indexB = _proxyB.Support(xfB.Q.InvRotate(-normal));
                        var pointB = xfB.TransformPoint(_proxyB.Vertex(indexB));
                        return Vec2.Dot(pointB - pointA, normal);
                    }

                    default:
                    {
                        var normal = xfB.Q.Rotate(_axis);
                        var pointB = xfB.TransformPoint(_localPoint);
                        indexB = -1;
                        indexA = _proxyA.Support(xfA.Q.InvRotate(-normal));
                        var pointA = xfA.TransformPoint(_proxyA.Vertex(indexA));
                        return Vec2.Dot(pointA - pointB, normal);
                    }
                }
            }

            // Separation of a given pair along the axis at time t.
            public float Evaluate(int indexA, int indexB, float t)
            {
                var xfA = _sweepA.GetPose(t);
                var xfB = _sweepB.GetPose(t);

                switch (_kind)
                {
                    case SeparationKind.Points:
                    {
                        var pointA = xfA.TransformPoint(_proxyA.Vertex(indexA));
                        var pointB = xfB.TransformPoint(_proxyB.Vertex(indexB));
                        return Vec2.Dot(pointB - pointA, _axis);
                    }

                    case SeparationKind.FaceA:
                    {
                        var normal = xfA.Q.Rotate(_axis);
                        var pointA = xfA.TransformPoint(_localPoint);
                        var pointB = xfB.TransformPoint(_proxyB.Vertex(indexB));
                        return Vec2.Dot(pointB - pointA, normal);
                    }

                    default:
                    {
                        var normal = xfB.Q.Rotate(_axis);
                        var pointB = xfB.TransformPoint(_localPoint);
                        var pointA = xfA.TransformPoint(_proxyA.Vertex(indexA));
                        return Vec2.Dot(pointA - pointB, normal);
                    }
                }
            }

            private readonly DistanceProxy _proxyA;
            private readonly DistanceProxy _proxyB;
            private readonly Sweep _sweepA;
            private readonly Sweep _sweepB;
            private readonly SeparationKind _kind;

            // World axis (Points), or the face normal in the local frame of A or B.
            private readonly Vec2 _axis;
            private readonly Vec2 _localPoint;
        }

        private const int MaxIterations = 20;
        private const int MaxRootIterations = 50;
    }
}
