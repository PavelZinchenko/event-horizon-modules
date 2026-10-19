using System;

namespace CombatSimulation.Physics
{
    // Swept bullets (P3, step 5 owner decision 5). After the solver has moved the bodies, each bullet is cast along its
    // motion relative to each candidate target:
    //
    // - against a body: the body keeps its start rotation and moves its centre on a line from the start to the end of
    //   the step (owner decision 3). A hull tip that turns through the bullet within the step is missed; the bullet is
    //   then inside the hull at the next step and hits it at fraction 0 there;
    // - against a bullet: both move on their lines.
    //
    // An attached bullet moves on the line from its pose on the parent's start pose to its pose on the parent's end pose,
    // with the parent's start rotation, and never hits its parent.
    //
    // Targets are de-duplicated per unit: the shapes of one body with the same filter categories (the parts of one
    // hull) are one target, a shield shape on the same body is another (contact ordering rule 4, investigation 1.5).
    public sealed partial class PhysicsWorld
    {
        // Bullet ids in the sweep and query grids; shape ids stay below it.
        private const int BulletFlag = 1 << 30;

        // Cast tolerance: the largest gap left at a reported hit, plus the float resolution of the coordinates in the
        // cast frame. Far below the P3 hit accuracy (point within 1% of a 0.2 radius). A tolerance relative to the
        // bullet size let a large bullet graze a hull within the tolerance and report that graze as its hit, early.
        private const float CastTolerance = 1e-5f;
        private const float CastResolution = 1e-7f;

        private static float Tolerance(Vec2 offset, Vec2 translation) =>
            CastTolerance + CastResolution * (Math.Abs(offset.X) + Math.Abs(offset.Y) + Math.Abs(translation.X) + Math.Abs(translation.Y));

        private void SaveStartPoses()
        {
            if (_aliveBulletCount == 0) return;

            if (_startPoses.Length < _bodyCount)
            {
                var size = Math.Max(_bodyCount, _startPoses.Length * 2);
                Array.Resize(ref _startPoses, size);
                Array.Resize(ref _startCenters, size);
                Array.Resize(ref _bodyTranslations, size);
            }

            for (var i = 0; i < _bodyCount; ++i)
            {
                ref var body = ref _bodies[i];
                if (!body.Alive) continue;
                _startPoses[i] = body.Pose;
                _startCenters[i] = body.Center;
            }
        }

        private void SweepBullets(float dt)
        {
            _hitCount = 0;
            if (_aliveBulletCount == 0)
            {
                _historyCount = 0;
                return;
            }

            var grid = _sweepGrid ?? (_sweepGrid = new TorusGrid(Torus, Settings.CellSize));
            grid.Clear();

            for (var i = 0; i < _bodyCount; ++i)
                if (_bodies[i].Alive)
                    _bodyTranslations[i] = Torus.Delta(_startCenters[i], _bodies[i].Center);

            if (_attachedBulletCount > 0) PrepareAttachedSweep(dt);

            // Body shapes are 'fixed' in this grid, so two shapes never pair: only pairs with a bullet are reported.
            for (var s = 0; s < _shapeCount; ++s)
            {
                ref var shape = ref _shapes[s];
                if (!shape.Alive) continue;
                var aabb = SweptAabb(shape.ComputeAabb(_startPoses[shape.Body]), _bodyTranslations[shape.Body]);
                grid.Add(s, aabb, shape.Body, true, shape.Filter);
            }

            // The owner is the bullet's group, so it never pairs with its own ship or with bullets of the same ship.
            for (var b = 0; b < _bulletCount; ++b)
            {
                ref var bullet = ref _bullets[b];
                if (!bullet.Alive) continue;
                var group = IsAlive(bullet.Owner) ? bullet.Owner.Index : -2 - b;
                var aabb = SweptAabb(bullet.Shape.ComputeAabb(bullet.Pose), dt * bullet.V);
                grid.Add(BulletFlag | b, aabb, group, false, bullet.Shape.Filter);
            }

            var pairCount = grid.FindPairs(ref _sweepPairs);
            Array.Sort(_sweepPairs, 0, pairCount);

            _hitCandidateCount = 0;
            for (var p = 0; p < pairCount; ++p)
            {
                var low = (int)(_sweepPairs[p] >> 32);
                var high = (int)(_sweepPairs[p] & 0xffffffff);
                if ((low & BulletFlag) != 0)
                    CastBulletPair(low & ~BulletFlag, high & ~BulletFlag, dt);
                else
                    CastBulletAtShape(high & ~BulletFlag, low, dt);
            }

            SelectHits();

            for (var b = 0; b < _bulletCount; ++b)
            {
                ref var bullet = ref _bullets[b];
                if (!bullet.Alive) continue;
                if (bullet.Parent.IsNull) bullet.Position = Torus.Wrap(bullet.Position + dt * bullet.V);
                else PlaceAttached(ref bullet);
            }
        }

