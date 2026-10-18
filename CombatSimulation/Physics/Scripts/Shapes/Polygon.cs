using System;
using System.Collections.Generic;

namespace CombatSimulation.Physics
{
    // Convex polygon in body-local coordinates, counter-clockwise, at most MaxVertices vertices.
    // Immutable. Concave outlines are split into convex parts at load (ConvexDecomposition).
    //
    // A rounded polygon has a Radius: its surface lies Radius outside the vertex outline. The only rounded
    // polygon is the core of a Capsule (2 vertices); every other polygon has Radius 0.
    public sealed class Polygon
    {
        public const int MaxVertices = 8;

        public int Count { get; }
        public Vec2 Centroid { get; }

        // Rounding radius. Zero except for a capsule core.
        public float Radius { get; }

        // Distance from the centroid to the farthest surface point.
        public float BoundRadius { get; }

        public Vec2 Vertex(int index) => _vertices[index];
        public Vec2 Normal(int index) => _normals[index];

        // Outline data of a part of a concave outline (ConvexDecomposition.AttachOutline). Zero for other polygons.
        // Bit i is set when edge i (vertex i to vertex i + 1) is a cut: it lies inside the outline, between two parts.
        // A contact never takes its normal from a cut (Collide, "Outline parts").
        public int CutEdges { get; }

        public bool IsCut(int edge) => (CutEdges & (1 << edge)) != 0;

        // Normals of the outline edges before and after vertex i. At a vertex inside an outline edge both are that
        // edge's normal. Zero for a vertex that is not on the outline, and for a polygon without cut edges.
        public Vec2 OutlineNormalIn(int index) => _outlineIn != null ? _outlineIn[index] : default;
        public Vec2 OutlineNormalOut(int index) => _outlineOut != null ? _outlineOut[index] : default;

        // Builds the convex hull of the points. Throws for fewer than 3 hull points,
        // more than MaxVertices hull points, or a degenerate hull.
        public static Polygon FromPoints(IReadOnlyList<Vec2> points)
        {
            var hull = ConvexHull(points);
            if (hull.Count < 3)
                throw new ArgumentException("Polygon hull needs at least 3 points that are not collinear");
            if (hull.Count > MaxVertices)
                throw new ArgumentException($"Polygon hull has {hull.Count} vertices, the maximum is {MaxVertices}");

            return new Polygon(hull.ToArray());
        }

        public static Polygon Box(float halfWidth, float halfHeight) => Box(halfWidth, halfHeight, Vec2.Zero, 0f);

        public static Polygon Box(float halfWidth, float halfHeight, Vec2 center, float angle)
        {
            var rot = Rot.FromAngle(angle);
            return new Polygon(new[]
            {
                center + rot.Rotate(new Vec2(-halfWidth, -halfHeight)),
                center + rot.Rotate(new Vec2(halfWidth, -halfHeight)),
                center + rot.Rotate(new Vec2(halfWidth, halfHeight)),
                center + rot.Rotate(new Vec2(-halfWidth, halfHeight)),
            });
        }

        // Regular polygon with the given circumradius. The first vertex lies on the +X axis.
        public static Polygon Regular(int count, float radius) => Regular(count, radius, Vec2.Zero);

        public static Polygon Regular(int count, float radius, Vec2 center)
        {
            if (count < 3 || count > MaxVertices)
                throw new ArgumentOutOfRangeException(nameof(count));

            var vertices = new Vec2[count];
            for (var i = 0; i < count; ++i)
            {
                var angle = 2.0 * Math.PI * i / count;
                vertices[i] = center + new Vec2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
            }

            return new Polygon(vertices);
        }

        // Core of a capsule: the segment c1-c2, rounded by the radius. The two edges run both ways along it.
        internal static Polygon Capsule(Vec2 center1, Vec2 center2, float radius) => new Polygon(new[] { center1, center2 }, radius);

        // Mass of the vertex outline. Not valid for a capsule core (Capsule.ComputeMass).
        public MassData ComputeMass()
        {
            if (Count < 3) throw new InvalidOperationException("Capsule core has no polygon mass");

            // Triangle fan from the first vertex. Adapted from Box2D v3 (MIT, Erin Catto), b2ComputePolygonMass.
            var origin = _vertices[0];
            var center = Vec2.Zero;
            var area = 0f;
            var inertia = 0f;
            const float inv3 = 1f / 3f;

            for (var i = 1; i < Count - 1; ++i)
            {
                var e1 = _vertices[i] - origin;
                var e2 = _vertices[i + 1] - origin;
                var d = Vec2.Cross(e1, e2);
                var triangleArea = 0.5f * d;
                area += triangleArea;
                center += triangleArea * inv3 * (e1 + e2);

                var intx2 = e1.X * e1.X + e2.X * e1.X + e2.X * e2.X;
                var inty2 = e1.Y * e1.Y + e2.Y * e1.Y + e2.Y * e2.Y;
                inertia += 0.25f * inv3 * d * (intx2 + inty2);
            }

            var localCenter = center * (1f / area);
            var centroid = origin + localCenter;
            // Inertia about the fan origin -> about the centroid -> about the body origin.
            var inertiaAboutOrigin = inertia + area * (Vec2.Dot(centroid, centroid) - Vec2.Dot(localCenter, localCenter));
            return new MassData(area, centroid, inertiaAboutOrigin);
        }

