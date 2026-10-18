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
    //
    // Outline parts: a part of a concave outline knows its cut edges (Polygon.CutEdges). A contact with such a part
    // never takes its normal from a cut, because a body that slides across the seam would catch on it. Allowed
    // normals are the outer face normals and, at a vertex, the directions between the outline normals there.
    // Polygons without cut edges take the plain code path, unchanged.
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

            Vec2 localNormal;
            Vec2 cA;
            float pointSeparation;

            if (a.CutEdges != 0)
            {
                if (!OutlinePartAndCircle(a, center, radius, normalIndex, separation, maxDistance, out localNormal, out cA, out pointSeparation)) return;
            }
            else
            {
                var v1 = a.Vertex(normalIndex);
                var v2 = a.Vertex(normalIndex + 1 < a.Count ? normalIndex + 1 : 0);

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

        // PolygonAndCircle for an outline part. 'normalIndex' and 'separation' are the face of largest separation.
        // False when there is no point.
        // 1. An outer face, centre in its face region: as for any polygon.
        // 2. An outer face, centre in a vertex region: the direction to the centre, clamped into the outline arc of the
        //    vertex. On a straight seam this is the face normal of the neighbour part.
        // 3. A cut, centre outside: no point. The neighbour part holds the whole cut, so its contact is at least as near.
        // 4. A cut, centre inside: the outline axis with the largest separation.
        private static bool OutlinePartAndCircle(Polygon a, Vec2 center, float radius, int normalIndex, float separation, float maxDistance,
            out Vec2 normal, out Vec2 cA, out float pointSeparation)
        {
            normal = default;
            cA = default;
            pointSeparation = 0f;

            if (a.IsCut(normalIndex))
            {
                if (separation > 0f) return false;

                var best = float.MinValue;
                for (var i = 0; i < a.Count; ++i)
                {
                    if (a.IsCut(i)) continue;
                    ConsiderCircleAxis(a, center, a.Normal(i), ref best, ref normal);
                }

                for (var i = 0; i < a.Count; ++i)
                {
                    if (!a.HasOutline(i)) continue;
                    ConsiderCircleAxis(a, center, a.OutlineNormalIn(i), ref best, ref normal);
                    ConsiderCircleAxis(a, center, a.OutlineNormalOut(i), ref best, ref normal);
                }

                // A part with only cuts and no vertex on the outline: the plain face.
                if (best == float.MinValue)
                {
                    best = separation;
                    normal = a.Normal(normalIndex);
                }

                cA = center - best * normal;
                pointSeparation = best - radius;
                return true;
            }

            var next = normalIndex + 1 < a.Count ? normalIndex + 1 : 0;
            var v1 = a.Vertex(normalIndex);
            var v2 = a.Vertex(next);
            int vertex;
            if (separation > 0f && Vec2.Dot(center - v1, v2 - v1) < 0f) vertex = normalIndex;
            else if (separation > 0f && Vec2.Dot(center - v2, v1 - v2) < 0f) vertex = next;
            else
            {
                normal = a.Normal(normalIndex);
                cA = center - separation * normal;
                pointSeparation = separation - radius;
                return true;
            }

            var d = center - a.Vertex(vertex);
            var distance = d.Length;
            normal = distance > Epsilon ? d * (1f / distance) : a.Normal(normalIndex);
            if (a.HasOutline(vertex)) normal = a.ClampToOutlineArc(vertex, normal);

            // Along a clamped normal the support of the polygon can be another vertex.
            var s = Vec2.Dot(normal, center) - Support(a, normal);
            pointSeparation = s - radius;
            if (pointSeparation > maxDistance) return false;
            cA = center - s * normal;
            return true;
        }

        private static void ConsiderCircleAxis(Polygon a, Vec2 center, Vec2 axis, ref float best, ref Vec2 normal)
        {
            var s = Vec2.Dot(axis, center) - Support(a, axis);
            if (s <= best) return;
            best = s;
            normal = axis;
        }

        private static float Support(Polygon a, Vec2 direction)
        {
            var max = float.MinValue;
            for (var i = 0; i < a.Count; ++i)
                max = Math.Max(max, Vec2.Dot(direction, a.Vertex(i)));
            return max;
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

            var polyA = new LocalPolygon(stackalloc Vec2[Polygon.MaxVertices], stackalloc Vec2[Polygon.MaxVertices], a, Rot.Identity);
            for (var i = 0; i < a.Count; ++i)
            {
                polyA.Vertices[i] = a.Vertex(i) - origin;
                polyA.Normals[i] = a.Normal(i);
            }

            var polyB = new LocalPolygon(stackalloc Vec2[Polygon.MaxVertices], stackalloc Vec2[Polygon.MaxVertices], b, q);
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
            if ((a.CutEdges | b.CutEdges) != 0)
            {
                OutlinePolygons(polyA, polyB, edgeA, edgeB, flip, flip ? separationB : separationA, maxDistance, ref manifold);
            }
            else
            {
                if (flip) edgeA = FindIncidentEdge(polyB.Normals[edgeB], polyA);
                else edgeB = FindIncidentEdge(polyA.Normals[edgeA], polyB);

                var apart = (flip ? separationB : separationA) > RelativeTolerance;
                var overlaps = flip
                    ? IncidentOverlapsReference(polyB, edgeB, polyA, edgeA)
                    : IncidentOverlapsReference(polyA, edgeA, polyB, edgeB);

                if (!apart || overlaps || !ClosestFeatures(polyA, polyB, edgeA, edgeB, maxDistance, ref manifold))
                    ClipPolygons(polyA, polyB, edgeA, edgeB, flip, apart ? maxDistance : 0f, maxDistance, ref manifold);
            }

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

        // Polygons when either is an outline part. The reference face of the separating axis test decides:
        // 1. Its normal is an outline direction of both polygons: the plain manifold. When that manifold is a closest-
        //    features point whose normal is not an outline direction, the best outline axis replaces it.
        // 2. Otherwise a cut faces the other polygon. Overlapping: the best outline axis. Apart: one point between the
        //    true closest points, when their direction is an outline direction of both polygons; else no points (the
        //    neighbour part across the cut is at least as near). An outline axis is no speculative normal here: when
        //    the other polygon lies beyond the cut, its gap along a sideways outer face is small, and a point on it
        //    stops a fast body sideways (P2-3: f6s1 into the mothership at 140 u/s spun and sank in 154%).
        private static void OutlinePolygons(in LocalPolygon polyA, in LocalPolygon polyB, int edgeA, int edgeB, bool flip, float separation,
            float maxDistance, ref Manifold manifold)
        {
            var apart = separation > RelativeTolerance;
            var normal = flip ? -polyB.Normals[edgeB] : polyA.Normals[edgeA];

            if (IsOutlineDirection(polyA, normal) && IsOutlineDirection(polyB, -normal))
            {
                if (flip) edgeA = FindIncidentEdge(polyB.Normals[edgeB], polyA);
                else edgeB = FindIncidentEdge(polyA.Normals[edgeA], polyB);

                var overlaps = flip
                    ? IncidentOverlapsReference(polyB, edgeB, polyA, edgeA)
                    : IncidentOverlapsReference(polyA, edgeA, polyB, edgeB);

                if (!apart || overlaps || !ClosestFeatures(polyA, polyB, edgeA, edgeB, maxDistance, ref manifold))
                {
                    ClipPolygons(polyA, polyB, edgeA, edgeB, flip, apart ? maxDistance : 0f, maxDistance, ref manifold);
                    return;
                }

                if (manifold.PointCount == 0 || IsOutlineDirection(polyA, manifold.Normal) && IsOutlineDirection(polyB, -manifold.Normal)) return;
                manifold = default;
            }
            else if (apart)
            {
                ClosestPointsOnOutline(polyA, polyB, maxDistance, ref manifold);
                return;
            }

            // Axes that are outline directions of both polygons first; when there is none (two outline parts that
            // meet at cuts), the outline axes of each polygon alone.
            var axis = new OutlineAxis { Separation = float.MinValue };
            ConsiderOutlineAxes(polyA, polyB, false, true, ref axis);
            ConsiderOutlineAxes(polyB, polyA, true, true, ref axis);
            if (axis.Separation == float.MinValue)
            {
                ConsiderOutlineAxes(polyA, polyB, false, false, ref axis);
                ConsiderOutlineAxes(polyB, polyA, true, false, ref axis);
            }

            OutlineAxisManifold(polyA, polyB, axis, maxDistance, ref manifold);
        }

        // One point between the closest points of two polygons that are apart, when the direction between them is an
        // outline direction of both. For polygons apart, the closest pair is a vertex of one and an edge of the other.
        private static void ClosestPointsOnOutline(in LocalPolygon polyA, in LocalPolygon polyB, float maxDistance, ref Manifold manifold)
        {
            var best = float.MaxValue;
            Vec2 pointA = default, pointB = default;
            var featureA = 0;
            var featureB = 0;
            for (var i = 0; i < polyA.Count; ++i)
            for (var j = 0; j < polyB.Count; ++j)
            {
                var vertexA = polyA.Vertices[i];
                var onB = ClosestOnSegment(vertexA, polyB.Vertices[j], polyB.Vertices[polyB.Next(j)]);
                var distanceSquared = Vec2.DistanceSquared(vertexA, onB);
                if (distanceSquared < best)
                {
                    best = distanceSquared;
                    pointA = vertexA;
                    pointB = onB;
                    featureA = i;
                    featureB = j;
                }

                var vertexB = polyB.Vertices[j];
                var onA = ClosestOnSegment(vertexB, polyA.Vertices[i], polyA.Vertices[polyA.Next(i)]);
                distanceSquared = Vec2.DistanceSquared(onA, vertexB);
                if (distanceSquared < best)
                {
                    best = distanceSquared;
                    pointA = onA;
                    pointB = vertexB;
                    featureA = i;
                    featureB = j;
                }
            }

            var distance = (float)Math.Sqrt(best);
            if (distance < Epsilon) return;

            var separation = distance - polyA.Radius - polyB.Radius;
            if (separation > maxDistance) return;

            var normal = (pointB - pointA) * (1f / distance);
            if (!IsOutlineDirection(polyA, normal) || !IsOutlineDirection(polyB, -normal)) return;

            var surfaceA = pointA + polyA.Radius * normal;
            var surfaceB = pointB - polyB.Radius * normal;
            manifold.Normal = normal;
            AddPoint(ref manifold, Vec2.Lerp(surfaceA, surfaceB, 0.5f), separation, Manifold.MakeId(featureA, featureB, ClosestKind), maxDistance);
        }

        private static Vec2 ClosestOnSegment(Vec2 point, Vec2 a, Vec2 b)
        {
            var e = b - a;
            var t = Vec2.Dot(point - a, e);
            if (t <= 0f) return a;
            var ee = Vec2.Dot(e, e);
            return t >= ee ? b : a + (t / ee) * e;
        }

        // An axis for OutlinePolygons. Normal points from A to B. Feature: the face index, or 2 × vertex + 0 for the
        // outline normal before the vertex and + 1 for the one after it.
        private struct OutlineAxis
        {
            public Vec2 Normal;
            public float Separation;
            public bool FromB;
            public bool Face;
            public int Feature;
        }

        // The outer faces and the outline vertex normals of 'own', each kept when it gives a larger separation and, if
        // 'checkOther', is an outline direction of 'other' too. The separation uses the support of both polygons.
        private static void ConsiderOutlineAxes(in LocalPolygon own, in LocalPolygon other, bool fromB, bool checkOther, ref OutlineAxis axis)
        {
            var source = own.Source;
            for (var i = 0; i < own.Count; ++i)
                if (!source.IsCut(i))
                    ConsiderOutlineAxis(own, other, own.Normals[i], fromB, true, i, checkOther, ref axis);

            if (source.CutEdges == 0) return;

            for (var i = 0; i < own.Count; ++i)
            {
                if (!source.HasOutline(i)) continue;
                ConsiderOutlineAxis(own, other, own.Q.Rotate(source.OutlineNormalIn(i)), fromB, false, 2 * i, checkOther, ref axis);
                ConsiderOutlineAxis(own, other, own.Q.Rotate(source.OutlineNormalOut(i)), fromB, false, 2 * i + 1, checkOther, ref axis);
            }
        }

        // 'direction' points out of 'own'.
        private static void ConsiderOutlineAxis(in LocalPolygon own, in LocalPolygon other, Vec2 direction, bool fromB, bool face, int feature,
            bool checkOther, ref OutlineAxis axis)
        {
            if (checkOther && !IsOutlineDirection(other, -direction)) return;

            var otherMin = float.MaxValue;
            for (var j = 0; j < other.Count; ++j)
                otherMin = Math.Min(otherMin, Vec2.Dot(direction, other.Vertices[j]));
            var ownMax = float.MinValue;
            for (var i = 0; i < own.Count; ++i)
                ownMax = Math.Max(ownMax, Vec2.Dot(direction, own.Vertices[i]));

            var separation = otherMin - ownMax - own.Radius - other.Radius;
            if (separation <= axis.Separation) return;

            axis.Normal = fromB ? -direction : direction;
            axis.Separation = separation;
            axis.FromB = fromB;
            axis.Face = face;
            axis.Feature = feature;
        }

        // A face axis clips like a reference face when the shapes overlap or the incident edge lies along the face.
        // Otherwise one point at the deepest vertex of the other polygon along the axis.
        private static void OutlineAxisManifold(in LocalPolygon polyA, in LocalPolygon polyB, in OutlineAxis axis, float maxDistance, ref Manifold manifold)
        {
            if (axis.Separation > maxDistance) return;

            var apart = axis.Separation > RelativeTolerance;
            if (axis.Face)
            {
                int edgeA, edgeB;
                bool overlaps;
                if (axis.FromB)
                {
                    edgeB = axis.Feature;
                    edgeA = FindIncidentEdge(polyB.Normals[edgeB], polyA);
                    overlaps = IncidentOverlapsReference(polyB, edgeB, polyA, edgeA);
                }
                else
                {
                    edgeA = axis.Feature;
                    edgeB = FindIncidentEdge(polyA.Normals[edgeA], polyB);
                    overlaps = IncidentOverlapsReference(polyA, edgeA, polyB, edgeB);
                }

                if (!apart || overlaps)
                {
                    ClipPolygons(polyA, polyB, edgeA, edgeB, axis.FromB, apart ? maxDistance : 0f, maxDistance, ref manifold);
                    return;
                }
            }

            var normal = axis.Normal;
            var kind = AxisKind + (axis.FromB ? 1 : 0) + (axis.Face ? 0 : 2);
            Vec2 point;
            ushort id;
            if (axis.FromB)
            {
                var i = SupportIndex(polyA, normal);
                point = polyA.Vertices[i] + (polyA.Radius + 0.5f * axis.Separation) * normal;
                id = Manifold.MakeId(i, axis.Feature, kind);
            }
            else
            {
                var j = SupportIndex(polyB, -normal);
                point = polyB.Vertices[j] - (polyB.Radius + 0.5f * axis.Separation) * normal;
                id = Manifold.MakeId(axis.Feature, j, kind);
            }

            manifold.Normal = normal;
            AddPoint(ref manifold, point, axis.Separation, id, maxDistance);
        }

        // True when 'direction' (unit, pointing out of the polygon) is an outline direction at the support of the
        // polygon: the normal of an outer face, or a direction in the outline arc of the support vertex. Always true
        // for a polygon without cut edges.
        private static bool IsOutlineDirection(in LocalPolygon poly, Vec2 direction)
        {
            var source = poly.Source;
            if (source.CutEdges == 0) return true;

            for (var i = 0; i < poly.Count; ++i)
                if (Vec2.Dot(direction, poly.Normals[i]) >= 1f - Polygon.ArcTolerance)
                    return !source.IsCut(i);

            var vertex = SupportIndex(poly, direction);
            return source.HasOutline(vertex) && source.InOutlineArc(vertex, poly.Q.InvRotate(direction));
        }

        private static int SupportIndex(in LocalPolygon poly, Vec2 direction)
        {
            var index = 0;
            var max = Vec2.Dot(direction, poly.Vertices[0]);
            for (var i = 1; i < poly.Count; ++i)
            {
                var value = Vec2.Dot(direction, poly.Vertices[i]);
                if (value > max)
                {
                    max = value;
                    index = i;
                }
            }

            return index;
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

        // Polygon in the local frame of one collide call, on the stack. Q rotates the source polygon's local frame
        // into this frame.
        private readonly ref struct LocalPolygon
        {
            public readonly Span<Vec2> Vertices;
            public readonly Span<Vec2> Normals;
            public readonly int Count;
            public readonly float Radius;
            public readonly Polygon Source;
            public readonly Rot Q;

            public LocalPolygon(Span<Vec2> vertices, Span<Vec2> normals, Polygon source, Rot q)
            {
                Vertices = vertices;
                Normals = normals;
                Count = source.Count;
                Radius = source.Radius;
                Source = source;
                Q = q;
            }

            public int Next(int index) => index + 1 < Count ? index + 1 : 0;
        }

        // Feature kind of a closest-features point, so its id differs from the clipped points of the same vertices.
        private const int ClosestKind = 1;

        // Feature kinds 2-5 of a single point on an outline axis: face of A, face of B, vertex normal of A, of B.
        private const int AxisKind = 2;
        private const float FloatEpsilon = 1.1920929e-7f;
        private const float Epsilon = 1e-6f;

        // Prefer shape A as the reference shape unless B is clearly better (Box2D: 0.1 × linear slop).
        private const float RelativeTolerance = 0.0005f;
    }
}