        // Attached bullets start the sweep at their pose on the parent's start pose, with the velocity of that pose's
        // motion in the step. A bullet whose parent is gone comes free with its last pose and velocity.
        private void PrepareAttachedSweep(float dt)
        {
            for (var b = 0; b < _bulletCount; ++b)
            {
                ref var bullet = ref _bullets[b];
                if (!bullet.Alive || bullet.Parent.IsNull) continue;

                if (!IsAlive(bullet.Parent))
                {
                    bullet.Parent = default;
                    --_attachedBulletCount;
                    continue;
                }

                var start = _startPoses[bullet.Parent.Index];
                var startPoint = start.TransformPoint(bullet.Local.P);
                var endPoint = _bodies[bullet.Parent.Index].Pose.TransformPoint(bullet.Local.P);
                bullet.Position = Torus.Wrap(startPoint);
                bullet.Q = Rot.Mul(start.Q, bullet.Local.Q);
                bullet.V = Torus.Delta(startPoint, endPoint) * (1f / dt);
            }
        }

        private void CastBulletAtShape(int b, int s, float dt)
        {
            ref var bullet = ref _bullets[b];
            ref var shape = ref _shapes[s];
            var bodyIndex = shape.Body;
            if (!bullet.Parent.IsNull && bullet.Parent.Index == bodyIndex) return;
            var start = _startPoses[bodyIndex];
            var bodyMove = _bodyTranslations[bodyIndex];
            var bulletMove = dt * bullet.V;

            // Frame: the body origin at its start pose is at zero; the bullet in the torus image nearest to it.
            var offset = 0.5f * bodyMove + Torus.Delta(start.P + 0.5f * bodyMove, bullet.Position + 0.5f * bulletMove) - 0.5f * bulletMove;
            var xfTarget = new Pose(Vec2.Zero, start.Q);
            var xfBullet = new Pose(offset, bullet.Q);
            var translation = bulletMove - bodyMove;

            var state = ShapeCast.Cast(DistanceProxy.FromShape(shape), xfTarget, DistanceProxy.FromShape(bullet.Shape), xfBullet,
                translation, Tolerance(offset, translation), out var fraction, out var point, out var normal);
            if (state == ShapeCastState.Miss) return;
            SurfaceContact(shape, xfTarget, bullet.Shape, new Pose(offset + fraction * translation, bullet.Q), state, translation, ref point, ref normal);

            AddCandidate(new HitCandidate
            {
                Bullet = b,
                Shape = s,
                TargetBullet = -1,
                Unit = UnitOf(s),
                TargetOrder = bodyIndex,
                Fraction = fraction,
                Point = Torus.Wrap(start.P + point + fraction * bodyMove),
                Normal = normal,
            });
        }

        // One cast for the pair; each bullet gets its own hit on the other.
        private void CastBulletPair(int i, int j, float dt)
        {
            ref var a = ref _bullets[i];
            ref var b = ref _bullets[j];
            var moveA = dt * a.V;
            var moveB = dt * b.V;

            var offset = 0.5f * moveA + Torus.Delta(a.Position + 0.5f * moveA, b.Position + 0.5f * moveB) - 0.5f * moveB;
            var xfA = new Pose(Vec2.Zero, a.Q);
            var xfB = new Pose(offset, b.Q);
            var translation = moveB - moveA;

            var state = ShapeCast.Cast(DistanceProxy.FromShape(a.Shape), xfA, DistanceProxy.FromShape(b.Shape), xfB,
                translation, Tolerance(offset, translation), out var fraction, out var point, out var normal);
            if (state == ShapeCastState.Miss) return;
            SurfaceContact(a.Shape, xfA, b.Shape, new Pose(offset + fraction * translation, b.Q), state, translation, ref point, ref normal);

            // The surfaces touch at the contact, so both hits share the point.
            var world = Torus.Wrap(a.Position + point + fraction * moveA);
            AddCandidate(new HitCandidate
            {
                Bullet = j, Shape = -1, TargetBullet = i, Unit = BulletFlag | i, TargetOrder = BulletFlag | i,
                Fraction = fraction, Point = world, Normal = normal,
            });
            AddCandidate(new HitCandidate
            {
                Bullet = i, Shape = -1, TargetBullet = j, Unit = BulletFlag | j, TargetOrder = BulletFlag | j,
                Fraction = fraction, Point = world, Normal = -normal,
            });
        }

