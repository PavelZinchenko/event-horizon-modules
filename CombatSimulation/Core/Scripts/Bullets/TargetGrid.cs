using System;
using CombatSimulation.Physics;
using Utilites;

namespace CombatSimulation.Core
{
    // The per-tick target snapshot (investigation R9): active ships in a uniform torus grid, rebuilt once per tick from
    // the poses at the start of the step. A search visits only the cells within its radius, so its cost does not grow
    // with the number of bullets times targets. It allocates nothing once its buffers have grown.
    internal sealed class TargetGrid
    {
        public const float CellSize = 32f;

        private static readonly float ConeCos = (float)Math.Cos(BulletDefinition.SearchCone);

        public TargetGrid(Torus torus, ulong matchSeed = 0UL)
        {
            _torus = torus;
            _matchSeed = matchSeed;
            _columns = Math.Max(1, (int)(torus.Width / CellSize));
            _rows = Math.Max(1, (int)(torus.Height / CellSize));
            _cellWidth = torus.Width / _columns;
            _cellHeight = torus.Height / _rows;
            _cellHead = new int[_columns * _rows];
            for (var i = 0; i < _cellHead.Length; ++i) _cellHead[i] = -1;
            _usedCells = new int[16];
        }

        public int Count => _count;

        public void Clear()
        {
            for (var i = 0; i < _usedCellCount; ++i) _cellHead[_usedCells[i]] = -1;
            _usedCellCount = 0;
            _count = 0;
            _maxRadius = 0f;
        }

        public void Add(EntityId id, Allegiance allegiance, Vec2 position, float radius,
            TargetPriority priority = TargetPriority.Normal, float chanceToAvoidMissile = 0f)
        {
            if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);

            position = _torus.Wrap(position);
            var cell = CellIndex(Floor(position.X / _cellWidth), Floor(position.Y / _cellHeight));
            if (_cellHead[cell] < 0)
            {
                if (_usedCellCount == _usedCells.Length) Array.Resize(ref _usedCells, _usedCells.Length * 2);
                _usedCells[_usedCellCount++] = cell;
            }

            _entries[_count] = new Entry(id, allegiance, position, radius, priority, chanceToAvoidMissile, _cellHead[cell]);
            _cellHead[cell] = _count++;
            if (radius > _maxRadius) _maxRadius = radius;
        }

        // Legacy GetEnemyForMissile with the owner's fallback (2026-10-20). Range is the centre distance minus both
        // radii, at the nearest torus image. Within maxRange: the nearest enemy within the search cone of the heading;
        // without one, the enemy with the smallest deviation. Equal keys go to the lower EntityId, so the result does
        // not depend on the order of cells or entries. Beyond maxRange there is no target. All around (legacy 360 degree
        // deviation, phase 5, step 4c): every enemy is in the cone, so the nearest one wins.
        // Phase 5, step 9a (owner, 2026-11-07): an enemy with priority None is never a target. Inside the cone a higher
        // priority wins before distance; the fallback by deviation ignores priority (legacy). A candidate with missile
        // camouflage is skipped with its chance, one roll per search: counter-based from the match seed, the searcher,
        // the tick and the candidate, so both cores agree (legacy System.Random, D20).
        public bool FindMissileTarget(Vec2 position, Vec2 heading, Allegiance allegiance, float maxRange, float selfRadius,
            out EntityId target, bool allAround = false, EntityId searcher = default, long tick = 0)
        {
            target = default;
            if (_count == 0) return false;

            position = _torus.Wrap(position);
            var reach = maxRange + selfRadius + _maxRadius;
            var x0 = Floor((position.X - reach) / _cellWidth);
            var y0 = Floor((position.Y - reach) / _cellHeight);
            var x1 = Math.Min(Floor((position.X + reach) / _cellWidth), x0 + _columns - 1);
            var y1 = Math.Min(Floor((position.Y + reach) / _cellHeight), y0 + _rows - 1);

            var found = false;
            var bestInCone = false;
            var bestKey = 0f;
            var bestPriority = TargetPriority.None;
            for (var y = y0; y <= y1; ++y)
            for (var x = x0; x <= x1; ++x)
            for (var e = _cellHead[CellIndex(x, y)]; e >= 0; e = _entries[e].Next)
            {
                ref readonly var entry = ref _entries[e];
                if (!Allegiances.IsHostile(entry.Allegiance, allegiance) || entry.Priority == TargetPriority.None) continue;

                var delta = _torus.Delta(position, entry.Position);
                var distance = delta.Length;
                if (distance - selfRadius - entry.Radius > maxRange) continue;
                if (entry.ChanceToAvoidMissile > 0f && Dodges(entry, searcher, tick)) continue;

                // Cosine of the deviation: larger means nearer the heading. A target at the centre counts as ahead.
                var cos = distance > 0f ? Vec2.Dot(delta, heading) / distance : 1f;
                var inCone = allAround || cos >= ConeCos;
                var key = inCone ? distance - entry.Radius : -cos;

                if (found)
                {
                    if (bestInCone && !inCone) continue;
                    if (inCone == bestInCone)
                    {
                        if (inCone && entry.Priority != bestPriority)
                        {
                            if (entry.Priority < bestPriority) continue;
                        }
                        else if (key > bestKey || (key == bestKey && entry.Id.Value > target.Value))
                            continue;
                    }
                }

                found = true;
                bestInCone = inCone;
                bestKey = key;
                bestPriority = entry.Priority;
                target = entry.Id;
            }

            return found;
        }

        // Legacy Percentage(chance): a chance of 1 always dodges.
        private bool Dodges(in Entry entry, EntityId searcher, long tick)
        {
            if (entry.ChanceToAvoidMissile >= 1f) return true;
            var random = CounterRandom.Create(_matchSeed, searcher, tick, RandomPurpose.MissileDodge, entry.Id);
            return random.NextFloat() < entry.ChanceToAvoidMissile;
        }

        private int CellIndex(int x, int y) => Wrap(y, _rows) * _columns + Wrap(x, _columns);

        private static int Wrap(int value, int size)
        {
            value %= size;
            return value < 0 ? value + size : value;
        }

        private static int Floor(float value) => (int)Math.Floor(value);

        private readonly struct Entry
        {
            public readonly EntityId Id;
            public readonly Allegiance Allegiance;
            public readonly Vec2 Position;
            public readonly float Radius;
            public readonly TargetPriority Priority;
            public readonly float ChanceToAvoidMissile;
            public readonly int Next;

            public Entry(EntityId id, Allegiance allegiance, Vec2 position, float radius, TargetPriority priority,
                float chanceToAvoidMissile, int next)
            {
                Id = id;
                Allegiance = allegiance;
                Position = position;
                Radius = radius;
                Priority = priority;
                ChanceToAvoidMissile = chanceToAvoidMissile;
                Next = next;
            }
        }

        private readonly Torus _torus;
        private readonly ulong _matchSeed;
        private readonly int _columns;
        private readonly int _rows;
        private readonly float _cellWidth;
        private readonly float _cellHeight;
        private readonly int[] _cellHead;
        private int[] _usedCells;
        private int _usedCellCount;
        private Entry[] _entries = new Entry[16];
        private int _count;
        private float _maxRadius;
    }
}
