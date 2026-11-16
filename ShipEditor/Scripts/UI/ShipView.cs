using UnityEngine;
using ShipEditor.Model;
using GameDatabase.DataModel;
using System.Collections.Generic;
using Zenject;
using Services.ObjectPool;

namespace ShipEditor.UI
{
	public class ShipView : MonoBehaviour
	{
        [Inject] private IGameObjectFactory _gameObjectFactory;

		[SerializeField] private float _satelliteOffset = 2;
		[SerializeField] private float _cellSize = 1.0f;
        [SerializeField] private EditorShipLayout _shipLayout;
        [SerializeField] private Transform _satellitesGroup;

        private readonly List<SatelliteData> _satellites = new();
        private Vector2 _shipRectSize;

        public float Width => _shipRectSize.x;
        public float Height => _shipRectSize.y;

        public Vector2 Position { get => transform.localPosition; set => transform.localPosition = value; }
        public float Rotation { get => transform.localEulerAngles.z; set => transform.localEulerAngles = new Vector3(0, 0, value); }
        public float Scale { get => transform.localScale.z; set => transform.localScale = value * Vector3.one; }
        public float CellSize => _cellSize * transform.localScale.z;

		public void InitializeShip(IShipLayoutModel layout, Sprite sprite, IEnumerable<SatelliteMountPoint> mountPoints)
		{
			_shipLayout.Initialize(layout, sprite, _cellSize);

            _satellites.Clear();
            _satellitesGroup.InitializeElements<EditorShipLayout, SatelliteMountPoint>(
                mountPoints, AddSatelliteMountPoint, _gameObjectFactory);
            UpdateShipRectSize();
        }

        private void AddSatelliteMountPoint(EditorShipLayout layout, SatelliteMountPoint mountPoint)
        {
            layout.transform.localEulerAngles = Vector3.zero;
            var position = new Vector2(-mountPoint.Position.y, mountPoint.Position.x);
            var satellite = new SatelliteData(layout, position);
            _satellites.Add(satellite);
        }

        public void RemoveSatellite(int mountPoint) => InitializeSatellite(mountPoint, null, null);
		public void InitializeSatellite(int mountPoint, IShipLayoutModel layout, Sprite sprite)
		{
            _satellites[mountPoint].Layout.Initialize(layout, sprite, _cellSize);
			UpdateSatellitePosition(mountPoint);
            UpdateShipRectSize();
        }

        private void UpdateSatellitePosition(int mountPoint)
		{
			var shipView = _shipLayout;
			var satelliteView = _satellites[mountPoint].Layout;
            var shipSize = Mathf.Max(_shipLayout.Width, _shipLayout.Height);
            var satelliteSize = Mathf.Max(satelliteView.Width, satelliteView.Height);
            var position = _satellites[mountPoint].MountPoint * (shipSize + satelliteSize + _satelliteOffset) * 0.5f;
            var shipPosition = shipView.transform.localPosition;
			satelliteView.transform.localPosition = new Vector3(shipPosition.x + position.x, shipPosition.y + position.y, shipPosition.z);
		}

        private void UpdateShipRectSize()
        {
            _shipRectSize = new Vector2(_shipLayout.Width, _shipLayout.Height);
            var shipPosition = _shipLayout.transform.localPosition;
            for (int i = 0; i < _satellites.Count; ++i)
            {
                var satellite = _satellites[i].Layout;
                var position = satellite.transform.localPosition - shipPosition;
                var width = Mathf.Abs(position.x)*2 + satellite.Width;
                var height = Mathf.Abs(position.y)*2 + satellite.Height;
                if (_shipRectSize.x < width) _shipRectSize.x = width;
                if (_shipRectSize.y < height) _shipRectSize.y = height;
            }
        }

		public Vector2Int WorldToCell(Vector2 center, ShipElementType element, int componentSize)
		{
            var view = GetElement(element);
            center = transform.InverseTransformPoint(center.x, center.y, 0);
			var offset = view.ContentOffset + view.transform.localPosition;
			var x = (center.x - offset.x) / _cellSize - 0.5f * componentSize;
			var y = (offset.y - center.y) / _cellSize - 0.5f * componentSize;
			return new Vector2Int(Mathf.RoundToInt(x + view.X0), Mathf.RoundToInt(y + view.Y0));
		}

		public void ShowSelection(Vector2 position, GameDatabase.DataModel.Component component)
		{
			var size = component != null ? component.Layout.Size : 0;
			_shipLayout.ShowSelection(WorldToCell(position, ShipElementType.Ship, size), component);
            for (int i = 0; i < _satellites.Count; ++i)
    			_satellites[i].Layout.ShowSelection(WorldToCell(position, ShipElementType.Satellite(i), size), component);
		}

		public void AddComponent(IComponentModel component)
		{
            GetElement(component.Location).AddComponent(component);
		}

		public void RemoveComponent(IComponentModel component)
		{
            GetElement(component.Location).RemoveComponent(component);
		}

		public void ReloadAllComponents(ShipElementType shipElement)
		{
            GetElement(shipElement).GenerateModules();
            GetElement(shipElement).GenerateWeaponClasses();
		}

		public void UpdateComponent(IComponentModel component)
		{
            GetElement(component.Location).UpdateComponent(component);
		}

        private EditorShipLayout GetElement(ShipElementType elementType)
        {
            if (elementType.IsShip) return _shipLayout;
            return _satellites[elementType.ToMountPoint].Layout;
        }

        private readonly struct SatelliteData
        {
            public SatelliteData(EditorShipLayout layout, Vector2 mountPoint)
            {
                Layout = layout;
                MountPoint = mountPoint;
            }

            public readonly EditorShipLayout Layout;
            public readonly Vector2 MountPoint;
        }
	}
}
