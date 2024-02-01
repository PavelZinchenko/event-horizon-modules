﻿using System.Collections.Generic;
using UnityEngine;
using GameDatabase.Model;
using GameDatabase.Enums;
using Services.Resources;

namespace ShipEditor
{
	public class ModuleMeshBuilder
	{
		private readonly IResourceLocator _resourceLocator;
		private readonly List<Vector3> _vertices = new();
		private readonly List<Vector2> _uv = new();
		private readonly List<Color> _colors = new();
		private readonly List<int> _triangles = new();
		private readonly float _cellSize;

		public ModuleMeshBuilder(IResourceLocator resourceLocator, float cellSize)
		{
			_cellSize = cellSize;
			_resourceLocator = resourceLocator;
		}

		public void AddComponent(int x, int y, GameDatabase.DataModel.Component component)
		{
			var layout = component.Layout;
			var color = (Color)component.Color;

			var sprite = _resourceLocator.GetSprite(component.Icon);
			var spriteRect = new SpriteRect(sprite);
			var rect = new ComponentRect(layout);

			var xmin = rect.xmin;
			var xmax = rect.xmax + 1;
			var ymin = rect.ymin;
			var ymax = rect.ymax + 1;

			var aspect = spriteRect.Aspect;
			var centerX = x + 0.5f * (xmax + xmin) * _cellSize;
			var centerY = y + 0.5f * (ymax + ymin) * _cellSize;
			var halfWidth = rect.Size * _cellSize * 0.5f * aspect.x;
			var halfHeight = rect.Size * _cellSize * 0.5f * aspect.y;

			int index = _vertices.Count;
			_vertices.Add(new Vector3(centerX - halfWidth, -centerY + halfHeight, 0));
			_vertices.Add(new Vector3(centerX + halfWidth, -centerY + halfHeight, 0));
			_vertices.Add(new Vector3(centerX + halfWidth, -centerY - halfHeight, 0));
			_vertices.Add(new Vector3(centerX - halfWidth, -centerY - halfHeight, 0));

			_uv.Add(spriteRect.TransformUV(new Vector2(0,0)));
			_uv.Add(spriteRect.TransformUV(new Vector2(1,0)));
			_uv.Add(spriteRect.TransformUV(new Vector2(1,1)));
			_uv.Add(spriteRect.TransformUV(new Vector2(0,1)));

			_triangles.Add(index);
			_triangles.Add(index+1);
			_triangles.Add(index+2);
			_triangles.Add(index+2);
			_triangles.Add(index+3);
			_triangles.Add(index);

			_colors.Add(color);
			_colors.Add(color);
			_colors.Add(color);
			_colors.Add(color);
		}

		public Mesh CreateMesh()
		{
			var mesh = new Mesh();
			mesh.vertices = _vertices.ToArray();
			mesh.triangles = _triangles.ToArray();
			mesh.uv = _uv.ToArray();
			mesh.colors = _colors.ToArray();
			mesh.Optimize();

			//Debug.LogError($"{mesh.vertices.Length} vertices, {mesh.triangles.Length / 3} triangles");
			return mesh;
		}

		private struct SpriteRect
		{
			public float xmin;
			public float xmax;
			public float ymin;
			public float ymax;

			public Vector2 TransformUV(Vector2 uv) => new Vector2(xmin + (xmax - xmin) * uv.x, ymax + (ymin - ymax) * uv.y);

			public Vector2 Aspect
			{
				get
				{
					var width = xmax - xmin;
					var height = ymax - ymin;
					var max = Mathf.Max(width, height);
					return new Vector2(width / max, height / max);
				}
			}

			public SpriteRect(Sprite sprite)
			{
				int count = sprite.uv.Length;

				xmax = xmin = sprite.uv[0].x;
				ymax = ymin = sprite.uv[0].y;

				for (int i = 1; i < count; ++i)
				{
					var point = sprite.uv[i];
					if (point.x < xmin) xmin = point.x;
					if (point.x > xmax) xmax = point.x;
					if (point.y < ymin) ymin = point.y;
					if (point.y > ymax) ymax = point.y;
				}
			}
		}

		private struct ComponentRect
		{
			public int xmin;
			public int xmax;
			public int ymin;
			public int ymax;

			public int Width => xmax >= xmin ? xmax - xmin + 1 : 0;
			public int Height => ymax >= ymin ? ymax - ymin + 1 : 0;
			public int Size => Mathf.Max(Width, Height);

			public ComponentRect(Layout layout)
			{
				var size = layout.Size;
				int count = 0;

				xmin = size;
				xmax = 0;
				ymin = size;
				ymax = 0;

				for (int i = 0; i < size; ++i)
				{
					for (int j = 0; j < size; ++j)
					{
						var cell = (CellType)layout[j,i];
						if (cell == CellType.Empty) continue;

						count++;
						if (j < xmin) xmin = j;
						if (j > xmax) xmax = j;
						if (i < ymin) ymin = i;
						if (i > ymax) ymax = i;
					}
				}

				if (xmin > xmax) xmin = xmax = 0;
				if (ymin > ymax) ymin = ymax = 0;
			}
		}
	}
}
