using System;

namespace CombatSimulation.Physics
{
    public readonly struct RayHit
    {
        public readonly HitTarget Target;

        // Distance along the ray as a fraction of its (clipped) translation. 0 when the ray starts inside the target.
        public readonly float Fraction;

        // Entry point (canonical torus coordinates) and the surface normal there. A ray that starts inside the
        // target has its origin as the point and the reverse ray direction as the normal.
        public readonly Vec2 Point;
        public readonly Vec2 Normal;

        public RayHit(HitTarget target, float fraction, Vec2 point, Vec2 normal)
        {
            Target = target;
            Fraction = fraction;
            Point = point;
            Normal = normal;
        }
    }

    public readonly struct OverlapHit
    {
        public readonly HitTarget Target;

        // Distance from the circle centre to the target's surface; 0 when the centre is inside the target.
        public readonly float Distance;

        public OverlapHit(HitTarget target, float distance)
        {
            Target = target;
            Distance = distance;
        }
    }

    // Ray casts and circle overlaps between steps (P3, owner decisions 4 and 5). Both see the current poses of the
    // bodies and the bullets. They test the convex parts of a hull, not a bounding circle, and report each target unit
    // once (BulletSweep): the parts of a hull are one target, so a ray never hits an inner edge between parts.
    // They allocate nothing once their buffers have grown.
    public sealed partial class PhysicsWorld
    {
        // Gap left at a ray hit, relative to the ray length (P3 ray accuracy: 1e-4 of the length).
        private const float RayTolerance = 1e-6f;

        public Filter GetShapeFilter(ShapeId id) => _shapes[ResolveShape(id)].Filter;

        // Shield on and off, piercing and similar rules. Contacts follow at the next step.
        public void SetShapeFilter(ShapeId id, in Filter filter)
        {
            _shapes[ResolveShape(id)].Filter = filter;
            _queryDirty = true;
        }

        public BodyId GetShapeBody(ShapeId id) => GetBodyId(_shapes[ResolveShape(id)].Body);

        public bool IsAlive(ShapeId id) =>
            id.Index >= 0 && id.Index < _shapeCount && _shapes[id.Index].Alive && _shapes[id.Index].Generation == id.Generation;

        // Hits along origin → origin + translation, nearest first. The ray is clipped to min(Width, Height), so it
        // never reaches its start again across the wrap. Each target is hit once, at its nearest torus image. The ray
        // never hits 'ignore' (the shooter), also not through the wrap.
        //
        // Without pass-through the ray returns its first hit only. With pass-through it returns up to hits.Length
        // hits (8 in the game); when there are more, it keeps the nearest and sets 'overflow'.
        public int RayCast(Vec2 origin, Vec2 translation, in Filter filter, BodyId ignore, bool passThrough, Span<RayHit> hits, out bool overflow)
        {
            overflow = false;
            if (hits.Length == 0) return 0;

            var length = translation.Length;
            if (!(length > 0f)) return 0;

            var maxLength = Math.Min(Torus.Width, Torus.Height);
            if (length > maxLength)
            {
                translation = translation * (maxLength / length);
                length = maxLength;
            }

            EnsureQueryGrid();
            origin = Torus.Wrap(origin);
            ++_queryStamp;
            _queryHitCount = 0;

            var ignoreIndex = IsAlive(ignore) ? ignore.Index : -1;
            var tolerance = CastTolerance + RayTolerance * length;
            var grid = _queryGrid;

            // Grid walk (Amanatides–Woo) in unwrapped cell coordinates; CellIndex wraps them.
            var ix = (int)Math.Floor(origin.X / grid.CellWidth);
            var iy = (int)Math.Floor(origin.Y / grid.CellHeight);
            var stepX = translation.X > 0f ? 1 : translation.X < 0f ? -1 : 0;
            var stepY = translation.Y > 0f ? 1 : translation.Y < 0f ? -1 : 0;
            var tMaxX = stepX != 0 ? ((ix + (stepX > 0 ? 1 : 0)) * grid.CellWidth - origin.X) / translation.X : float.PositiveInfinity;
            var tMaxY = stepY != 0 ? ((iy + (stepY > 0 ? 1 : 0)) * grid.CellHeight - origin.Y) / translation.Y : float.PositiveInfinity;
            var tDeltaX = stepX != 0 ? grid.CellWidth / Math.Abs(translation.X) : float.PositiveInfinity;
            var tDeltaY = stepY != 0 ? grid.CellHeight / Math.Abs(translation.Y) : float.PositiveInfinity;

            var best = float.MaxValue;
            var maxCells = grid.Columns + grid.Rows + 4;
            for (var cells = 0; cells < maxCells; ++cells)
            {
                RayCell(ix, iy, origin, translation, filter, ignoreIndex, tolerance, ref best);

                // Shapes found in later cells lie beyond this cell's exit, so the first hit is final once it is
                // nearer than the exit.
                var exit = Math.Min(tMaxX, tMaxY);
                if (exit >= 1f || (!passThrough && best <= exit)) break;

                if (tMaxX < tMaxY)
                {
                    ix += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    iy += stepY;
                    tMaxY += tDeltaY;
                }
            }

            SortQueryHits();
            var count = passThrough ? Math.Min(_queryHitCount, hits.Length) : Math.Min(_queryHitCount, 1);
            overflow = passThrough && _queryHitCount > hits.Length;
            for (var i = 0; i < count; ++i)
            {
                ref var h = ref _queryHits[i];
                hits[i] = new RayHit(MakeTarget(h), h.Fraction, h.Point, h.Normal);
            }

            return count;
        }

