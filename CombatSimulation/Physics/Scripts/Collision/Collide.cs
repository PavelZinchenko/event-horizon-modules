using System;

namespace CombatSimulation.Physics
{
    // Contact manifolds for pairs of convex shapes.
    //
    // Both poses are in one unwrapped frame: the caller shifts B to its nearest torus image of A.
    // Each function keeps the points with separation <= maxDistance. Points with a positive
    // separation are speculative: the solver lets the shapes close that gap in the step, but no more.
    //
    // The polygon manifold, its clipping and the segment distance are adapted from Box2D v3 (MIT, Erin Catto).
    public static class Collide
    {
        // Used when two centres coincide and no direction is defined.
        public static readonly Vec2 FallbackNormal = new Vec2(1f, 0f);

        public static void Circles(in Circle a, in Pose xfA, in Circle b, in Pose xfB, float maxDistance, out Manifold manifold)
        {
            manifold = default;

            var pA = xfA.TransformPoint(a.Center);
            var pB = xfB.TransformPoint(b.Center);
            var d = pB - pA;
            var radius = a.Radius + b.Radius;
            var limit = radius + maxDistance;
            var distanceSquared = d.LengthSquared;
            if (distanceSquared > limit * limit) return;

            var distance = (float)Math.Sqrt(distanceSquared);
            var normal = distance > Epsilon ? d * (1f / distance) : FallbackNormal;
            var cA = pA + a.Radius * normal;
            var cB = pB - b.Radius * normal;

            manifold.Normal = normal;
            manifold.PointCount = 1;
            manifold.Point0.Point = Vec2.Lerp(cA, cB, 0.5f);
            manifold.Point0.Separation = distance - radius;
            manifold.Point0.Id = 0;
        }

        // A is the polygon, B is the circle.
        public static void PolygonAndCircle(Polygon a, in Pose xfA, in Circle b, in Pose xfB, float maxDistance, out Manifold manifold)
        {
            manifold = default;

            var center = xfA.InvTransformPoint(xfB.TransformPoint(b.Center));
            var radius = b.Radius;

            var normalIndex = 0;
            var separation = float.MinValue;
            for (var i = 0; i < a.Count; ++i)
            {
                var s = Vec2.Dot(a.Normal(i), center - a.Vertex(i));
                if (s > separation)
                {
                    separation = s;
                    normalIndex = i;
                }
            }

            if (separation > radius + maxDistance) return;

            var v1 = a.Vertex(normalIndex);
            var v2 = a.Vertex(normalIndex + 1 < a.Count ? normalIndex + 1 : 0);
            Vec2 localNormal;
            Vec2 cA;
            float pointSeparation;

            if (separation > 0f && Vec2.Dot(center - v1, v2 - v1) < 0f)
            {
                if (!VertexRegion(v1, center, radius, maxDistance, a.Normal(normalIndex), out localNormal, out pointSeparation)) return;
                cA = v1;
            }
            else if (separation > 0f && Vec2.Dot(center - v2, v1 - v2) < 0f)
            {
                if (!VertexRegion(v2, center, radius, maxDistance, a.Normal(normalIndex), out localNormal, out pointSeparation)) return;
                cA = v2;
            }
            else
            {
                localNormal = a.Normal(normalIndex);
                cA = center - separation * localNormal;
                pointSeparation = separation - radius;
            }

            var cB = center - radius * localNormal;

            manifold.Normal = xfA.Q.Rotate(localNormal);
            manifold.PointCount = 1;
            manifold.Point0.Point = xfA.TransformPoint(Vec2.Lerp(cA, cB, 0.5f));
            manifold.Point0.Separation = pointSeparation;
            manifold.Point0.Id = 0;
        }

