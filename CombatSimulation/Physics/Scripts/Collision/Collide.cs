namespace CombatSimulation.Physics
{
    // Contact manifolds for pairs of convex shapes.
    //
    // Both poses are in one unwrapped frame: the caller shifts B to its nearest torus image of A.
    // Each function keeps the points with separation <= maxDistance. Points with a positive
    // separation are speculative: the solver lets the shapes close that gap in the step, but no more.
    //
    // Clipping and reference-face selection are adapted from Box2D (MIT, Erin Catto).
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

            var distance = (float)System.Math.Sqrt(distanceSquared);
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

        public static void Polygons(Polygon a, in Pose xfA, Polygon b, in Pose xfB, float maxDistance, out Manifold manifold)
        {
            manifold = default;

            var separationA = FindMaxSeparation(a, xfA, b, xfB, out var edgeA);
            if (separationA > maxDistance) return;

            var separationB = FindMaxSeparation(b, xfB, a, xfA, out var edgeB);
            if (separationB > maxDistance) return;

            Polygon poly1, poly2;
            Pose xf1, xf2;
            int edge1;
            bool flip;

            if (separationB > separationA + RelativeTolerance)
            {
                poly1 = b; poly2 = a; xf1 = xfB; xf2 = xfA; edge1 = edgeB; flip = true;
            }
            else
            {
                poly1 = a; poly2 = b; xf1 = xfA; xf2 = xfB; edge1 = edgeA; flip = false;
            }

            // Incident edge on poly2: the edge most anti-parallel to the reference normal.
            var referenceNormal = xf2.Q.InvRotate(xf1.Q.Rotate(poly1.Normal(edge1)));
            var incident = 0;
            var minDot = float.MaxValue;
            for (var i = 0; i < poly2.Count; ++i)
            {
                var dot = Vec2.Dot(referenceNormal, poly2.Normal(i));
                if (dot < minDot)
                {
                    minDot = dot;
                    incident = i;
                }
            }

            var incident2 = incident + 1 < poly2.Count ? incident + 1 : 0;
            var c0 = new ClipVertex(xf2.TransformPoint(poly2.Vertex(incident)), Manifold.MakeId(edge1, incident));
            var c1 = new ClipVertex(xf2.TransformPoint(poly2.Vertex(incident2)), Manifold.MakeId(edge1, incident2));

            var iv1 = edge1;
            var iv2 = edge1 + 1 < poly1.Count ? edge1 + 1 : 0;
            var v11 = poly1.Vertex(iv1);
            var v12 = poly1.Vertex(iv2);
            var tangent = xf1.Q.Rotate((v12 - v11).Normalized);
            var normal = Vec2.RightPerp(tangent);
            v11 = xf1.TransformPoint(v11);
            v12 = xf1.TransformPoint(v12);

            // When the shapes are apart, a corner can approach past the end of the reference face.
            // Widen the side planes by the gap, so the corner still gets a speculative point.
            // The point measures against the face plane, which is conservative (it can stop early, never late).
            var sideMargin = Utilites.MathEx.Max(separationA, separationB) > 0f ? maxDistance : 0f;
            var frontOffset = Vec2.Dot(normal, v11);
            var sideOffset1 = -Vec2.Dot(tangent, v11) + sideMargin;
            var sideOffset2 = Vec2.Dot(tangent, v12) + sideMargin;

            // Clip the incident edge against the side planes of the reference face.
            if (!ClipSegment(ref c0, ref c1, -tangent, sideOffset1, iv1)) return;
            if (!ClipSegment(ref c0, ref c1, tangent, sideOffset2, iv2)) return;

            manifold.Normal = flip ? -normal : normal;
            AddClipPoint(ref manifold, c0, normal, frontOffset, maxDistance, flip);
            AddClipPoint(ref manifold, c1, normal, frontOffset, maxDistance, flip);
            if (manifold.PointCount == 0) manifold = default;
        }

        // Largest separation of poly2 along the face normals of poly1, and the face that gives it.
        private static float FindMaxSeparation(Polygon poly1, in Pose xf1, Polygon poly2, in Pose xf2, out int edgeIndex)
        {
            // Pose of poly1 in the frame of poly2.
            var q = new Rot(xf2.Q.C * xf1.Q.C + xf2.Q.S * xf1.Q.S, xf2.Q.C * xf1.Q.S - xf2.Q.S * xf1.Q.C);
            var p = xf2.Q.InvRotate(xf1.P - xf2.P);

            edgeIndex = 0;
            var maxSeparation = float.MinValue;
            for (var i = 0; i < poly1.Count; ++i)
            {
                var n = q.Rotate(poly1.Normal(i));
                var v1 = q.Rotate(poly1.Vertex(i)) + p;

                var minSeparation = float.MaxValue;
                for (var j = 0; j < poly2.Count; ++j)
                {
                    var s = Vec2.Dot(n, poly2.Vertex(j) - v1);
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

        // Keeps the part of segment c0-c1 behind the plane dot(normal, v) = offset.
        private static bool ClipSegment(ref ClipVertex c0, ref ClipVertex c1, Vec2 normal, float offset, int referenceVertex)
        {
            var d0 = Vec2.Dot(normal, c0.V) - offset;
            var d1 = Vec2.Dot(normal, c1.V) - offset;

            if (d0 <= 0f && d1 <= 0f) return true;
            if (d0 > 0f && d1 > 0f) return false;

            var t = d0 / (d0 - d1);
            var clipped = new ClipVertex(Vec2.Lerp(c0.V, c1.V, t), Manifold.MakeId(referenceVertex, c0.Id >> 4, ClippedKind));
            if (d0 > 0f) c0 = clipped;
            else c1 = clipped;
            return true;
        }

        private static void AddClipPoint(ref Manifold manifold, in ClipVertex clip, Vec2 normal, float frontOffset, float maxDistance, bool flip)
        {
            var separation = Vec2.Dot(normal, clip.V) - frontOffset;
            if (separation > maxDistance) return;

            ref var point = ref Manifold.Point(ref manifold, manifold.PointCount++);
            point.Point = clip.V - 0.5f * separation * normal;
            point.Separation = separation;
            point.Id = flip ? Manifold.FlipId(clip.Id) : clip.Id;
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

            var distance = (float)System.Math.Sqrt(distanceSquared);
            normal = distance > Epsilon ? d * (1f / distance) : faceNormal;
            separation = distance - radius;
            return true;
        }

        private readonly struct ClipVertex
        {
            public readonly Vec2 V;
            public readonly ushort Id;

            public ClipVertex(Vec2 v, ushort id)
            {
                V = v;
                Id = id;
            }
        }

        private const int ClippedKind = 1;
        private const float Epsilon = 1e-6f;

        // Prefer shape A as the reference shape unless B is clearly better (Box2D: 0.1 × linear slop).
        private const float RelativeTolerance = 0.0005f;
    }
}
