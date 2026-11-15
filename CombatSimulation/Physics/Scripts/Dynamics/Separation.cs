using System;

namespace CombatSimulation.Physics
{
    // Whole-body depenetration of one body pair (docs/combat_simulation.md, "Whole-body depenetration").
    // The reference body has the smaller inverse mass; the moving body leaves it along a fixed direction.
    internal struct Separation
    {
        // Lower body index in the high 32 bits, higher index in the low 32 bits; the list is sorted by it.
        public long Key;
        public int Reference;
        public int Moving;

        // Direction in which the moving body leaves, in the reference body's frame, and in the world for this step.
        public Vec2 LocalDirection;
        public Vec2 Normal;

        // Translation of the moving body along Normal that clears the pair, at the start of this step.
        // The depenetration ends at EndClearance; normal contacts handle the rest.
        public float Clearance;
        public float EndClearance;
        public float BestClearance;
        public int StallSteps;
        public bool Retried;

        public float NormalImpulse;
    }

    public sealed partial class PhysicsWorld
    {
        // Separates two overlapping bodies as whole bodies: the solver replaces all their contacts with one push
        // along the shortest way out, until they no longer overlap. For capture, spawn and teleport cases where the
        // caller knows about the overlap. A deep contact also starts it on its own (WorldSettings.SeparationDepth),
        // but only when one of the bodies is compound: a pair of single convex shapes always has one consistent
        // normal, so its contacts cannot wedge it (P1 crowd: drones pressed together by the crowd stay with contacts).
        // Returns false when the bodies do not overlap, or when neither can move.
        public bool Separate(BodyId a, BodyId b)
        {
            var indexA = Resolve(a);
            var indexB = Resolve(b);
            if (indexA == indexB) throw new ArgumentException("A body cannot separate from itself");

            return FindSeparation(PairKey(indexA, indexB)) >= 0 || StartSeparation(indexA, indexB);
        }

        public bool IsSeparating(BodyId a, BodyId b) => FindSeparation(PairKey(Resolve(a), Resolve(b))) >= 0;

        // Body pairs in depenetration now.
        public int SeparationCount => _separationCount;

        // Depenetrations that ended without separating the bodies (no progress for StallSteps steps, twice).
        public int FailedSeparationCount { get; private set; }

        // Deepest overlap of any shape pair of the two bodies. Zero or less means apart.
        public float GetPairOverlap(BodyId a, BodyId b) => PairOverlap(Resolve(a), Resolve(b), Vec2.Zero);

        // Before the solve: continues and ends the running depenetrations, starts new ones from deep contacts and
        // marks the contacts that the solver skips.
        private void UpdateSeparations()
        {
            var write = 0;
            for (var i = 0; i < _separationCount; ++i)
            {
                if (ContinueSeparation(ref _separations[i]))
                    _separations[write++] = _separations[i];
            }

            _separationCount = write;

            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                if (contact.DeepSteps > Settings.SeparationSteps && (IsCompound(contact.IndexA) || IsCompound(contact.IndexB)) &&
                    FindSeparation(PairKey(contact.IndexA, contact.IndexB)) < 0)
                    StartSeparation(contact.IndexA, contact.IndexB);
            }

            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                contact.Suppressed = _separationCount > 0 && FindSeparation(PairKey(contact.IndexA, contact.IndexB)) >= 0;
                if (!contact.Suppressed) continue;