        private Polygon(Vec2[] vertices, float radius = 0f)
        {
            Count = vertices.Length;
            Radius = radius;
            _vertices = vertices;
            _normals = new Vec2[Count];

            for (var i = 0; i < Count; ++i)
            {
                var edge = vertices[i + 1 < Count ? i + 1 : 0] - vertices[i];
                if (edge.LengthSquared < MinEdgeLength * MinEdgeLength)
                    throw new ArgumentException("Polygon edge is too short");

                _normals[i] = Vec2.RightPerp(edge).Normalized;
            }

            // A 2-vertex capsule core has no area, so it has no convexity to check.
            for (var i = 0; Count > 2 && i < Count; ++i)
            {
                var next = vertices[i + 1 < Count ? i + 1 : 0] - vertices[i];
                var after = vertices[(i + 2) % Count] - vertices[i];
                if (Vec2.Cross(next, after) <= 0f)
                    throw new ArgumentException("Polygon must be convex and counter-clockwise");
            }

            Centroid = Count > 2 ? ComputeMass().Centroid : Vec2.Lerp(vertices[0], vertices[1], 0.5f);
            var bound = 0f;
            for (var i = 0; i < Count; ++i)
                bound = Math.Max(bound, (vertices[i] - Centroid).Length);
            BoundRadius = bound + radius;
        }

        // Copy with outline data. Load time only.
        internal Polygon WithOutline(int cutEdges, Vec2[] normalIn, Vec2[] normalOut) => new Polygon(this, cutEdges, normalIn, normalOut);

        internal bool HasOutline(int vertex) => _arcCosine[vertex] <= 1f;

        // True when a unit direction in the local frame lies in the short arc between the outline normals of the vertex.
        internal bool InOutlineArc(int vertex, Vec2 direction) => Vec2.Dot(direction, _arcMiddle[vertex]) >= _arcCosine[vertex] - ArcTolerance;

        // The direction when it lies in the outline arc of the vertex, else the nearer end of the arc.
        internal Vec2 ClampToOutlineArc(int vertex, Vec2 direction)
        {
            if (InOutlineArc(vertex, direction)) return direction;
            return Vec2.Dot(direction, _outlineIn[vertex]) >= Vec2.Dot(direction, _outlineOut[vertex]) ? _outlineIn[vertex] : _outlineOut[vertex];
        }

        private Polygon(Polygon source, int cutEdges, Vec2[] normalIn, Vec2[] normalOut)
        {
            Count = source.Count;
            Centroid = source.Centroid;
            Radius = source.Radius;
            BoundRadius = source.BoundRadius;
            _vertices = source._vertices;
            _normals = source._normals;

            CutEdges = cutEdges;
            _outlineIn = normalIn;
            _outlineOut = normalOut;
            _arcMiddle = new Vec2[Count];
            _arcCosine = new float[Count];
            for (var i = 0; i < Count; ++i)
            {
                // A vertex off the outline, or a fold back that has no arc, never matches.
                var middle = normalIn[i] + normalOut[i];
                if (middle.LengthSquared < ArcTolerance)
                {
                    _arcCosine[i] = 2f;
                    continue;
                }

                _arcMiddle[i] = middle.Normalized;
                _arcCosine[i] = Vec2.Dot(normalIn[i], _arcMiddle[i]);
            }
        }

        // Andrew's monotone chain. Removes collinear points. Counter-clockwise result.
        private static List<Vec2> ConvexHull(IReadOnlyList<Vec2> input)
        {
            var points = new List<Vec2>(input);
            points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

            var hull = new List<Vec2>(points.Count + 1);
            for (var pass = 0; pass < 2; ++pass)
            {
                var start = hull.Count;
                for (var k = 0; k < points.Count; ++k)
                {
                    var p = points[pass == 0 ? k : points.Count - 1 - k];
                    while (hull.Count >= start + 2 && Vec2.Cross(hull[hull.Count - 1] - hull[hull.Count - 2], p - hull[hull.Count - 2]) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            for (var i = hull.Count - 1; i > 0; --i)
                if (Vec2.DistanceSquared(hull[i], hull[i - 1]) < MinEdgeLength * MinEdgeLength)
                    hull.RemoveAt(i);

            return hull;
        }

        internal const float MinEdgeLength = 0.001f;

        // Cosine margin of the outline arc test, about 0.8° at a straight seam.
        internal const float ArcTolerance = 1e-4f;

        private readonly Vec2[] _vertices;
        private readonly Vec2[] _normals;
        private readonly Vec2[] _outlineIn;
        private readonly Vec2[] _outlineOut;
        private readonly Vec2[] _arcMiddle;
        private readonly float[] _arcCosine;
    }
}