        // Targets within 'radius' of 'center', nearest surface first, each unit once, each at its nearest torus
        // image. Up to results.Length (32 in the game); when there are more, keeps the nearest and sets 'overflow'.
        public int OverlapCircle(Vec2 center, float radius, in Filter filter, Span<OverlapHit> results, out bool overflow)
        {
            overflow = false;
            if (results.Length == 0 || !(radius >= 0f)) return 0;

            EnsureQueryGrid();
            center = Torus.Wrap(center);
            ++_queryStamp;
            _queryHitCount = 0;

            var grid = _queryGrid;
            var x0 = (int)Math.Floor((center.X - radius) / grid.CellWidth);
            var y0 = (int)Math.Floor((center.Y - radius) / grid.CellHeight);
            var x1 = Math.Min((int)Math.Floor((center.X + radius) / grid.CellWidth), x0 + grid.Columns - 1);
            var y1 = Math.Min((int)Math.Floor((center.Y + radius) / grid.CellHeight), y0 + grid.Rows - 1);
            var point = DistanceProxy.FromPoint(Vec2.Zero);
            var origin = new Pose(Vec2.Zero, Rot.Identity);

            for (var y = y0; y <= y1; ++y)
            for (var x = x0; x <= x1; ++x)
            for (var e = grid.FirstEntry(grid.CellIndex(x, y)); e >= 0; e = grid.NextEntry(e))
            {
                var id = grid.EntryId(e);
                if (!TakeStamp(id, 0)) continue;
                if (!QueryTarget(id, filter, -1, out var proxy, out var pose, out var boundCenter)) continue;

                // Target in the image nearest to the centre, in a frame with the centre at zero.
                var shift = Torus.Delta(center, boundCenter) - (boundCenter - center);
                var relative = new Pose(pose.P + shift - center, pose.Q);

                var distance = Distance.Shapes(proxy, relative, point, origin, out _, out _);
                if (distance <= radius) AddQueryHit(id, distance, default, default);
            }

            SortQueryHits();
            var count = Math.Min(_queryHitCount, results.Length);
            overflow = _queryHitCount > results.Length;
            for (var i = 0; i < count; ++i)
                results[i] = new OverlapHit(MakeTarget(_queryHits[i]), _queryHits[i].Fraction);

            return count;
        }

        private void RayCell(int ix, int iy, Vec2 origin, Vec2 translation, in Filter filter, int ignoreIndex, float tolerance, ref float best)
        {
            var grid = _queryGrid;
            var cellCenter = new Vec2((ix + 0.5f) * grid.CellWidth, (iy + 0.5f) * grid.CellHeight);
            var point = DistanceProxy.FromPoint(Vec2.Zero);
            var start = new Pose(Vec2.Zero, Rot.Identity);

            for (var e = grid.FirstEntry(grid.CellIndex(ix, iy)); e >= 0; e = grid.NextEntry(e))
            {
                var id = grid.EntryId(e);
                if (!QueryTarget(id, filter, ignoreIndex, out var proxy, out var pose, out var boundCenter)) continue;

                // The image of the target that covers this unwrapped cell. A long ray can meet two images of one
                // target; each is tested once, and the unit keeps the nearer hit.
                var kx = (int)Math.Round((cellCenter.X - boundCenter.X) / Torus.Width);
                var ky = (int)Math.Round((cellCenter.Y - boundCenter.Y) / Torus.Height);
                if (!TakeStamp(id, (kx & 0xffff) | (ky << 16))) continue;

                var relative = new Pose(pose.P + new Vec2(kx * Torus.Width, ky * Torus.Height) - origin, pose.Q);
                var state = ShapeCast.Cast(proxy, relative, point, start, translation, tolerance, out var fraction, out var hitPoint, out var normal);
                if (state == ShapeCastState.Miss) continue;
                if (state == ShapeCastState.Overlapped)
                {
                    hitPoint = Vec2.Zero;
                    normal = -translation.Normalized;
                }

                AddQueryHit(id, fraction, Torus.Wrap(origin + hitPoint), normal);
                if (fraction < best) best = fraction;
            }
        }

