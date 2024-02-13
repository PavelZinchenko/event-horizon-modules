using System.Collections.Generic;
using UnityEngine;
using GameDatabase.Enums;

namespace ShipEditor
{
	public class ShipMeshBuilder
	{
		public interface ILayout
		{
			int Width { get; }
			int Height { get; }
			CellType this[int x, int y] { get; }
			string GetWeaponClasses(int x, int y);
		}

		private readonly Dictionary<int, int> _cache = new();
		private readonly List<Vector3> _vertices = new();
		private readonly List<Vector2> _uv = new();
		private readonly List<Color32> _colors = new();
		private readonly List<int> _triangles = new();
		private readonly float _cellSize;

		public Color OuterCellColor { get; set; } = Color.white;
		public Color InnerCellColor { get; set; } = Color.white;
		public Color EngineCellColor { get; set; } = Color.white;
		public Color WeaponCellColor { get; set; } = Color.white;

		public ShipMeshBuilder(float cellSize)
		{
			_cellSize = cellSize;
		}

		public void Build(ILayout layout)
		{
			for (int i = 0; i < layout.Height; ++i)
			{
				for (int j = 0; j < layout.Width; ++j)
				{
					var cellType = layout[j, i];

					if (!IsValidCell(cellType)) continue;

                    if (cellType == CellType.InnerOuter)
                        AddMixedCell(j, i, CellType.Outer, CellType.Inner);
                    else
                        AddSolidCell(j, i, cellType);
				}
			}
		}

        private void AddSolidCell(int x, int y, CellType cellType)
        {
            var v1 = GetVertex(x, y, cellType);
            var v2 = GetVertex(x+1, y, cellType);
            var v3 = GetVertex(x+1, y+1, cellType);
            var v4 = GetVertex(x, y+1, cellType);

            _triangles.Add(v1);
            _triangles.Add(v2);
            _triangles.Add(v3);
            _triangles.Add(v3);
            _triangles.Add(v4);
            _triangles.Add(v1);
        }

        private void AddMixedCell(int x, int y, CellType cellType1, CellType cellType2)
        {
            var v0 = GetVertex(x, y + 1, cellType1);
            var v1 = GetVertex(x, y, cellType1);
            var v2 = GetVertex(x + 1, y, cellType1);
            var v3 = GetVertex(x + 1, y, cellType2);
            var v4 = GetVertex(x+1, y+1, cellType2);
            var v5 = GetVertex(x, y + 1, cellType2);

            _triangles.Add(v0);
            _triangles.Add(v1);
            _triangles.Add(v2);
            _triangles.Add(v3);
            _triangles.Add(v4);
            _triangles.Add(v5);
        }

        public Mesh CreateMesh()
		{
			var mesh = new Mesh();
			mesh.vertices = _vertices.ToArray();
			mesh.triangles = _triangles.ToArray();
			mesh.uv = _uv.ToArray();
			mesh.colors32 = _colors.ToArray();
			mesh.Optimize();

			//Debug.LogError($"{mesh.vertices.Length} vertices, {mesh.triangles.Length / 3} triangles");
			return mesh;
		}

		private static int Key(int x, int y, CellType cell)
		{
			return ((((y & 0xfff) << 12) + (x & 0xfff)) << 12) + (byte)cell;
		}

		private static bool IsValidCell(CellType cell)
		{
			switch (cell)
			{
				case CellType.Outer:
				case CellType.Inner:
				case CellType.InnerOuter:
				case CellType.Engine:
				case CellType.Weapon:
					return true;
				default:
					return false;
			}
		}

		private Color32 CellToColor(CellType cell)
		{
			switch (cell)
			{
				case CellType.Outer: return OuterCellColor;
				case CellType.Inner: return InnerCellColor;
				case CellType.InnerOuter: return InnerCellColor;
				case CellType.Engine: return EngineCellColor;
				case CellType.Weapon: return WeaponCellColor;
				default: return new Color32();
			}
		}

		private int GetVertex(int x, int y, CellType cell)
		{
			var key = Key(x, y, cell);
			if (!_cache.TryGetValue(key, out var id))
			{
				id = _vertices.Count;
				_vertices.Add(new Vector3(x * _cellSize, -y*_cellSize, 0));
				_uv.Add(new Vector2(x % 2 == 0 ? 0f : 1f, y % 2 == 0 ? 0f : 1f));
				_colors.Add(CellToColor(cell));
				_cache.Add(key, id);
			}

			return id;
		}
	}
}