        // A is the capsule, B is the circle.
        public static void CapsuleAndCircle(in Capsule a, in Pose xfA, in Circle b, in Pose xfB, float maxDistance, out Manifold manifold)
        {
            manifold = default;

            var center = xfA.InvTransformPoint(xfB.TransformPoint(b.Center));

            // Closest point of the capsule segment to the circle centre.
            var p1 = a.Center1;
            var p2 = a.Center2;
            var e = p2 - p1;
            Vec2 closest;
            if (Vec2.Dot(center - p1, e) < 0f) closest = p1;
            else if (Vec2.Dot(p2 - center, e) < 0f) closest = p2;
            else closest = p1 + (Vec2.Dot(center - p1, e) / Vec2.Dot(e, e)) * e;

            var d = center - closest;
            var radius = a.Radius + b.Radius;
            var limit = radius + maxDistance;
            var distanceSquared = d.LengthSquared;
            if (distanceSquared > limit * limit) return;

            // A centre on the segment has no direction; push it out sideways.
            var distance = (float)Math.Sqrt(distanceSquared);
            var localNormal = distance > Epsilon ? d * (1f / distance) : Vec2.RightPerp(e).Normalized;
            var cA = closest + a.Radius * localNormal;
            var cB = center - b.Radius * localNormal;

            manifold.Normal = xfA.Q.Rotate(localNormal);
            manifold.PointCount = 1;
            manifold.Point0.Point = xfA.TransformPoint(Vec2.Lerp(cA, cB, 0.5f));
            manifold.Point0.Separation = distance - radius;
            manifold.Point0.Id = 0;
        }

        public static void Capsules(in Capsule a, in Pose xfA, in Capsule b, in Pose xfB, float maxDistance, out Manifold manifold) =>
            Polygons(a.Core, xfA, b.Core, xfB, maxDistance, out manifold);

        // A is the polygon, B is the capsule.
        public static void PolygonAndCapsule(Polygon a, in Pose xfA, in Capsule b, in Pose xfB, float maxDistance, out Manifold manifold) =>
            Polygons(a, xfA, b.Core, xfB, maxDistance, out manifold);

        // Convex polygons, also rounded ones (capsule cores). Adapted from Box2D v3 (MIT, Erin Catto),
        // b2CollidePolygons and b2ClipPolygons: both polygons go into the frame of A, shifted to the first vertex
        // of A for precision; the separating axis test runs on the vertex outlines; every separation subtracts
        // the two radii.
        //
        // When the outlines are apart, the reference face and the incident edge decide the points:
        // 1. The incident edge overlaps the face along the face: the incident edge is clipped against the side
        //    planes of the face, widened by maxDistance. A corner that approaches past the end of the face keeps
        //    its own point, measured against the face plane. This is conservative: it can stop early, never late.
        //    Box2D v3 does not widen; with a speculative distance that grows with the motion, both points of a
        //    corner hit near the end of a face would then sit at the face end, and the one-sided push spins the
        //    body (P2 run: a drone hitting an octagon vertex at 40 u/s spun at 134 rad/s and sank in 123%).
        // 2. The incident edge lies wholly beside the face (corner past corner): one point between the closest
        //    features of the two edges, with the true gap (Box2D v3).
        public static void Polygons(Polygon a, in Pose xfA, Polygon b, in Pose xfB, float maxDistance, out Manifold manifold)
        {
            manifold = default;

            var origin = a.Vertex(0);
            var frameA = new Pose(xfA.TransformPoint(origin), xfA.Q);
            var q = Rot.InvMul(xfA.Q, xfB.Q);
            var p = xfA.Q.InvRotate(xfB.P - frameA.P);

            var polyA = new LocalPolygon(stackalloc Vec2[Polygon.MaxVertices], stackalloc Vec2[Polygon.MaxVertices], a.Count, a.Radius);
            for (var i = 0; i < a.Count; ++i)
            {
                polyA.Vertices[i] = a.Vertex(i) - origin;
                polyA.Normals[i] = a.Normal(i);
            }

            var polyB = new LocalPolygon(stackalloc Vec2[Polygon.MaxVertices], stackalloc Vec2[Polygon.MaxVertices], b.Count, b.Radius);
            for (var i = 0; i < b.Count; ++i)
            {
                polyB.Vertices[i] = q.Rotate(b.Vertex(i)) + p;
                polyB.Normals[i] = q.Rotate(b.Normal(i));
            }

            var limit = maxDistance + a.Radius + b.Radius;

            var separationA = FindMaxSeparation(polyA, polyB, out var edgeA);
            if (separationA > limit) return;

            var separationB = FindMaxSeparation(polyB, polyA, out var edgeB);
            if (separationB > limit) return;

            var flip = separationB > separationA + RelativeTolerance;
            if (flip) edgeA = FindIncidentEdge(polyB.Normals[edgeB], polyA);
            else edgeB = FindIncidentEdge(polyA.Normals[edgeA], polyB);

            var apart = (flip ? separationB : separationA) > RelativeTolerance;
            var overlaps = flip
                ? IncidentOverlapsReference(polyB, edgeB, polyA, edgeA)
                : IncidentOverlapsReference(polyA, edgeA, polyB, edgeB);

            if (!apart || overlaps || !ClosestFeatures(polyA, polyB, edgeA, edgeB, maxDistance, ref manifold))
                ClipPolygons(polyA, polyB, edgeA, edgeB, flip, apart ? maxDistance : 0f, maxDistance, ref manifold);

            if (manifold.PointCount == 0)
            {
                manifold = default;
                return;
            }

            manifold.Normal = xfA.Q.Rotate(manifold.Normal);
            for (var i = 0; i < manifold.PointCount; ++i)
            {
                ref var point = ref Manifold.Point(ref manifold, i);
                point.Point = frameA.TransformPoint(point.Point);
            }
        }