        // Proxy, pose and bounding-circle centre of a grid entry, or false when the filter or 'ignore' excludes it.
        private bool QueryTarget(int id, in Filter filter, int ignoreIndex, out DistanceProxy proxy, out Pose pose, out Vec2 boundCenter)
        {
            if ((id & BulletFlag) != 0)
            {
                ref var bullet = ref _bullets[id & ~BulletFlag];
                proxy = DistanceProxy.FromShape(bullet.Shape);
                pose = bullet.Pose;
                boundCenter = pose.TransformPoint(bullet.Shape.LocalBoundCenter);
                return Filter.ShouldPair(filter, bullet.Shape.Filter);
            }

            ref var shape = ref _shapes[id];
            proxy = DistanceProxy.FromShape(shape);
            pose = _bodies[shape.Body].Pose;
            boundCenter = pose.TransformPoint(shape.LocalBoundCenter);
            return shape.Body != ignoreIndex && Filter.ShouldPair(filter, shape.Filter);
        }

        // False when this entry (and image) was already tested in this query.
        private bool TakeStamp(int id, int image)
        {
            var bullet = (id & BulletFlag) != 0;
            var index = id & ~BulletFlag;
            var stamps = bullet ? _bulletStamps : _shapeStamps;
            var images = bullet ? _bulletImages : _shapeImages;

            if (stamps[index] == _queryStamp && images[index] == image) return false;
            stamps[index] = _queryStamp;
            images[index] = image;
            return true;
        }

        // Keeps the smallest value per unit.
        private void AddQueryHit(int id, float value, Vec2 point, Vec2 normal)
        {
            var bullet = (id & BulletFlag) != 0;
            var unit = bullet ? id : UnitOf(id);

            for (var i = 0; i < _queryHitCount; ++i)
            {
                ref var hit = ref _queryHits[i];
                if (hit.Unit != unit) continue;
                if (value < hit.Fraction || (value == hit.Fraction && TieBefore(Candidate(id, unit, value, point, normal), hit)))
                    hit = Candidate(id, unit, value, point, normal);
                return;
            }

            if (_queryHitCount == _queryHits.Length) Array.Resize(ref _queryHits, _queryHits.Length * 2);
            _queryHits[_queryHitCount++] = Candidate(id, unit, value, point, normal);
        }

        private HitCandidate Candidate(int id, int unit, float value, Vec2 point, Vec2 normal)
        {
            var bullet = (id & BulletFlag) != 0;
            return new HitCandidate
            {
                Bullet = -1,
                Shape = bullet ? -1 : id,
                TargetBullet = bullet ? id & ~BulletFlag : -1,
                Unit = unit,
                TargetOrder = bullet ? id : _shapes[id].Body,
                Fraction = value,
                Point = point,
                Normal = normal,
            };
        }

        private HitTarget MakeTarget(in HitCandidate hit) =>
            hit.TargetBullet >= 0
                ? new HitTarget(GetBulletId(hit.TargetBullet))
                : new HitTarget(GetBodyId(_shapes[hit.Shape].Body), new ShapeId(hit.Shape, _shapes[hit.Shape].Generation));

        // By value (fraction or distance), then target, then shape. Few hits, so insertion sort.
        private void SortQueryHits()
        {
            for (var i = 1; i < _queryHitCount; ++i)
            {
                var item = _queryHits[i];
                var j = i - 1;
                while (j >= 0 && (item.Fraction < _queryHits[j].Fraction || (item.Fraction == _queryHits[j].Fraction && TieBefore(item, _queryHits[j]))))
                {
                    _queryHits[j + 1] = _queryHits[j];
                    --j;
                }

                _queryHits[j + 1] = item;
            }
        }

        // Tight boxes of the current poses. Rebuilt on the first query after a step or a change of shapes, poses or
        // bullets.
        private void EnsureQueryGrid()
        {
            if (!_queryDirty) return;

            var grid = _queryGrid ?? (_queryGrid = new TorusGrid(Torus, Settings.CellSize));
            grid.Clear();

            for (var s = 0; s < _shapeCount; ++s)
            {
                ref var shape = ref _shapes[s];
                if (shape.Alive) grid.Add(s, shape.ComputeAabb(_bodies[shape.Body].Pose), shape.Body, false, shape.Filter);
            }

            for (var b = 0; b < _bulletCount; ++b)
            {
                ref var bullet = ref _bullets[b];
                if (bullet.Alive) grid.Add(BulletFlag | b, bullet.Shape.ComputeAabb(bullet.Pose), -1, false, bullet.Shape.Filter);
            }

            if (_shapeStamps.Length < _shapes.Length)
            {
                _shapeStamps = new int[_shapes.Length];
                _shapeImages = new int[_shapes.Length];
            }

            if (_bulletStamps.Length < _bullets.Length)
            {
                _bulletStamps = new int[_bullets.Length];
                _bulletImages = new int[_bullets.Length];
            }

            _queryDirty = false;
        }

        private int ResolveShape(ShapeId id)
        {
            if (!IsAlive(id)) throw new ArgumentException($"{id} is not alive");
            return id.Index;
        }

        private TorusGrid _queryGrid;
        private bool _queryDirty = true;
        private int _queryStamp;
        private int[] _shapeStamps = new int[64];
        private int[] _shapeImages = new int[64];
        private int[] _bulletStamps = new int[64];
        private int[] _bulletImages = new int[64];
        private HitCandidate[] _queryHits = new HitCandidate[32];
        private int _queryHitCount;
    }
}
