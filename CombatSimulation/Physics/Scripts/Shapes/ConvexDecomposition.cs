using System;
using System.Collections.Generic;

namespace CombatSimulation.Physics
{
    // Splits a concave outline into convex parts once, at load ("load-time split", investigation step 5,
    // owner decision 1). The parts cover the outline exactly and have at most Polygon.MaxVertices vertices.
    // The parts of one outline go into one body as separate shapes (a compound body).
    //
    // Method:
    // 1. Clean the outline: weld near-duplicate points, drop collinear points, make it counter-clockwise,
    //    reject a self-intersecting outline.
    // 2. Bayazit's split: at each reflex (inward) vertex, cut to the best visible vertex inside the cone that
    //    resolves it, preferring other reflex vertices and near ones; when the cone holds no vertex, cut to the
    //    middle of the cone on the opposite edge (a new vertex). Convex pieces with too many vertices are cut
    //    in half.
    // 3. Hertel–Mehlhorn merge: remove a cut between two pieces while the union stays convex and within
    //    Polygon.MaxVertices, largest union first.
    // 4. Outline data on each part (AttachOutline): the cut edges and the outline normals at its vertices.
    //
    // Plain arithmetic in a fixed order, no hashing and no trigonometry, so the same outline gives the same
    // parts on every run of the same build. Allocates; for loading, not for the step.
    public static class ConvexDecomposition
    {
        public static List<Polygon> Decompose(IReadOnlyList<Vec2> outline)
        {
            var parts = new List<Polygon>();
            Decompose(outline, parts);
            return parts;
        }

        // Appends the parts of one outline path to 'parts'. The outline may have either winding; its points
        // are in body units (scale it first: Polygon has an absolute minimum edge length). A collider with
        // several paths is decomposed path by path. Throws for a self-intersecting outline, or for one with
        // fewer than 3 points that are not collinear.
        public static void Decompose(IReadOnlyList<Vec2> outline, List<Polygon> parts)
        {
            if (outline == null) throw new ArgumentNullException(nameof(outline));

            var points = Clean(outline);
            if (points.Count < 3) throw new ArgumentException("Outline needs at least 3 points that are not collinear");
            if (Area(points) < 0f) points.Reverse();
            CheckSimple(points);

            var pieces = new List<List<Vec2>>();
            Split(points, pieces, 0);
            Merge(pieces);

            var first = parts.Count;
            foreach (var piece in pieces)
                parts.Add(Polygon.FromPoints(piece));
            Attach(parts, first, points);
        }

        // Replaces each part with a copy that knows its cut edges and the outline normals at its vertices
        // (Polygon.CutEdges). The parts must cover the outline exactly, with every vertex on the outline or on a cut.
        // Decompose does this itself; call it for parts made by hand. A part with no cut edge stays as it is.
        public static void AttachOutline(List<Polygon> parts, IReadOnlyList<Vec2> outline)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            if (outline == null) throw new ArgumentNullException(nameof(outline));

            var points = Clean(outline);
            if (points.Count < 3) throw new ArgumentException("Outline needs at least 3 points that are not collinear");
            if (Area(points) < 0f) points.Reverse();
            Attach(parts, 0, points);
        }

        // Matches the parts from index 'first' against a clean counter-clockwise outline, with the weld distance as
        // tolerance. A part edge is outer when it runs along an outline edge for more than the tolerance; this keeps
        // an edge outer when it continues a cut in a straight line (the hull drops the vertex between them).
        private static void Attach(List<Polygon> parts, int first, List<Vec2> outline)
        {
            var tolerance = WeldDistance(outline);
            var n = outline.Count;
            var normals = new Vec2[n];
            for (var i = 0; i < n; ++i)
                normals[i] = Vec2.RightPerp(At(outline, i + 1) - outline[i]).Normalized;

            for (var p = first; p < parts.Count; ++p)
            {
                var part = parts[p];
                var cuts = 0;
                var normalIn = new Vec2[part.Count];
                var normalOut = new Vec2[part.Count];
                for (var i = 0; i < part.Count; ++i)
                {
                    var a = part.Vertex(i);
                    var b = part.Vertex(i + 1 < part.Count ? i + 1 : 0);
                    if (!RunsAlongOutline(a, b, outline, normals, tolerance)) cuts |= 1 << i;
                    OutlineNormals(a, outline, normals, tolerance, out normalIn[i], out normalOut[i]);
                }

                if (cuts != 0) parts[p] = part.WithOutline(cuts, normalIn, normalOut);
            }
        }