        // Largest separation of poly2 along the face normals of poly1, and the face that gives it.
        private static float FindMaxSeparation(in LocalPolygon poly1, in LocalPolygon poly2, out int edgeIndex)
        {
            edgeIndex = 0;
            var maxSeparation = float.MinValue;
            for (var i = 0; i < poly1.Count; ++i)
            {
                var n = poly1.Normals[i];
                var v1 = poly1.Vertices[i];

                var minSeparation = float.MaxValue;
                for (var j = 0; j < poly2.Count; ++j)
                {
                    var s = Vec2.Dot(n, poly2.Vertices[j] - v1);
                    if (s < minSeparation) minSeparation = s;
                }

                if (minSeparation > maxSeparation)
                {
                    maxSeparation = minSeparation;
                    edgeIndex = i;
                }
            }

            return maxSeparation;
        }

        // The edge of 'poly' most anti-parallel to the reference normal.
        private static int FindIncidentEdge(Vec2 referenceNormal, in LocalPolygon poly)
        {
            var incident = 0;
            var minDot = float.MaxValue;
            for (var i = 0; i < poly.Count; ++i)
            {
                var dot = Vec2.Dot(referenceNormal, poly.Normals[i]);
                if (dot < minDot)
                {
                    minDot = dot;
                    incident = i;
                }
            }

            return incident;
        }

        // True when the incident edge, projected on the reference face, overlaps the face.
        private static bool IncidentOverlapsReference(in LocalPolygon poly1, int edge1, in LocalPolygon poly2, int edge2)
        {
            var v11 = poly1.Vertices[edge1];
            var tangent = Vec2.LeftPerp(poly1.Normals[edge1]);
            var upper1 = Vec2.Dot(poly1.Vertices[poly1.Next(edge1)] - v11, tangent);
            var t1 = Vec2.Dot(poly2.Vertices[edge2] - v11, tangent);
            var t2 = Vec2.Dot(poly2.Vertices[poly2.Next(edge2)] - v11, tangent);
            return Math.Max(t1, t2) >= 0f && Math.Min(t1, t2) <= upper1;
        }

