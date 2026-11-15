using System;

namespace CombatSimulation.Physics
{
    internal enum ShapeCastState
    {
        // The shapes stay apart for the whole translation.
        Miss,

        // The shapes overlap or touch at the start. Fraction is 0; the caller finds the normal (Shape.Collide).
        Overlapped,

        // First contact at Fraction.
        Hit,
    }

    // Linear shape cast (P3 owner decision 2): shape B moves by a translation without rotation, shape A stays still.
    // Gives the first time the surfaces touch. Rays are a point proxy with radius 0.
    //
    // Newton steps on the distance d(t) between the cores. For a pure translation d(t) is convex, so its tangent at t
    // reaches the target no later than d(t) does: each step lands at or before the first contact and never jumps
    // through a shape. A flat face converges in one step, a round surface quadratically. The derivative is
    // dot(translation, n), with n the unit vector between the closest points; when it is not negative, the shapes
    // never come closer, which is a miss.
    //
    // The caller gives poses near the origin (relative to one of the shapes), so float round-off does not grow with
    // the map coordinates.
    internal static class ShapeCast
    {
        public static ShapeCastState Cast(in DistanceProxy proxyA, in Pose xfA, in DistanceProxy proxyB, in Pose xfB, Vec2 translationB,
            float tolerance, out float fraction, out Vec2 point, out Vec2 normal)
        {
            var target = proxyA.Radius + proxyB.Radius;
            var cache = default(SimplexCache);
            var t = 0f;

            for (var iteration = 0; iteration < MaxIterations; ++iteration)
            {
                var xfBt = new Pose(xfB.P + t * translationB, xfB.Q);
                var distance = Distance.Cores(proxyA, xfA, proxyB, xfBt, ref cache, out var pointA, out var pointB);

                if (distance <= target + tolerance || cache.Count == 3)
                {
                    fraction = t;
                    return t > 0f
                        ? Finish(proxyA, xfA, proxyB, xfB, translationB, t, pointA, pointB, distance, out point, out normal)
                        : Overlapped(out point, out normal);
                }

                var n = (pointB - pointA) * (1f / distance);
                var closing = -Vec2.Dot(translationB, n);
                if (closing <= 0f) break;

                // A step this small means a gap below TimeTolerance × |translation|: float noise in the distance can keep
                // it above 'tolerance', and it must not end as a miss.
                var step = (distance - target) / closing;
                if (step < TimeTolerance)
                {
                    fraction = t;
                    return t > 0f
                        ? Finish(proxyA, xfA, proxyB, xfB, translationB, t, pointA, pointB, distance, out point, out normal)
                        : Overlapped(out point, out normal);
                }

                t += step;
                if (t > 1f) break;
            }

            fraction = 1f;
            point = default;
            normal = default;
            return ShapeCastState.Miss;
        }

        private static ShapeCastState Overlapped(out Vec2 point, out Vec2 normal)
        {
            point = default;
            normal = default;
            return ShapeCastState.Overlapped;
        }

        // Contact at fraction t. The point lies on the surface of A, the normal points from A to B.
        private static ShapeCastState Finish(in DistanceProxy proxyA, in Pose xfA, in DistanceProxy proxyB, in Pose xfB, Vec2 translationB,
            float t, Vec2 pointA, Vec2 pointB, float distance, out Vec2 point, out Vec2 normal)
        {
            normal = ContactNormal(proxyA, xfA, proxyB, xfB, translationB, t, pointA, pointB, distance);
            point = pointA + proxyA.Radius * normal;
            return ShapeCastState.Hit;
        }

        // The direction between the closest core points while the cores are apart (always for a circle or a capsule,
        // whose cores stay a radius apart). When a vertex lands exactly on a face (a flat face converges exactly), the
        // cores touch and that direction is lost in float noise. Then the face normal with the largest separation is
        // the contact normal (separating axis test): the touched face separates by about zero, every other face less. At
        // a corner on a corner several faces tie, and each of them separates the shapes. Cut edges are inside a hull
        // and never give the normal (Collide, "Outline parts"). Polygon manifolds are no help here: they prefer a face
        // of A within a tolerance, also when a vertex of A touches a face of B.
        private static Vec2 ContactNormal(in DistanceProxy proxyA, in Pose xfA, in DistanceProxy proxyB, in Pose xfB, Vec2 translationB,
            float t, Vec2 pointA, Vec2 pointB, float distance)
        {
            if (distance >= NormalGap) return (pointB - pointA) * (1f / distance);

            var xfBt = new Pose(xfB.P + t * translationB, xfB.Q);
            var best = float.MinValue;
            var normal = distance >= Epsilon ? (pointB - pointA) * (1f / distance) : -translationB.Normalized;

            var polygonA = proxyA.Polygon;
            if (polygonA != null && polygonA.Count >= 3)
            {
                for (var i = 0; i < polygonA.Count; ++i)
                {
                    if (polygonA.IsCut(i)) continue;
                    var n = xfA.Q.Rotate(polygonA.Normal(i));
                    var separation = Separation(proxyA, xfA, proxyB, xfBt, n);
                    if (separation <= best) continue;
                    best = separation;
                    normal = n;
                }
            }

            var polygonB = proxyB.Polygon;
            if (polygonB != null && polygonB.Count >= 3)
            {
                for (var i = 0; i < polygonB.Count; ++i)
                {
                    if (polygonB.IsCut(i)) continue;
                    var n = -xfBt.Q.Rotate(polygonB.Normal(i));
                    var separation = Separation(proxyA, xfA, proxyB, xfBt, n);
                    if (separation <= best) continue;
                    best = separation;
                    normal = n;
                }
            }

            return normal;
        }

        // Gap between the cores along n (from A to B): the lowest point of B minus the highest point of A.
        private static float Separation(in DistanceProxy proxyA, in Pose xfA, in DistanceProxy proxyB, in Pose xfB, Vec2 n)
        {
            var maxA = float.MinValue;
            for (var i = 0; i < proxyA.Count; ++i) maxA = Math.Max(maxA, Vec2.Dot(xfA.TransformPoint(proxyA.Vertex(i)), n));
            var minB = float.MaxValue;
            for (var i = 0; i < proxyB.Count; ++i) minB = Math.Min(minB, Vec2.Dot(xfB.TransformPoint(proxyB.Vertex(i)), n));
            return minB - maxA;
        }

        // A flat face needs one step and a round surface a few. Without a contact the steps overshoot the closest
        // approach and end with a negative derivative. Only a path that just grazes the target converges slowly
        // (linearly); 32 steps then still land within the tolerance.
        private const int MaxIterations = 32;

        // Fraction of the translation below which a Newton step counts as converged.
        private const float TimeTolerance = 1e-6f;
        private const float Epsilon = 1e-6f;

        // Core distance that gives a normal direction to better than 0.3° in the cast frame (coordinates up to about 60).
        private const float NormalGap = 1e-3f;
    }
}