                // No warm start from stale impulses when the contact returns.
                for (var j = 0; j < contact.Manifold.PointCount; ++j)
                {
                    ref var point = ref Manifold.Point(ref contact.Manifold, j);
                    point.NormalImpulse = 0f;
                    point.TangentImpulse = 0f;
                    point.MaxNormalImpulse = 0f;
                }
            }
        }

        private bool StartSeparation(int indexA, int indexB)
        {
            ref var bodyA = ref _bodies[indexA];
            ref var bodyB = ref _bodies[indexB];
            if (bodyA.InvMass == 0f && bodyB.InvMass == 0f) return false;

            // The heavier body is the reference; equal masses: the lower index.
            var aIsReference = bodyA.InvMass < bodyB.InvMass || bodyA.InvMass == bodyB.InvMass && indexA < indexB;
            var separation = new Separation
            {
                Key = PairKey(indexA, indexB),
                Reference = aIsReference ? indexA : indexB,
                Moving = aIsReference ? indexB : indexA,
            };

            // 5% of the smaller body's radius: the P1 resting limit, so a pair pressed together by other bodies ends.
            separation.EndClearance = Math.Max(Settings.LinearSlop, EndFraction * Math.Min(bodyA.Extent, bodyB.Extent));
            if (!ChooseDirection(ref separation)) return false;

            if (_separationCount == _separations.Length) Array.Resize(ref _separations, _separations.Length * 2);
            var at = _separationCount;
            while (at > 0 && _separations[at - 1].Key > separation.Key)
            {
                _separations[at] = _separations[at - 1];
                --at;
            }

            _separations[at] = separation;
            ++_separationCount;
            return true;
        }

        // Tests the candidate directions and keeps the one with the shortest clearance; a tie goes to the lower
        // candidate. False when the overlap is already down to EndClearance.
        private bool ChooseDirection(ref Separation separation)
        {
            ref var reference = ref _bodies[separation.Reference];
            ref var moving = ref _bodies[separation.Moving];
            if (PairOverlap(separation.Reference, separation.Moving, Vec2.Zero) <= separation.EndClearance) return false;

            // The fixed directions turn with the reference body; the last candidate is the line between the centres.
            var count = 0;
            foreach (var direction in Directions)
                _candidates[count++] = reference.Q.Rotate(direction);
            var delta = Torus.Delta(reference.Center, moving.Center);
            if (delta.LengthSquared >= Epsilon * Epsilon) _candidates[count++] = delta.Normalized;

            Clearances(separation.Reference, separation.Moving, count);

            var best = 0;
            for (var i = 1; i < count; ++i)
                if (_clearances[i] < _clearances[best]) best = i;

            separation.Normal = _candidates[best];
            separation.LocalDirection = reference.Q.InvRotate(_candidates[best]);
            separation.Clearance = _clearances[best];
            separation.BestClearance = _clearances[best];
            separation.StallSteps = 0;
            separation.NormalImpulse = 0f;
            return true;
        }

        // False when the depenetration ends: the overlap is down to EndClearance, or it made no progress twice.
        private bool ContinueSeparation(ref Separation separation)
        {
            if (PairOverlap(separation.Reference, separation.Moving, Vec2.Zero) <= separation.EndClearance) return false;

            ref var reference = ref _bodies[separation.Reference];
            var normal = reference.Q.Rotate(separation.LocalDirection);
            _candidates[0] = normal;
            Clearances(separation.Reference, separation.Moving, 1);
            var clearance = _clearances[0];

            separation.Normal = normal;
            separation.Clearance = clearance;
            if (clearance < separation.BestClearance - Settings.LinearSlop)
            {
                separation.BestClearance = clearance;
                separation.StallSteps = 0;
                return true;
            }

            if (++separation.StallSteps <= StallSteps) return true;

            // Blocked (for example by a third body): choose the direction again once, then give up.
            if (!separation.Retried)
            {
                separation.Retried = true;
                return ChooseDirection(ref separation);
            }

            ++FailedSeparationCount;
            for (var i = 0; i < _contactCount; ++i)
                if (PairKey(_contacts[i].IndexA, _contacts[i].IndexB) == separation.Key)
                    _contacts[i].DeepSteps = 0;
            return false;
        }

        // For each candidate direction (unit, world) in _candidates: the shortest translation t >= 0 of the moving
        // body along it after which no shape pair overlaps by more than LinearSlop, into _clearances.
        //
        // Both shapes of a pair are convex, so the pair overlaps in one interval of t. The interval comes from
        // separating axes: the face normals of both shapes and a capsule's own axis (shared by all directions, so
        // projected once per pair), plus the direction and its perpendicular. This is exact for polygons; for round
        // shapes it can be a little too long, never too short, so every t outside it is free. Circle pairs are exact.
        // The clearance is the first t that no interval covers.
        private void Clearances(int reference, int moving, int candidateCount)
        {
            ref var bodyR = ref _bodies[reference];
            ref var bodyM = ref _bodies[moving];
            var slop = Settings.LinearSlop;

            // The moving body in the torus image nearest to the reference body.
            var xfR = bodyR.Pose;
            var xfM = new Pose(bodyR.Origin + Torus.Delta(bodyR.Origin, bodyM.Origin), bodyM.Q);
            var centerM = xfM.TransformPoint(bodyM.LocalCenter);
            CacheMovingShapes(moving, xfM, candidateCount);

            for (var c = 0; c < candidateCount; ++c)
                _intervalCounts[c] = 0;

            Span<Vec2> pointsR = stackalloc Vec2[Polygon.MaxVertices];
            Span<Vec2> axes = stackalloc Vec2[2 * MaxAxesPerShape];
            Span<float> projections = stackalloc float[4 * 2 * MaxAxesPerShape];
            Span<float> directionsR = stackalloc float[4 * CandidateCount];

            for (var s = bodyR.FirstShape; s >= 0; s = _shapes[s].NextInBody)
            {
                ref var shapeR = ref _shapes[s];
                var boundR = xfR.TransformPoint(shapeR.LocalBoundCenter);
                var reached = false;
                for (var c = 0; c < candidateCount && !reached; ++c)
                    reached = RayReaches(centerM, _candidates[c], boundR, shapeR.BoundRadius + bodyM.Extent);
                if (!reached) continue;

                var countR = WorldPoints(shapeR, xfR, pointsR, out var radiusR);
                ProjectOnCandidates(pointsR.Slice(0, countR), radiusR, candidateCount, directionsR);
                var axesR = WorldAxes(shapeR, xfR, axes);

                for (var j = 0; j < _movingShapeCount; ++j)
                {
                    ref var cached = ref _movingShapes[j];
                    var pointsM = new ReadOnlySpan<Vec2>(_movingPoints, cached.First, cached.Count);
                    var circles = shapeR.Kind == ShapeKind.Circle && cached.IsCircle;
                    var projected = false;
                    var axisCount = axesR + cached.AxisCount;

                    for (var c = 0; c < candidateCount; ++c)
                    {
                        var direction = _candidates[c];
                        if (!RayReaches(cached.BoundCenter, direction, boundR, shapeR.BoundRadius + cached.BoundRadius)) continue;

                        float lower, upper;
                        if (circles)
                        {
                            if (!CircleInterval(pointsR[0], radiusR, pointsM[0], cached.Radius, direction, slop, out lower, out upper)) continue;
                        }
                        else
                        {
                            if (!projected)
                            {
                                for (var k = 0; k < cached.AxisCount; ++k)
                                    axes[axesR + k] = _movingAxes[cached.FirstAxis + k];
                                for (var k = 0; k < axisCount; ++k)
                                {
                                    Project(pointsR.Slice(0, countR), radiusR, axes[k], out projections[4 * k], out projections[4 * k + 1]);
                                    Project(pointsM, cached.Radius, axes[k], out projections[4 * k + 2], out projections[4 * k + 3]);
                                }

                                projected = true;
                            }

                            lower = float.MinValue;
                            upper = float.MaxValue;
                            var r = 4 * c;
                            var m = 4 * (j * CandidateCount + c);
                            if (!Narrow(directionsR[r], directionsR[r + 1], _movingDirections[m], _movingDirections[m + 1], 1f, slop, ref lower, ref upper)) continue;
                            if (!Narrow(directionsR[r + 2], directionsR[r + 3], _movingDirections[m + 2], _movingDirections[m + 3], 0f, slop, ref lower, ref upper)) continue;

                            var overlaps = true;
                            for (var k = 0; k < axisCount && overlaps; ++k)
                                overlaps = Narrow(projections[4 * k], projections[4 * k + 1], projections[4 * k + 2], projections[4 * k + 3],
                                    Vec2.Dot(direction, axes[k]), slop, ref lower, ref upper);
                            if (!overlaps) continue;
                        }

                        if (upper > 0f) AddInterval(c, lower, upper);
                    }
                }
            }

            for (var c = 0; c < candidateCount; ++c)
            {
                // The sweep result does not depend on the order of equal keys, so the unstable sort is deterministic.
                var lowerBounds = _intervalLower[c];
                var upperBounds = _intervalUpper[c];
                var count = _intervalCounts[c];
                Array.Sort(lowerBounds, upperBounds, 0, count);

                var clearance = 0f;
                for (var i = 0; i < count && lowerBounds[i] < clearance; ++i)
                    if (upperBounds[i] > clearance) clearance = upperBounds[i];
                _clearances[c] = clearance;
            }
        }

        // World points, rounding radius and separating axes of the moving body's shapes, and their projections on
        // each candidate direction and its perpendicular, for Clearances.
        private void CacheMovingShapes(int moving, in Pose xfM, int candidateCount)
        {
            _movingShapeCount = 0;
            var points = 0;
            var axes = 0;
            for (var t = _bodies[moving].FirstShape; t >= 0; t = _shapes[t].NextInBody)
            {
                ref var shape = ref _shapes[t];
                if (_movingShapeCount == _movingShapes.Length) Array.Resize(ref _movingShapes, _movingShapeCount * 2);
                if (points + Polygon.MaxVertices > _movingPoints.Length) Array.Resize(ref _movingPoints, 2 * (points + Polygon.MaxVertices));
                if (axes + MaxAxesPerShape > _movingAxes.Length) Array.Resize(ref _movingAxes, 2 * (axes + MaxAxesPerShape));
                if (4 * CandidateCount * _movingShapeCount >= _movingDirections.Length) Array.Resize(ref _movingDirections, 2 * _movingDirections.Length);

                ref var cached = ref _movingShapes[_movingShapeCount++];
                cached.First = points;
                cached.Count = WorldPoints(shape, xfM, new Span<Vec2>(_movingPoints, points, Polygon.MaxVertices), out cached.Radius);
                cached.FirstAxis = axes;
                cached.AxisCount = WorldAxes(shape, xfM, new Span<Vec2>(_movingAxes, axes, MaxAxesPerShape));
                cached.IsCircle = shape.Kind == ShapeKind.Circle;
                cached.BoundCenter = xfM.TransformPoint(shape.LocalBoundCenter);
                cached.BoundRadius = shape.BoundRadius;
                ProjectOnCandidates(new ReadOnlySpan<Vec2>(_movingPoints, points, cached.Count), cached.Radius, candidateCount,
                    new Span<float>(_movingDirections, 4 * CandidateCount * (_movingShapeCount - 1), 4 * CandidateCount));
                points += cached.Count;
                axes += cached.AxisCount;
            }
        }

        // Vertex points (a circle: its centre) in the world, and the rounding radius.
        private static int WorldPoints(in Shape shape, in Pose xf, Span<Vec2> points, out float radius)
        {
            if (shape.Kind == ShapeKind.Circle)
            {
                points[0] = xf.TransformPoint(shape.Circle.Center);
                radius = shape.Circle.Radius;
                return 1;
            }

            var polygon = shape.Polygon;
            for (var i = 0; i < polygon.Count; ++i)
                points[i] = xf.TransformPoint(polygon.Vertex(i));
            radius = polygon.Radius;
            return polygon.Count;
        }

        // Face normals in the world, plus a capsule's own axis (it separates past the round ends). A circle has none.
        private static int WorldAxes(in Shape shape, in Pose xf, Span<Vec2> axes)
        {
            if (shape.Kind == ShapeKind.Circle) return 0;

            var polygon = shape.Polygon;
            for (var i = 0; i < polygon.Count; ++i)
                axes[i] = xf.Q.Rotate(polygon.Normal(i));
            if (shape.Kind != ShapeKind.Capsule) return polygon.Count;

            axes[polygon.Count] = xf.Q.Rotate((polygon.Vertex(1) - polygon.Vertex(0)).Normalized);
            return polygon.Count + 1;
        }

        private void AddInterval(int candidate, float lower, float upper)
        {
            var count = _intervalCounts[candidate];
            if (_intervalLower[candidate] == null || count == _intervalLower[candidate].Length)
            {
                var size = Math.Max(64, 2 * count);
                Array.Resize(ref _intervalLower[candidate], size);
                Array.Resize(ref _intervalUpper[candidate], size);
            }

            _intervalLower[candidate][count] = lower;
            _intervalUpper[candidate][count] = upper;
            _intervalCounts[candidate] = count + 1;
        }

        // True when a circle moving from 'from' along 'direction' (t >= 0) comes closer than 'reach' to 'center'.
        private static bool RayReaches(Vec2 from, Vec2 direction, Vec2 center, float reach)
        {
            var w = center - from;
            var along = Math.Max(0f, Vec2.Dot(w, direction));
            return (w - along * direction).LengthSquared < reach * reach;
        }

        // Interval of t in which circle b, moved by t·direction, overlaps circle a by more than 'slop': exact.
        private static bool CircleInterval(Vec2 centerA, float radiusA, Vec2 centerB, float radiusB, Vec2 direction, float slop, out float lower, out float upper)
        {
            lower = upper = 0f;

            // |cB + t·d - cA| < rA + rB - slop.
            var w = centerB - centerA;
            var reach = radiusA + radiusB - slop;
            if (reach <= 0f) return false;
            var half = Vec2.Dot(w, direction);
            var discriminant = half * half - (w.LengthSquared - reach * reach);
            if (discriminant <= 0f) return false;

            var root = (float)Math.Sqrt(discriminant);
            lower = -half - root;
            upper = -half + root;
            return true;
        }

        // Projections on each candidate direction and on its perpendicular: min, max, min, max per candidate.
        private void ProjectOnCandidates(ReadOnlySpan<Vec2> points, float radius, int candidateCount, Span<float> projections)
        {
            for (var c = 0; c < candidateCount; ++c)
            {
                Project(points, radius, _candidates[c], out projections[4 * c], out projections[4 * c + 1]);
                Project(points, radius, Vec2.LeftPerp(_candidates[c]), out projections[4 * c + 2], out projections[4 * c + 3]);
            }
        }

        // Narrows (lower, upper) to the t in which the projections [minA, maxA] and [minB, maxB] + t·k on one axis
        // overlap by more than 'slop'. False when they never do.
        private static bool Narrow(float minA, float maxA, float minB, float maxB, float k, float slop, ref float lower, ref float upper)
        {
            // Overlap: maxA - (minB + t·k) > slop and (maxB + t·k) - minA > slop.
            var high = maxA - minB - slop;
            var low = minA - maxB + slop;
            if (Math.Abs(k) < AxisEpsilon) return low < 0f && high > 0f;

            var t1 = low / k;
            var t2 = high / k;
            if (k < 0f)
            {
                var swap = t1;
                t1 = t2;
                t2 = swap;
            }

            if (t1 > lower) lower = t1;
            if (t2 < upper) upper = t2;
            return lower < upper;
        }

        private static void Project(ReadOnlySpan<Vec2> points, float radius, Vec2 axis, out float min, out float max)
        {
            min = max = Vec2.Dot(points[0], axis);
            for (var i = 1; i < points.Length; ++i)
            {
                var p = Vec2.Dot(points[i], axis);
                if (p < min) min = p;
                else if (p > max) max = p;
            }

            min -= radius;
            max += radius;
        }

        private struct MovingShape
        {
            public int First;
            public int Count;
            public float Radius;
            public int FirstAxis;
            public int AxisCount;
            public bool IsCircle;
            public Vec2 BoundCenter;
            public float BoundRadius;
        }

        // Deepest overlap of the shape pairs of two bodies, with 'offset' added to the position of body b.
        private float PairOverlap(int a, int b, Vec2 offset)
        {
            ref var bodyA = ref _bodies[a];
            ref var bodyB = ref _bodies[b];

            // B in the torus image nearest to A.
            var xfA = bodyA.Pose;
            var xfB = new Pose(bodyA.Origin + Torus.Delta(bodyA.Origin, bodyB.Origin) + offset, bodyB.Q);
            var centerB = xfB.TransformPoint(bodyB.LocalCenter);

            var deepest = 0f;
            for (var s = bodyA.FirstShape; s >= 0; s = _shapes[s].NextInBody)
            {
                ref var shapeA = ref _shapes[s];
                var boundA = xfA.TransformPoint(shapeA.LocalBoundCenter);
                var reachBody = shapeA.BoundRadius + bodyB.Extent;
                if ((centerB - boundA).LengthSquared >= reachBody * reachBody) continue;

                for (var t = bodyB.FirstShape; t >= 0; t = _shapes[t].NextInBody)
                {
                    ref var shapeB = ref _shapes[t];
                    var reach = shapeA.BoundRadius + shapeB.BoundRadius;
                    if ((xfB.TransformPoint(shapeB.LocalBoundCenter) - boundA).LengthSquared >= reach * reach) continue;

                    Shape.Collide(shapeA, xfA, shapeB, xfB, 0f, out var manifold);
                    if (manifold.PointCount > 0) deepest = Math.Max(deepest, -manifold.MinSeparation);
                }
            }

            return deepest;
        }

        private bool IsCompound(int bodyIndex) => _bodies[bodyIndex].FirstShape >= 0 && _shapes[_bodies[bodyIndex].FirstShape].NextInBody >= 0;

        private void RemoveSeparations(int bodyIndex)
        {
            var write = 0;
            for (var i = 0; i < _separationCount; ++i)
                if (_separations[i].Reference != bodyIndex && _separations[i].Moving != bodyIndex)
                    _separations[write++] = _separations[i];
            _separationCount = write;
        }

        private int FindSeparation(long key)
        {
            int lower = 0, upper = _separationCount - 1;
            while (lower <= upper)
            {
                var middle = (lower + upper) >> 1;
                var value = _separations[middle].Key;
                if (value == key) return middle;
                if (value < key) lower = middle + 1;
                else upper = middle - 1;
            }

            return -1;
        }

        private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        // 16 directions, every 22.5°, as literals so that no platform-dependent trigonometry enters the state.
        private static readonly Vec2[] Directions =
        {
            new Vec2(1f, 0f), new Vec2(0.9238795f, 0.38268343f), new Vec2(0.70710677f, 0.70710677f), new Vec2(0.38268343f, 0.9238795f),
            new Vec2(0f, 1f), new Vec2(-0.38268343f, 0.9238795f), new Vec2(-0.70710677f, 0.70710677f), new Vec2(-0.9238795f, 0.38268343f),
            new Vec2(-1f, 0f), new Vec2(-0.9238795f, -0.38268343f), new Vec2(-0.70710677f, -0.70710677f), new Vec2(-0.38268343f, -0.9238795f),
            new Vec2(0f, -1f), new Vec2(0.38268343f, -0.9238795f), new Vec2(0.70710677f, -0.70710677f), new Vec2(0.9238795f, -0.38268343f),
        };

        // Steps without progress before the direction is chosen again (1 s at 50 steps per second).
        private const int StallSteps = 50;
        private const float AxisEpsilon = 1e-6f;
        private const int CandidateCount = 17;

        // Polygon.MaxVertices face normals plus a capsule axis.
        private const int MaxAxesPerShape = Polygon.MaxVertices + 1;
        private const float EndFraction = 0.05f;
        private const float Epsilon = 1e-6f;

        private Separation[] _separations = new Separation[4];
        private int _separationCount;

        // Clearances buffers: candidate directions and their results, intervals per candidate, the moving body's shapes.
        private readonly Vec2[] _candidates = new Vec2[CandidateCount];
        private readonly float[] _clearances = new float[CandidateCount];
        private readonly float[][] _intervalLower = new float[CandidateCount][];
        private readonly float[][] _intervalUpper = new float[CandidateCount][];
        private readonly int[] _intervalCounts = new int[CandidateCount];
        private MovingShape[] _movingShapes = new MovingShape[8];
        private int _movingShapeCount;
        private Vec2[] _movingPoints = new Vec2[64];
        private Vec2[] _movingAxes = new Vec2[64];
        private float[] _movingDirections = new float[4 * CandidateCount * 8];
    }
}