        // Clipped points of the incident edge against the side planes of the reference face, widened by
        // sideMargin (Box2D v3, b2ClipPolygons, without the margin). Ids name the feature of A, then of B.
        private static void ClipPolygons(in LocalPolygon polyA, in LocalPolygon polyB, int edgeA, int edgeB, bool flip, float sideMargin, float maxDistance, ref Manifold manifold)
        {
            var poly1 = flip ? polyB : polyA;
            var poly2 = flip ? polyA : polyB;
            var i11 = flip ? edgeB : edgeA;
            var i12 = poly1.Next(i11);
            var i21 = flip ? edgeA : edgeB;
            var i22 = poly2.Next(i21);

            var normal = poly1.Normals[i11];
            var v11 = poly1.Vertices[i11];
            var v12 = poly1.Vertices[i12];
            var v21 = poly2.Vertices[i21];
            var v22 = poly2.Vertices[i22];

            // The tangent runs along the reference face from v11 to v12. The incident edge runs the other way.
            var tangent = Vec2.LeftPerp(normal);
            var lower1 = -sideMargin;
            var upper1 = Vec2.Dot(v12 - v11, tangent) + sideMargin;
            var upper2 = Vec2.Dot(v21 - v11, tangent);
            var lower2 = Vec2.Dot(v22 - v11, tangent);

            var vLower = lower2 < lower1 && upper2 - lower2 > FloatEpsilon ? Vec2.Lerp(v22, v21, (lower1 - lower2) / (upper2 - lower2)) : v22;
            var vUpper = upper2 > upper1 && upper2 - lower2 > FloatEpsilon ? Vec2.Lerp(v22, v21, (upper1 - lower2) / (upper2 - lower2)) : v21;

            var separationLower = Vec2.Dot(vLower - v11, normal);
            var separationUpper = Vec2.Dot(vUpper - v11, normal);

            // Points halfway between the two surfaces.
            vLower += 0.5f * (poly1.Radius - poly2.Radius - separationLower) * normal;
            vUpper += 0.5f * (poly1.Radius - poly2.Radius - separationUpper) * normal;

            var radius = poly1.Radius + poly2.Radius;
            manifold.Normal = flip ? -normal : normal;
            if (!flip)
            {
                AddPoint(ref manifold, vLower, separationLower - radius, Manifold.MakeId(i11, i22), maxDistance);
                AddPoint(ref manifold, vUpper, separationUpper - radius, Manifold.MakeId(i12, i21), maxDistance);
            }
            else
            {
                AddPoint(ref manifold, vUpper, separationUpper - radius, Manifold.MakeId(i21, i12), maxDistance);
                AddPoint(ref manifold, vLower, separationLower - radius, Manifold.MakeId(i22, i11), maxDistance);
            }
        }

        // One point between the closest features of edge A and edge B. False when the features touch and the
        // normal is not defined.
        private static bool ClosestFeatures(in LocalPolygon polyA, in LocalPolygon polyB, int edgeA, int edgeB, float maxDistance, ref Manifold manifold)
        {
            var nextA = polyA.Next(edgeA);
            var nextB = polyB.Next(edgeB);
            SegmentDistance(polyA.Vertices[edgeA], polyA.Vertices[nextA], polyB.Vertices[edgeB], polyB.Vertices[nextB],
                out var fractionA, out var fractionB, out var closestA, out var closestB);

            var distance = (closestB - closestA).Length;
            if (distance < Epsilon) return false;

            var separation = distance - polyA.Radius - polyB.Radius;
            if (separation > maxDistance) return true;

            var normal = (closestB - closestA) * (1f / distance);
            var surfaceA = closestA + polyA.Radius * normal;
            var surfaceB = closestB - polyB.Radius * normal;

            manifold.Normal = normal;
            var id = Manifold.MakeId(fractionA < 0.5f ? edgeA : nextA, fractionB < 0.5f ? edgeB : nextB, ClosestKind);
            AddPoint(ref manifold, Vec2.Lerp(surfaceA, surfaceB, 0.5f), separation, id, maxDistance);
            return true;
        }