        private static bool RunsAlongOutline(Vec2 a, Vec2 b, List<Vec2> outline, Vec2[] normals, float tolerance)
        {
            for (var j = 0; j < outline.Count; ++j)
            {
                var o = outline[j];
                if (Math.Abs(Vec2.Dot(a - o, normals[j])) > tolerance || Math.Abs(Vec2.Dot(b - o, normals[j])) > tolerance) continue;

                // Both run counter-clockwise, so an outer edge has the direction of its outline edge.
                var tangent = Vec2.LeftPerp(normals[j]);
                var ta = Vec2.Dot(a - o, tangent);
                var tb = Vec2.Dot(b - o, tangent);
                var length = Vec2.Dot(At(outline, j + 1) - o, tangent);
                if (tb > ta && Math.Min(tb, length) - Math.Max(ta, 0f) > tolerance) return true;
            }

            return false;
        }

        // Normals of the outline edges at a point: before and after an outline vertex, or both the normal of the
        // outline edge that holds the point. Zero for a point off the outline.
        private static void OutlineNormals(Vec2 point, List<Vec2> outline, Vec2[] normals, float tolerance, out Vec2 normalIn, out Vec2 normalOut)
        {
            var n = outline.Count;
            for (var j = 0; j < n; ++j)
            {
                if (Vec2.DistanceSquared(point, outline[j]) > tolerance * tolerance) continue;
                normalIn = normals[(j + n - 1) % n];
                normalOut = normals[j];
                return;
            }

            for (var j = 0; j < n; ++j)
            {
                var o = outline[j];
                var tangent = Vec2.LeftPerp(normals[j]);
                var t = Vec2.Dot(point - o, tangent);
                if (Math.Abs(Vec2.Dot(point - o, normals[j])) > tolerance || t < 0f || t > Vec2.Dot(At(outline, j + 1) - o, tangent)) continue;
                normalIn = normals[j];
                normalOut = normals[j];
                return;
            }

            normalIn = default;
            normalOut = default;
        }

        // Area of a simple outline by the shoelace formula; positive for counter-clockwise.
        public static float Area(IReadOnlyList<Vec2> outline)
        {
            var area = 0.0;
            for (var i = 0; i < outline.Count; ++i)
            {
                var a = outline[i];
                var b = outline[i + 1 < outline.Count ? i + 1 : 0];
                area += (double)a.X * b.Y - (double)b.X * a.Y;
            }

            return (float)(0.5 * area);
        }

        private static List<Vec2> Clean(IReadOnlyList<Vec2> outline)
        {
            if (outline.Count == 0) return new List<Vec2>();

            var weld = WeldDistance(outline);
            var weldSquared = weld * weld;

            var points = new List<Vec2>(outline.Count);
            foreach (var point in outline)
                if (points.Count == 0 || Vec2.DistanceSquared(points[points.Count - 1], point) > weldSquared)
                    points.Add(point);

            while (points.Count > 1 && Vec2.DistanceSquared(points[0], points[points.Count - 1]) <= weldSquared)
                points.RemoveAt(points.Count - 1);

            // Collinear points and zero-width spikes; removing one can make its neighbour collinear.
            var changed = true;
            while (changed && points.Count >= 3)
            {
                changed = false;
                for (var i = 0; i < points.Count && points.Count >= 3;)
                {
                    if (IsCollinear(At(points, i - 1), points[i], At(points, i + 1)))
                    {
                        points.RemoveAt(i);
                        changed = true;
                    }
                    else
                    {
                        ++i;
                    }
                }
            }

            return points;
        }