        // A hit keeps the cast's point and normal. Shapes that overlap at the start have neither: the manifold gives the
        // normal (never from a cut edge, Collide "Outline parts") and its deepest point, moved onto the target's surface;
        // without manifold points, the direction between the centres.
        private void SurfaceContact(in Shape target, in Pose xfTarget, in Shape other, in Pose xfOther, ShapeCastState state, Vec2 translation,
            ref Vec2 point, ref Vec2 normal)
        {
            if (state != ShapeCastState.Overlapped) return;

            Shape.Collide(target, xfTarget, other, xfOther, Settings.LinearSlop, out var manifold);
            if (manifold.PointCount > 0)
            {
                var deepest = manifold.PointCount == 2 && manifold.Point1.Separation < manifold.Point0.Separation ? manifold.Point1 : manifold.Point0;
                normal = manifold.Normal;
                point = deepest.Point - 0.5f * deepest.Separation * normal;
                return;
            }

            var otherCenter = xfOther.TransformPoint(other.LocalBoundCenter);
            normal = (otherCenter - xfTarget.TransformPoint(target.LocalBoundCenter)).Normalized;
            if (normal.LengthSquared == 0f) normal = -translation.Normalized;
            point = otherCenter;
        }

        // The first shape of the body with the same filter categories stands for the whole unit.
        private int UnitOf(int shapeIndex)
        {
            var categories = _shapes[shapeIndex].Filter.Categories;
            for (var s = _bodies[_shapes[shapeIndex].Body].FirstShape; s >= 0; s = _shapes[s].NextInBody)
                if (_shapes[s].Filter.Categories == categories)
                    return s;
            return shapeIndex;
        }

        // Per bullet: the first hit, or (pass-through) the first hit on each unit. Then contact order, the Begin flags
        // and the history for the next step.
        private void SelectHits()
        {
            var count = _hitCandidateCount;
            for (var i = 0; i < count; ++i)
                _hitCandidateKeys[i] = ((long)_hitCandidates[i].Bullet << 32) | (uint)FractionKey(_hitCandidates[i].Fraction);
            Array.Sort(_hitCandidateKeys, _hitCandidates, 0, count);
            SortTies(_hitCandidateKeys, _hitCandidates, count);

            var write = 0;
            for (var i = 0; i < count;)
            {
                var bullet = _hitCandidates[i].Bullet;
                var end = i;
                while (end < count && _hitCandidates[end].Bullet == bullet) ++end;

                if (!_bullets[bullet].PassThrough)
                {
                    _hitCandidates[write++] = _hitCandidates[i];
                }
                else
                {
                    var runStart = write;
                    for (var k = i; k < end; ++k)
                    {
                        var seen = false;
                        for (var w = runStart; w < write && !seen; ++w) seen = _hitCandidates[w].Unit == _hitCandidates[k].Unit;
                        if (!seen) _hitCandidates[write++] = _hitCandidates[k];
                    }
                }

                i = end;
            }

            // Contact order: fraction, bullet, target, shape.
            for (var i = 0; i < write; ++i)
                _hitCandidateKeys[i] = ((long)FractionKey(_hitCandidates[i].Fraction) << 32) | (uint)_hitCandidates[i].Bullet;
            Array.Sort(_hitCandidateKeys, _hitCandidates, 0, write);
            SortTies(_hitCandidateKeys, _hitCandidates, write);

            if (_hits.Length < write) _hits = new BulletHit[Math.Max(write, _hits.Length * 2)];
            if (_nextHistoryKeys.Length < write)
            {
                var size = Math.Max(write, _nextHistoryKeys.Length * 2);
                _nextHistoryKeys = new long[size];
                _nextHistoryStamps = new HistoryStamp[size];
            }

            for (var i = 0; i < write; ++i)
            {
                ref var c = ref _hitCandidates[i];
                var bulletGeneration = _bullets[c.Bullet].Generation;
                var targetGeneration = c.TargetBullet >= 0 ? _bullets[c.TargetBullet].Generation : _bodies[_shapes[c.Shape].Body].Generation;
                var key = ((long)c.Bullet << 32) | (uint)c.Unit;
                var stamp = new HistoryStamp { BulletGeneration = bulletGeneration, TargetGeneration = targetGeneration };

                var old = FindKey(_historyKeys, _historyCount, key);
                var begin = old < 0 || !_historyStamps[old].Equals(stamp);

                var target = c.TargetBullet >= 0
                    ? new HitTarget(new BulletId(c.TargetBullet, targetGeneration))
                    : new HitTarget(new BodyId(_shapes[c.Shape].Body, targetGeneration), new ShapeId(c.Shape, _shapes[c.Shape].Generation));
                _hits[i] = new BulletHit(new BulletId(c.Bullet, bulletGeneration), target, c.Fraction, c.Point, c.Normal, begin);

                _nextHistoryKeys[i] = key;
                _nextHistoryStamps[i] = stamp;
            }

            _hitCount = write;
            Array.Sort(_nextHistoryKeys, _nextHistoryStamps, 0, write);
            (_historyKeys, _nextHistoryKeys) = (_nextHistoryKeys, _historyKeys);
            (_historyStamps, _nextHistoryStamps) = (_nextHistoryStamps, _historyStamps);
            _historyCount = write;
        }

