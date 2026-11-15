namespace CombatSimulation.Physics
{
    // Convex shape for the distance and time-of-impact queries: points in body-local coordinates plus a radius.
    // A circle is one point; a capsule is its 2-point core.
    internal readonly struct DistanceProxy
    {
        private readonly Polygon _polygon;
        private readonly Vec2 _point;

        public readonly float Radius;

        private DistanceProxy(Polygon polygon, Vec2 point, float radius)
        {
            _polygon = polygon;
            _point = point;
            Radius = radius;
        }

        public static DistanceProxy FromCircle(in Circle circle) => new DistanceProxy(null, circle.Center, circle.Radius);
        public static DistanceProxy FromPolygon(Polygon polygon) => new DistanceProxy(polygon, Vec2.Zero, polygon.Radius);

        // A point without radius: the moving end of a ray, or the centre of an overlap circle.
        public static DistanceProxy FromPoint(Vec2 point, float radius = 0f) => new DistanceProxy(null, point, radius);

        public static DistanceProxy FromShape(in Shape shape) =>
            shape.Kind == ShapeKind.Circle ? FromCircle(shape.Circle) : FromPolygon(shape.Polygon);

        public int Count => _polygon == null ? 1 : _polygon.Count;

        // The polygon (or capsule core) behind the proxy; null for a circle or a point.
        public Polygon Polygon => _polygon;
        public Vec2 Vertex(int index) => _polygon == null ? _point : _polygon.Vertex(index);

        // Index of the point farthest along the local direction; a tie goes to the lower index.
        public int Support(Vec2 direction)
        {
            if (_polygon == null) return 0;

            var best = 0;
            var bestValue = Vec2.Dot(_polygon.Vertex(0), direction);
            for (var i = 1; i < _polygon.Count; ++i)
            {
                var value = Vec2.Dot(_polygon.Vertex(i), direction);
                if (value > bestValue)
                {
                    best = i;
                    bestValue = value;
                }
            }

            return best;
        }
    }

    // Vertex pairs of the last simplex. Starts the next query from the same features (warm start).
    internal struct SimplexCache
    {
        public int Count;
        public int IndexA0, IndexA1, IndexA2;
        public int IndexB0, IndexB1, IndexB2;

        public int IndexA(int i) => i == 0 ? IndexA0 : i == 1 ? IndexA1 : IndexA2;
        public int IndexB(int i) => i == 0 ? IndexB0 : i == 1 ? IndexB1 : IndexB2;
    }

    // Distance between the point sets of two convex proxies (GJK). Adapted from Box2D (MIT, Erin Catto):
    // b2ShapeDistance, b2Simplex (Solve2, Solve3, search direction, witness points).
    internal static class Distance
    {
        // Distance between the surfaces, zero when the shapes overlap. Witness points on each surface.
        public static float Shapes(in DistanceProxy proxyA, in Pose xfA, in DistanceProxy proxyB, in Pose xfB, out Vec2 pointA, out Vec2 pointB)
        {
            var cache = default(SimplexCache);
            var distance = Cores(proxyA, xfA, proxyB, xfB, ref cache, out pointA, out pointB);

            var rA = proxyA.Radius;
            var rB = proxyB.Radius;
            if (distance > rA + rB && distance > Epsilon)
            {
                // The shapes are apart: move the witness points to the surfaces.
                var normal = (pointB - pointA) * (1f / distance);
                pointA += rA * normal;
                pointB -= rB * normal;
                return distance - rA - rB;
            }

            // Overlap: both points at the middle.
            var middle = Vec2.Lerp(pointA, pointB, 0.5f);
            pointA = middle;
            pointB = middle;
            return 0f;
        }

        // Distance between the point sets, without radii. The cache gives the start simplex and keeps the end simplex.
        public static float Cores(in DistanceProxy proxyA, in Pose xfA, in DistanceProxy proxyB, in Pose xfB, ref SimplexCache cache,
            out Vec2 pointA, out Vec2 pointB)
        {
            var simplex = new Simplex();
            simplex.ReadCache(cache, proxyA, xfA, proxyB, xfB);

            var saveA = new SaveIndices();
            var saveB = new SaveIndices();

            for (var iteration = 0;; ++iteration)
            {
                var saveCount = simplex.Count;
                for (var i = 0; i < saveCount; ++i)
                {
                    saveA.Set(i, Simplex.Vertex(ref simplex, i).IndexA);
                    saveB.Set(i, Simplex.Vertex(ref simplex, i).IndexB);
                }

                if (simplex.Count == 2) simplex.Solve2();
                else if (simplex.Count == 3) simplex.Solve3();

                // Three points: the origin is inside the triangle, so the shapes overlap.
                if (simplex.Count == 3 || iteration == MaxIterations) break;

                var d = simplex.SearchDirection();

                // The origin is on the segment or very close to the point: overlap or touching.
                if (Vec2.Dot(d, d) < Epsilon * Epsilon) break;

                // New support point: support(B, d) - support(A, -d).
                ref var vertex = ref Simplex.Vertex(ref simplex, simplex.Count);
                vertex.IndexA = proxyA.Support(xfA.Q.InvRotate(-d));
                vertex.WA = xfA.TransformPoint(proxyA.Vertex(vertex.IndexA));
                vertex.IndexB = proxyB.Support(xfB.Q.InvRotate(d));
                vertex.WB = xfB.TransformPoint(proxyB.Vertex(vertex.IndexB));
                vertex.W = vertex.WB - vertex.WA;

                // A support point found before means no progress: the main exit.
                var duplicate = false;
                for (var i = 0; i < saveCount; ++i)
                {
                    if (vertex.IndexA != saveA.Get(i) || vertex.IndexB != saveB.Get(i)) continue;
                    duplicate = true;
                    break;
                }

                if (duplicate) break;
                ++simplex.Count;
            }

            simplex.WitnessPoints(out pointA, out pointB);
            simplex.WriteCache(ref cache);
            return (pointB - pointA).Length;
        }

        private struct SimplexVertex
        {
            public Vec2 WA;
            public Vec2 WB;

            // WB - WA: a point of the Minkowski difference.
            public Vec2 W;

            // Barycentric coordinate of the closest point.
            public float A;

            public int IndexA;
            public int IndexB;
        }

        private struct SaveIndices
        {
            private int _i0, _i1, _i2;

            public void Set(int i, int value)
            {
                if (i == 0) _i0 = value;
                else if (i == 1) _i1 = value;
                else _i2 = value;
            }

            public int Get(int i) => i == 0 ? _i0 : i == 1 ? _i1 : _i2;
        }

        private struct Simplex
        {
            public SimplexVertex V1;
            public SimplexVertex V2;
            public SimplexVertex V3;
            public int Count;

            public static ref SimplexVertex Vertex(ref Simplex simplex, int i)
            {
                if (i == 0) return ref simplex.V1;
                if (i == 1) return ref simplex.V2;
                return ref simplex.V3;
            }

            public void ReadCache(in SimplexCache cache, in DistanceProxy proxyA, in Pose xfA, in DistanceProxy proxyB, in Pose xfB)
            {
                Count = cache.Count;
                for (var i = 0; i < Count; ++i)
                {
                    ref var v = ref Vertex(ref this, i);
                    v.IndexA = cache.IndexA(i);
                    v.IndexB = cache.IndexB(i);
                    v.WA = xfA.TransformPoint(proxyA.Vertex(v.IndexA));
                    v.WB = xfB.TransformPoint(proxyB.Vertex(v.IndexB));
                    v.W = v.WB - v.WA;
                    v.A = -1f;
                }

                if (Count > 0) return;

                V1.IndexA = 0;
                V1.IndexB = 0;
                V1.WA = xfA.TransformPoint(proxyA.Vertex(0));
                V1.WB = xfB.TransformPoint(proxyB.Vertex(0));
                V1.W = V1.WB - V1.WA;
                V1.A = 1f;
                Count = 1;
            }

            public void WriteCache(ref SimplexCache cache)
            {
                cache.Count = Count;
                cache.IndexA0 = V1.IndexA;
                cache.IndexB0 = V1.IndexB;
                cache.IndexA1 = V2.IndexA;
                cache.IndexB1 = V2.IndexB;
                cache.IndexA2 = V3.IndexA;
                cache.IndexB2 = V3.IndexB;
            }

            public Vec2 SearchDirection()
            {
                if (Count == 1) return -V1.W;

                var e12 = V2.W - V1.W;
                var sign = Vec2.Cross(e12, -V1.W);

                // Towards the origin: left of e12 when the origin is on the left, otherwise right.
                return sign > 0f ? Vec2.LeftPerp(e12) : Vec2.RightPerp(e12);
            }

            public void WitnessPoints(out Vec2 pointA, out Vec2 pointB)
            {
                if (Count == 1)
                {
                    pointA = V1.WA;
                    pointB = V1.WB;
                }
                else if (Count == 2)
                {
                    pointA = V1.A * V1.WA + V2.A * V2.WA;
                    pointB = V1.A * V1.WB + V2.A * V2.WB;
                }
                else
                {
                    pointA = V1.A * V1.WA + V2.A * V2.WA + V3.A * V3.WA;
                    pointB = pointA;
                }
            }

            // Closest point of a segment to the origin, in barycentric coordinates.
            public void Solve2()
            {
                var w1 = V1.W;
                var w2 = V2.W;
                var e12 = w2 - w1;

                // w1 region.
                var d12_2 = -Vec2.Dot(w1, e12);
                if (d12_2 <= 0f)
                {
                    V1.A = 1f;
                    Count = 1;
                    return;
                }

                // w2 region.
                var d12_1 = Vec2.Dot(w2, e12);
                if (d12_1 <= 0f)
                {
                    V2.A = 1f;
                    Count = 1;
                    V1 = V2;
                    return;
                }

                var inv = 1f / (d12_1 + d12_2);
                V1.A = d12_1 * inv;
                V2.A = d12_2 * inv;
                Count = 2;
            }

            // Closest point of a triangle to the origin: a vertex, an edge or the inside.
            public void Solve3()
            {
                var w1 = V1.W;
                var w2 = V2.W;
                var w3 = V3.W;

                var e12 = w2 - w1;
                var d12_1 = Vec2.Dot(w2, e12);
                var d12_2 = -Vec2.Dot(w1, e12);

                var e13 = w3 - w1;
                var d13_1 = Vec2.Dot(w3, e13);
                var d13_2 = -Vec2.Dot(w1, e13);

                var e23 = w3 - w2;
                var d23_1 = Vec2.Dot(w3, e23);
                var d23_2 = -Vec2.Dot(w2, e23);

                var n123 = Vec2.Cross(e12, e13);
                var d123_1 = n123 * Vec2.Cross(w2, w3);
                var d123_2 = n123 * Vec2.Cross(w3, w1);
                var d123_3 = n123 * Vec2.Cross(w1, w2);

                // w1 region.
                if (d12_2 <= 0f && d13_2 <= 0f)
                {
                    V1.A = 1f;
                    Count = 1;
                    return;
                }

                // e12.
                if (d12_1 > 0f && d12_2 > 0f && d123_3 <= 0f)
                {
                    var inv = 1f / (d12_1 + d12_2);
                    V1.A = d12_1 * inv;
                    V2.A = d12_2 * inv;
                    Count = 2;
                    return;
                }

                // e13.
                if (d13_1 > 0f && d13_2 > 0f && d123_2 <= 0f)
                {
                    var inv = 1f / (d13_1 + d13_2);
                    V1.A = d13_1 * inv;
                    V3.A = d13_2 * inv;
                    Count = 2;
                    V2 = V3;
                    return;
                }

                // w2 region.
                if (d12_1 <= 0f && d23_2 <= 0f)
                {
                    V2.A = 1f;
                    Count = 1;
                    V1 = V2;
                    return;
                }

                // w3 region.
                if (d13_1 <= 0f && d23_1 <= 0f)
                {
                    V3.A = 1f;
                    Count = 1;
                    V1 = V3;
                    return;
                }

                // e23.
                if (d23_1 > 0f && d23_2 > 0f && d123_1 <= 0f)
                {
                    var inv = 1f / (d23_1 + d23_2);
                    V2.A = d23_1 * inv;
                    V3.A = d23_2 * inv;
                    Count = 2;
                    V1 = V3;
                    return;
                }

                // Inside the triangle.
                var invTriangle = 1f / (d123_1 + d123_2 + d123_3);
                V1.A = d123_1 * invTriangle;
                V2.A = d123_2 * invTriangle;
                V3.A = d123_3 * invTriangle;
                Count = 3;
            }
        }

        private const int MaxIterations = 20;
        private const float Epsilon = 1.1920929e-7f;
    }
}