        // Closest points of segments p1-q1 and p2-q2. Adapted from Box2D v3 (MIT, Erin Catto), b2SegmentDistance.
        private static void SegmentDistance(Vec2 p1, Vec2 q1, Vec2 p2, Vec2 q2, out float fraction1, out float fraction2, out Vec2 closest1, out Vec2 closest2)
        {
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            var dd1 = Vec2.Dot(d1, d1);
            var dd2 = Vec2.Dot(d2, d2);
            var rd1 = Vec2.Dot(r, d1);
            var rd2 = Vec2.Dot(r, d2);

            const float epsSqr = FloatEpsilon * FloatEpsilon;

            if (dd1 < epsSqr || dd2 < epsSqr)
            {
                if (dd1 >= epsSqr)
                {
                    fraction1 = Clamp01(-rd1 / dd1);
                    fraction2 = 0f;
                }
                else if (dd2 >= epsSqr)
                {
                    fraction1 = 0f;
                    fraction2 = Clamp01(rd2 / dd2);
                }
                else
                {
                    fraction1 = 0f;
                    fraction2 = 0f;
                }
            }
            else
            {
                var d12 = Vec2.Dot(d1, d2);
                var denominator = dd1 * dd2 - d12 * d12;

                // Parallel segments: start from p1.
                var f1 = denominator != 0f ? Clamp01((d12 * rd2 - rd1 * dd2) / denominator) : 0f;
                var f2 = (d12 * f1 + rd2) / dd2;

                // Clamping on segment 2 needs a new point on segment 1.
                if (f2 < 0f)
                {
                    f2 = 0f;
                    f1 = Clamp01(-rd1 / dd1);
                }
                else if (f2 > 1f)
                {
                    f2 = 1f;
                    f1 = Clamp01((d12 - rd1) / dd1);
                }

                fraction1 = f1;
                fraction2 = f2;
            }

            closest1 = p1 + fraction1 * d1;
            closest2 = p2 + fraction2 * d2;
        }

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        private static void AddPoint(ref Manifold manifold, Vec2 point, float separation, ushort id, float maxDistance)
        {
            if (separation > maxDistance) return;

            ref var mp = ref Manifold.Point(ref manifold, manifold.PointCount++);
            mp.Point = point;
            mp.Separation = separation;
            mp.Id = id;
        }

        private static bool VertexRegion(Vec2 vertex, Vec2 center, float radius, float maxDistance, Vec2 faceNormal, out Vec2 normal, out float separation)
        {
            var d = center - vertex;
            var limit = radius + maxDistance;
            var distanceSquared = d.LengthSquared;
            if (distanceSquared > limit * limit)
            {
                normal = default;
                separation = 0f;
                return false;
            }

            var distance = (float)Math.Sqrt(distanceSquared);
            normal = distance > Epsilon ? d * (1f / distance) : faceNormal;
            separation = distance - radius;
            return true;
        }

        // Polygon in the local frame of one collide call, on the stack.
        private readonly ref struct LocalPolygon
        {
            public readonly Span<Vec2> Vertices;
            public readonly Span<Vec2> Normals;
            public readonly int Count;
            public readonly float Radius;

            public LocalPolygon(Span<Vec2> vertices, Span<Vec2> normals, int count, float radius)
            {
                Vertices = vertices;
                Normals = normals;
                Count = count;
                Radius = radius;
            }

            public int Next(int index) => index + 1 < Count ? index + 1 : 0;
        }

        // Feature kind of a closest-features point, so its id differs from the clipped points of the same vertices.
        private const int ClosestKind = 1;
        private const float FloatEpsilon = 1.1920929e-7f;
        private const float Epsilon = 1e-6f;

        // Prefer shape A as the reference shape unless B is clearly better (Box2D: 0.1 × linear slop).
        private const float RelativeTolerance = 0.0005f;
    }
}
