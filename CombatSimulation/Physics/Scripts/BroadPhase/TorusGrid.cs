using System;

namespace CombatSimulation.Physics
{
    // Uniform grid on the torus, rebuilt every step.
    //
    // A proxy goes into every cell that its AABB covers, so large bodies (starbases) and small ones (drones)
    // share one grid. A pair is reported once: in the cell that holds the lower corner of the overlap of the
    // two cell ranges. Cell ranges are integers, so the choice of that cell has no rounding error.
    //
    // Limit: an AABB must be smaller than half the torus on each axis.
    internal sealed class TorusGrid
    {
        public TorusGrid(Torus torus, float cellSize)
        {
            if (!(cellSize > 0f)) throw new ArgumentException("Cell size must be positive");

            _torus = torus;
            _columns = Math.Max(1, (int)(torus.Width / cellSize));
            _rows = Math.Max(1, (int)(torus.Height / cellSize));
            _cellWidth = torus.Width / _columns;
            _cellHeight = torus.Height / _rows;
            _cellHead = new int[_columns * _rows];
            for (var i = 0; i < _cellHead.Length; ++i) _cellHead[i] = -1;
        }

        public void Clear()
        {
            for (var i = 0; i < _usedCellCount; ++i)
                _cellHead[_usedCells[i]] = -1;

            _usedCellCount = 0;
            _entryCount = 0;
            _proxyCount = 0;
        }

        // Group: proxies of one group never pair (shapes of one body).
        // Fixed: two fixed proxies never pair (two bodies that cannot move).
        public void Add(int id, in Aabb aabb, int group, bool isFixed)
        {
            EnsureProxyCapacity(_proxyCount + 1);

            var index = _proxyCount++;
            ref var proxy = ref _proxies[index];
            proxy.Id = id;
            proxy.Aabb = aabb;
            proxy.Group = group;
            proxy.Fixed = isFixed;
            proxy.X0 = (int)Math.Floor(aabb.Lower.X / _cellWidth);
            proxy.Y0 = (int)Math.Floor(aabb.Lower.Y / _cellHeight);
            proxy.X1 = Math.Min((int)Math.Floor(aabb.Upper.X / _cellWidth), proxy.X0 + _columns - 1);
            proxy.Y1 = Math.Min((int)Math.Floor(aabb.Upper.Y / _cellHeight), proxy.Y0 + _rows - 1);

            for (var y = proxy.Y0; y <= proxy.Y1; ++y)
            for (var x = proxy.X0; x <= proxy.X1; ++x)
            {
                var cell = Wrap(y, _rows) * _columns + Wrap(x, _columns);
                if (_cellHead[cell] < 0)
                {
                    EnsureCellListCapacity(_usedCellCount + 1);
                    _usedCells[_usedCellCount++] = cell;
                }

                EnsureEntryCapacity(_entryCount + 1);
                _entryProxy[_entryCount] = index;
                _entryNext[_entryCount] = _cellHead[cell];
                _cellHead[cell] = _entryCount++;
            }
        }

        // Writes each overlapping pair once, as (lowId << 32) | highId. The order is not sorted.
        public int FindPairs(ref long[] pairs)
        {
            var count = 0;
            for (var c = 0; c < _usedCellCount; ++c)
            {
                var cell = _usedCells[c];
                var cellX = cell % _columns;
                var cellY = cell / _columns;

                for (var e1 = _cellHead[cell]; e1 >= 0; e1 = _entryNext[e1])
                for (var e2 = _entryNext[e1]; e2 >= 0; e2 = _entryNext[e2])
                {
                    ref var a = ref _proxies[_entryProxy[e1]];
                    ref var b = ref _proxies[_entryProxy[e2]];
                    if (a.Group == b.Group || (a.Fixed && b.Fixed)) continue;

                    if (!Overlap(a.Aabb.Lower.X, a.Aabb.Upper.X, b.Aabb.Lower.X, b.Aabb.Upper.X, _torus.Width, a.X0, a.X1, b.X0, b.X1, _columns, out var lowX)) continue;
                    if (!Overlap(a.Aabb.Lower.Y, a.Aabb.Upper.Y, b.Aabb.Lower.Y, b.Aabb.Upper.Y, _torus.Height, a.Y0, a.Y1, b.Y0, b.Y1, _rows, out var lowY)) continue;
                    if (Wrap(lowX, _columns) != cellX || Wrap(lowY, _rows) != cellY) continue;

                    if (count == pairs.Length) Array.Resize(ref pairs, pairs.Length * 2);
                    pairs[count++] = a.Id < b.Id ? ((long)a.Id << 32) | (uint)b.Id : ((long)b.Id << 32) | (uint)a.Id;
                }
            }

            return count;
        }

        // 1D overlap on a circle of length 'size'. B is shifted to the image nearest to A.
        // lowCell is the first cell (in A's unwrapped numbering) that both ranges cover.
        private static bool Overlap(float a0, float a1, float b0, float b1, float size, int ca0, int ca1, int cb0, int cb1, int cells, out int lowCell)
        {
            var centerDelta = 0.5f * (b0 + b1) - 0.5f * (a0 + a1);
            var shift = centerDelta > 0.5f * size ? -1 : centerDelta < -0.5f * size ? 1 : 0;

            var shiftedB0 = b0 + shift * size;
            var shiftedB1 = b1 + shift * size;
            lowCell = Math.Max(ca0, cb0 + shift * cells);
            var highCell = Math.Min(ca1, cb1 + shift * cells);

            return shiftedB0 <= a1 && a0 <= shiftedB1 && lowCell <= highCell;
        }

        private static int Wrap(int value, int size)
        {
            value %= size;
            return value < 0 ? value + size : value;
        }

        private void EnsureProxyCapacity(int count)
        {
            if (count > _proxies.Length) Array.Resize(ref _proxies, Math.Max(count, _proxies.Length * 2));
        }

        private void EnsureEntryCapacity(int count)
        {
            if (count <= _entryProxy.Length) return;
            var size = Math.Max(count, _entryProxy.Length * 2);
            Array.Resize(ref _entryProxy, size);
            Array.Resize(ref _entryNext, size);
        }

        private void EnsureCellListCapacity(int count)
        {
            if (count > _usedCells.Length) Array.Resize(ref _usedCells, Math.Max(count, _usedCells.Length * 2));
        }

        private struct Proxy
        {
            public int Id;
            public Aabb Aabb;
            public int Group;
            public bool Fixed;
            public int X0, Y0, X1, Y1;
        }

        private readonly Torus _torus;
        private readonly int _columns;
        private readonly int _rows;
        private readonly float _cellWidth;
        private readonly float _cellHeight;
        private readonly int[] _cellHead;

        private Proxy[] _proxies = new Proxy[64];
        private int _proxyCount;
        private int[] _entryProxy = new int[256];
        private int[] _entryNext = new int[256];
        private int _entryCount;
        private int[] _usedCells = new int[256];
        private int _usedCellCount;
    }
}