        // Points closer than this are welded: a fraction of the outline size, at least twice the shortest polygon edge.
        private static float WeldDistance(IReadOnlyList<Vec2> outline)
        {
            var lower = outline[0];
            var upper = outline[0];
            foreach (var point in outline)
            {
                lower = Vec2.Min(lower, point);
                upper = Vec2.Max(upper, point);
            }

            var size = Math.Max(upper.X - lower.X, upper.Y - lower.Y);
            return Math.Max(WeldFraction * size, 2f * Polygon.MinEdgeLength);
        }

        private static void CheckSimple(List<Vec2> points)
        {
            var n = points.Count;
            for (var i = 0; i < n; ++i)
            for (var j = i + 2; j < n; ++j)
            {
                // The last edge shares a vertex with the first.
                if (i == 0 && j == n - 1) continue;

                if (SegmentsTouch(points[i], At(points, i + 1), points[j], At(points, j + 1)))
                    throw new ArgumentException($"Outline intersects itself: edge {i} and edge {j}");
            }
        }

        // Bayazit's split, after Mark Bayazit's description of his algorithm.
        private static void Split(List<Vec2> polygon, List<List<Vec2>> pieces, int depth)
        {
            if (depth > MaxDepth) throw new ArgumentException("Outline could not be decomposed");

            var n = polygon.Count;
            for (var i = 0; i < n; ++i)
            {
                if (!IsReflex(polygon, i)) continue;

                var prev = At(polygon, i - 1);
                var cur = polygon[i];
                var next = At(polygon, i + 1);

                // The two edges at i, extended beyond i, hit the outline at lowerPoint and upperPoint.
                // The vertices between the hits are the cut candidates that resolve the reflex vertex.
                var lowerDistance = double.MaxValue;
                var upperDistance = double.MaxValue;
                var lowerPoint = default(Vec2);
                var upperPoint = default(Vec2);
                var lowerIndex = 0;
                var upperIndex = 0;

                for (var j = 0; j < n; ++j)
                {
                    if (Orient(prev, cur, polygon[j]) > 0.0 && Orient(prev, cur, At(polygon, j - 1)) <= 0.0)
                    {
                        var p = LineIntersection(prev, cur, polygon[j], At(polygon, j - 1));
                        if (Orient(next, cur, p) < 0.0)
                        {
                            var d = Vec2.DistanceSquared(cur, p);
                            if (d < lowerDistance)
                            {
                                lowerDistance = d;
                                lowerPoint = p;
                                lowerIndex = j;
                            }
                        }
                    }

                    if (Orient(next, cur, At(polygon, j + 1)) > 0.0 && Orient(next, cur, polygon[j]) <= 0.0)
                    {
                        var p = LineIntersection(next, cur, polygon[j], At(polygon, j + 1));
                        if (Orient(prev, cur, p) > 0.0)
                        {
                            var d = Vec2.DistanceSquared(cur, p);
                            if (d < upperDistance)
                            {
                                upperDistance = d;
                                upperPoint = p;
                                upperIndex = j;
                            }
                        }
                    }
                }

                List<Vec2> lower, upper;
                if (lowerIndex == (upperIndex + 1) % n)
                {
                    // No vertex between the hits: cut to the middle of the two hit points on that edge.
                    var p = Vec2.Lerp(lowerPoint, upperPoint, 0.5f);
                    lower = Copy(polygon, i, upperIndex);
                    lower.Add(p);
                    upper = Copy(polygon, lowerIndex, i);
                    upper.Add(p);
                }
                else
                {
                    var bestIndex = -1;
                    var bestScore = 0.0;
                    var end = upperIndex;
                    while (end < lowerIndex) end += n;

                    for (var k = lowerIndex; k <= end; ++k)
                    {
                        var j = k % n;
                        if (!CanSee(polygon, i, j)) continue;

                        // Near vertices first; a reflex vertex that the cut also resolves is best.
                        var score = 1.0 / (Vec2.DistanceSquared(cur, polygon[j]) + 1.0);
                        if (IsReflex(polygon, j))
                            score += Orient(At(polygon, j - 1), polygon[j], cur) <= 0.0 && Orient(At(polygon, j + 1), polygon[j], cur) >= 0.0 ? 3.0 : 2.0;
                        else
                            score += 1.0;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestIndex = j;
                        }
                    }

                    if (bestIndex < 0) throw new ArgumentException("Outline could not be decomposed: no visible vertex");

                    lower = Copy(polygon, i, bestIndex);
                    upper = Copy(polygon, bestIndex, i);
                }

                Split(lower, pieces, depth + 1);
                Split(upper, pieces, depth + 1);
                return;
            }

