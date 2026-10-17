using System;
using System.Collections.Generic;

namespace CombatSimulation.Physics
{
    // Convex polygon in body-local coordinates, counter-clockwise, at most MaxVertices vertices.
    // Immutable. Concave outlines are split into convex parts at load (stage P2).
    public sealed class Polygon
    {
        public const int MaxVertices = 8;

        public int Count { get; }
        public Vec2 Centroid { get; }

        // Distance from the centroid to the farthest vertex.
        public float Radius { get; }

        public Vec2 Vertex(int index) => _vertices[index];
        public Vec2 Normal(int index) => _normals[index];

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

        public MassData ComputeMass()
        {
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

        private Polygon(Vec2[] vertices)
        {
            Count = vertices.Length;
            _vertices = vertices;
            _normals = new Vec2[Count];

            for (var i = 0; i < Count; ++i)
            {
                var edge = vertices[i + 1 < Count ? i + 1 : 0] - vertices[i];
                if (edge.LengthSquared < MinEdgeLength * MinEdgeLength)
                    throw new ArgumentException("Polygon edge is too short");

                _normals[i] = Vec2.RightPerp(edge).Normalized;
            }

            for (var i = 0; i < Count; ++i)
            {
                var next = vertices[i + 1 < Count ? i + 1 : 0] - vertices[i];
                var after = vertices[(i + 2) % Count] - vertices[i];
                if (Vec2.Cross(next, after) <= 0f)
                    throw new ArgumentException("Polygon must be convex and counter-clockwise");
            }

            Centroid = ComputeMass().Centroid;
            var radius = 0f;
            for (var i = 0; i < Count; ++i)
                radius = Math.Max(radius, (vertices[i] - Centroid).Length);
            Radius = radius;
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

        private const float MinEdgeLength = 0.001f;

        private readonly Vec2[] _vertices;
        private readonly Vec2[] _normals;
    }
}