        private void AddCandidate(in HitCandidate candidate)
        {
            if (_hitCandidateCount == _hitCandidates.Length)
            {
                Array.Resize(ref _hitCandidates, _hitCandidates.Length * 2);
                Array.Resize(ref _hitCandidateKeys, _hitCandidates.Length);
            }

            _hitCandidates[_hitCandidateCount++] = candidate;
        }

        // Equal keys (same fraction and bullet) are ordered by target, then shape. Runs are short, so insertion sort.
        private static void SortTies(long[] keys, HitCandidate[] items, int count)
        {
            for (var i = 1; i < count; ++i)
            {
                if (keys[i] != keys[i - 1]) continue;

                var item = items[i];
                var j = i - 1;
                while (j >= 0 && keys[j] == keys[i] && TieBefore(item, items[j]))
                {
                    items[j + 1] = items[j];
                    --j;
                }

                items[j + 1] = item;
            }
        }

        private static bool TieBefore(in HitCandidate a, in HitCandidate b) =>
            a.TargetOrder != b.TargetOrder ? a.TargetOrder < b.TargetOrder : a.Shape < b.Shape;

        // Fractions lie in [0, 1], so their bits sort as the values do.
        private static int FractionKey(float fraction) => BitConverter.SingleToInt32Bits(fraction < 0f ? 0f : fraction);

        private static int FindKey(long[] keys, int count, long key)
        {
            int low = 0, high = count - 1;
            while (low <= high)
            {
                var middle = (low + high) >> 1;
                if (keys[middle] < key) low = middle + 1;
                else if (keys[middle] > key) high = middle - 1;
                else return middle;
            }

            return -1;
        }

        private static Aabb SweptAabb(in Aabb aabb, Vec2 move) =>
            new Aabb(Vec2.Min(aabb.Lower, aabb.Lower + move), Vec2.Max(aabb.Upper, aabb.Upper + move));

        private struct HitCandidate
        {
            public int Bullet;

            // Target: a shape (TargetBullet -1) or a bullet (Shape -1).
            public int Shape;
            public int TargetBullet;

            // De-duplication unit and tie order: the unit's first shape or BulletFlag | bullet; the body index or
            // BulletFlag | bullet, so bodies come before bullets.
            public int Unit;
            public int TargetOrder;

            public float Fraction;
            public Vec2 Point;
            public Vec2 Normal;
        }

        private struct HistoryStamp : IEquatable<HistoryStamp>
        {
            public int BulletGeneration;
            public int TargetGeneration;

            public bool Equals(HistoryStamp other) => BulletGeneration == other.BulletGeneration && TargetGeneration == other.TargetGeneration;
        }

        private TorusGrid _sweepGrid;
        private long[] _sweepPairs = new long[256];
        private Pose[] _startPoses = new Pose[64];
        private Vec2[] _startCenters = new Vec2[64];
        private Vec2[] _bodyTranslations = new Vec2[64];

        private HitCandidate[] _hitCandidates = new HitCandidate[64];
        private long[] _hitCandidateKeys = new long[64];
        private int _hitCandidateCount;

        private BulletHit[] _hits = new BulletHit[64];
        private int _hitCount;

        private long[] _historyKeys = new long[64];
        private HistoryStamp[] _historyStamps = new HistoryStamp[64];
        private long[] _nextHistoryKeys = new long[64];
        private HistoryStamp[] _nextHistoryStamps = new HistoryStamp[64];
        private int _historyCount;
    }
}