            if (n > Polygon.MaxVertices)
            {
                Split(Copy(polygon, 0, n / 2), pieces, depth + 1);
                Split(Copy(polygon, n / 2, 0), pieces, depth + 1);
                return;
            }

            pieces.Add(polygon);
        }

        // True when the segment i-j runs inside the polygon: it starts into the interior at both ends and
        // touches no edge except those at i and j.
        private static bool CanSee(List<Vec2> polygon, int i, int j)
        {
            if (!LeavesInward(polygon, i, polygon[j]) || !LeavesInward(polygon, j, polygon[i])) return false;

            var n = polygon.Count;
            for (var k = 0; k < n; ++k)
            {
                var k1 = k + 1 < n ? k + 1 : 0;
                if (k == i || k1 == i || k == j || k1 == j) continue;
                if (SegmentsTouch(polygon[i], polygon[j], polygon[k], polygon[k1])) return false;
            }

            return true;
        }

        // True when a segment from vertex i to 'target' starts into the interior of the polygon.
        private static bool LeavesInward(List<Vec2> polygon, int i, Vec2 target)
        {
            var cur = polygon[i];
            var prev = At(polygon, i - 1);
            var next = At(polygon, i + 1);

            if (IsReflex(polygon, i))
                return !(Orient(cur, prev, target) >= 0.0 && Orient(cur, next, target) <= 0.0);

            return !(Orient(cur, next, target) <= 0.0 || Orient(cur, prev, target) >= 0.0);
        }

        // Hertel–Mehlhorn: merges two pieces across their shared cut while the union stays convex and
        // within Polygon.MaxVertices. The largest union goes first.
        private static void Merge(List<List<Vec2>> pieces)
        {
            while (true)
            {
                var bestP = -1;
                var bestQ = -1;
                List<Vec2> bestMerged = null;
                var bestArea = double.MinValue;

                for (var p = 0; p < pieces.Count; ++p)
                for (var q = p + 1; q < pieces.Count; ++q)
                {
                    var merged = TryMerge(pieces[p], pieces[q]);
                    if (merged == null) continue;

                    var area = Area(merged);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestP = p;
                        bestQ = q;
                        bestMerged = merged;
                    }
                }

                if (bestMerged == null) return;

                pieces[bestP] = bestMerged;
                pieces.RemoveAt(bestQ);
            }
        }

        // The union of two pieces that share an edge, or null when they share none, or when the union is not
        // convex or has too many vertices. A shared vertex that becomes collinear is dropped.
        private static List<Vec2> TryMerge(List<Vec2> p, List<Vec2> q)
        {
            for (var i = 0; i < p.Count; ++i)
            {
                var u = p[i];
                var v = At(p, i + 1);
                for (var j = 0; j < q.Count; ++j)
                {
                    if (!q[j].Equals(v) || !At(q, j + 1).Equals(u)) continue;

                    // p from v around to u, then q after u up to (not including) v.
                    var merged = new List<Vec2>(p.Count + q.Count - 2);
                    for (var k = 1; k <= p.Count; ++k)
                        merged.Add(p[(i + k) % p.Count]);
                    for (var k = 2; k < q.Count; ++k)
                        merged.Add(q[(j + k) % q.Count]);

                    // Only the shared vertices can turn reflex or collinear.
                    if (!FixJunction(merged, u) || !FixJunction(merged, v)) return null;
                    return merged.Count <= Polygon.MaxVertices ? merged : null;
                }
            }

            return null;
        }

        // False when the vertex is reflex in the merged piece. Removes it when it is collinear.
        private static bool FixJunction(List<Vec2> merged, Vec2 vertex)
        {
            var k = merged.IndexOf(vertex);
            var prev = At(merged, k - 1);
            var next = At(merged, k + 1);

            if (IsCollinear(prev, vertex, next))
            {
                merged.RemoveAt(k);
                return true;
            }

            return Orient(prev, vertex, next) > 0.0;
        }

        // Vertices from i to j (both included), going forward.
        private static List<Vec2> Copy(List<Vec2> polygon, int i, int j)
        {
            var n = polygon.Count;
            while (j < i) j += n;

            var result = new List<Vec2>(j - i + 1);
            for (; i <= j; ++i)
                result.Add(polygon[i % n]);
            return result;
        }

        private static Vec2 At(List<Vec2> polygon, int index)
        {
            var n = polygon.Count;
            return polygon[((index % n) + n) % n];
        }

        private static bool IsReflex(List<Vec2> polygon, int i) => Orient(At(polygon, i - 1), polygon[i], At(polygon, i + 1)) < 0.0;

        // Twice the signed area of triangle a-b-c; positive when counter-clockwise.
        private static double Orient(Vec2 a, Vec2 b, Vec2 c) =>
            ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

        // The turn at 'cur' is below CollinearSine; also true for a spike that folds back.
        private static bool IsCollinear(Vec2 prev, Vec2 cur, Vec2 next)
        {
            var cross = Orient(prev, cur, next);
            var lengths = Math.Sqrt(Vec2.DistanceSquared(prev, cur)) * Math.Sqrt(Vec2.DistanceSquared(cur, next));
            return Math.Abs(cross) <= CollinearSine * lengths;
        }

        // Intersection of the lines p1-p2 and q1-q2. The callers only pass lines that cross.
        private static Vec2 LineIntersection(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2)
        {
            double a1 = (double)p2.Y - p1.Y, b1 = (double)p1.X - p2.X;
            double c1 = a1 * p1.X + b1 * p1.Y;
            double a2 = (double)q2.Y - q1.Y, b2 = (double)q1.X - q2.X;
            double c2 = a2 * q1.X + b2 * q1.Y;
            var det = a1 * b2 - a2 * b1;
            return new Vec2((float)((b2 * c1 - b1 * c2) / det), (float)((a1 * c2 - a2 * c1) / det));
        }

        // Segments a-b and c-d cross or touch.
        private static bool SegmentsTouch(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            var o1 = Math.Sign(Orient(a, b, c));
            var o2 = Math.Sign(Orient(a, b, d));
            var o3 = Math.Sign(Orient(c, d, a));
            var o4 = Math.Sign(Orient(c, d, b));

            if (o1 != o2 && o3 != o4) return true;
            if (o1 == 0 && OnSegment(a, b, c)) return true;
            if (o2 == 0 && OnSegment(a, b, d)) return true;
            if (o3 == 0 && OnSegment(c, d, a)) return true;
            if (o4 == 0 && OnSegment(c, d, b)) return true;
            return false;
        }

        // p is collinear with a-b; true when it lies within the segment.
        private static bool OnSegment(Vec2 a, Vec2 b, Vec2 p) =>
            Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

        // Points closer than this fraction of the outline size are welded.
        private const float WeldFraction = 1e-4f;

        // Sine of the smallest turn that keeps a vertex (about 0.06°).
        private const double CollinearSine = 1e-3;

        // Each split resolves a reflex vertex or halves a large piece; the depth stays far below this.
        private const int MaxDepth = 256;
    }
}
