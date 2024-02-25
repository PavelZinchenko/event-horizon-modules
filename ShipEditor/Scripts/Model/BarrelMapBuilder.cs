using System.Collections.Generic;
using GameDatabase.Model;
using GameDatabase.Enums;

namespace ShipEditor.Model
{
	public class BarrelMapBuilder
	{
		private string _layout;
		private byte[] _map;
		private readonly Queue<int> _mapCells = new Queue<int>();

		public byte BarrelCount { get; private set; }
		public int Size { get; private set; }

		public int this[int x, int y]
		{
			get
			{
				if (x < 0 || x >= Size) return -1;
				if (y < 0 || y >= Size) return -1;
				return _map[x + y * Size] - 1;
			}
		}

		public void Build(Layout layout)
		{
			BarrelCount = 0;
			Size = layout.Size;
			_layout = layout.Data;
			_map = new byte[Size * Size];

			for (int i = 0; i < Size; ++i)
			{
				for (int j = 0; j < Size; ++j)
				{
					if (TryAssignNewBarrel(j, i))
						ProcessCells();
				}
			}
        }

        private void ProcessCells()
		{
			while (_mapCells.Count > 0)
			{
				var index = _mapCells.Dequeue();
				var y = index / Size;
				var x = index % Size;
				var barrel = _map[index];

				TryAssignBarrel(x - 1, y, barrel);
				TryAssignBarrel(x + 1, y, barrel);
				TryAssignBarrel(x, y + 1, barrel);
				TryAssignBarrel(x, y - 1, barrel);
			}
		}

        private bool TryAssignNewBarrel(int x, int y)
        {
            if (!TryGetUnassignedWeaponCell(x, y, out int index, out bool isStock) || !isStock) return false;
            _map[index] = ++BarrelCount;
            _mapCells.Enqueue(index);
            return true;
        }

        private bool TryAssignBarrel(int x, int y, byte barrelId)
		{
            if (!TryGetUnassignedWeaponCell(x, y, out int index, out bool isStock)) return false;
            if (isStock) _mapCells.Enqueue(index);
            _map[index] = barrelId;
            return true;
        }

        private bool TryGetUnassignedWeaponCell(int x, int y, out int index, out bool isStock)
        {
            isStock = true;
            index = x + y * Size;
            if (x < 0 || x >= Size) return false;
            if (y < 0 || y >= Size) return false;
            if (_map[index] > 0) return false;

            switch ((CellType)_layout[index])
            {
                case CellType.Weapon:
                    isStock = true;
                    return true;
                case Layout.CustomWeaponCell:
                    isStock = false;
                    return true;
                default:
                    return false;
            }
        }
    }
}
