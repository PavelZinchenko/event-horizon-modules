using System;
using System.Linq;
using System.Collections.Generic;
using ShipEditor.Context;
using Constructor;
using Constructor.Ships;
using Constructor.Satellites;
using GameDatabase.DataModel;
using GameDatabase.Enums;
using Constructor.Model;

namespace ShipEditor.Model
{
    public struct ShipElementType
    {
        private int _index;

        public static explicit operator int(ShipElementType elementType) => elementType._index;

        public static ShipElementType Ship => new ShipElementType(0);
        public static ShipElementType Satellite(int mountPoint) => new ShipElementType(mountPoint + 1);

        public static bool operator ==(ShipElementType lhs, ShipElementType rhs) => lhs._index == rhs._index;
        public static bool operator !=(ShipElementType lhs, ShipElementType rhs) => lhs._index != rhs._index;

        public bool IsShip => _index == 0;
        public int ToMountPoint => _index - 1;

        private ShipElementType(int index) => _index = index;
    }

    public interface IShipEditorModel
	{
		IShipEditorEvents Events { get; }

		IShip Ship { get; }
        IShipPresetStorage Presets { get; }
        IInventoryProvider Inventory { get; }
        IShipDataProvider ShipDataProvider { get; }
        IComponentUpgradesProvider UpgradesProvider { get; }
        ICompatibilityChecker CompatibilityChecker { get; }
		IEnumerable<IComponentModel> InstalledComponents { get; }
        bool IsShipNameEditable { get; }
		string ShipName { get; set; }

		IShipLayoutModel Layout(ShipElementType elementType);

		Satellite Satellite(int mountPoint);
		bool HasSatellite(int mountPoint);
		void RemoveSatellite(int mountPoint);
		bool TryInstallSatellite(int mountPoint, Satellite satellite);
		void InstallSatellite(int mountPoint, ISatellite satellite);

		void SelectShip(IShip ship);
        void SaveShip();
        void SaveShipToPreset(IShipPreset preset);
        bool LoadShipFromPreset(IShipPreset preset);

        bool TryFindComponent(ShipElementType elementType, UnityEngine.Vector2Int position, ComponentInfo info, out IComponentModel component);
		bool TryInstallComponent(ShipElementType shipElement, UnityEngine.Vector2Int position, ComponentInfo componentInfo, ComponentSettings settings);
		void RemoveComponent(IComponentModel component);
		void RemoveAllComponents();

		void SetComponentKeyBinding(IComponentModel component, int key);
		void SetComponentBehaviour(IComponentModel component, int behaviour);

		bool CanBeUnlocked(IComponentModel component);
		void UnlockComponent(IComponentModel component);
	}

	public class ShipEditorModel : IShipEditorModel, IDisposable
	{
		private readonly ShipEditorEvents _events = new();
		private readonly IShipEditorContext _context;
		private LayoutList _layouts;
		private SatelliteList _satellites;
		private ComponentTracker _compatibilityChecker;
		private IShip _ship;
		private string _shipName;

		public IShipEditorEvents Events => _events;

		public IShip Ship => _ship;
        public IShipPresetStorage Presets => _context.ShipPresetStorage;
        public IInventoryProvider Inventory => _context.Inventory;
        public IShipDataProvider ShipDataProvider => _context.ShipDataProvider;
        public IComponentUpgradesProvider UpgradesProvider => _context.UpgradesProvider;
        public ICompatibilityChecker CompatibilityChecker => _compatibilityChecker;
		public IEnumerable<IComponentModel> InstalledComponents => _layouts.All.SelectMany(layout => layout.Components);
		public IShipLayoutModel Layout(ShipElementType elementType) => _layouts[elementType];
		public bool HasSatellite(int mountPoint) => _satellites[mountPoint] != null;
		public Satellite Satellite(int mountPoint) => _satellites[mountPoint]?.BaseModel;
        public bool IsShipNameEditable => _context.IsShipNameEditable;

        public string ShipName 
		{
			get => _shipName ?? _ship?.Name;
            set
            {
                if (!IsShipNameEditable) return;
                _shipName = value;
            }
		}

		public ShipEditorModel(IShipEditorContext context)
		{
			_context = context;
			SelectShip(_context.Ship);
		}

		public void SelectShip(IShip ship)
		{
			SaveShip(_ship);

			_ship = ship;
			_compatibilityChecker = new ComponentTracker(ship);
			_shipName = null;

            _layouts = new LayoutList(_ship);
            _satellites = new SatelliteList(_ship);

			var shipLayout = _layouts[ShipElementType.Ship] = new ShipLayoutModel(
                ShipElementType.Ship, ship.Model.Layout, ship.Model.Barrels, _compatibilityChecker);
			InitializeLayout(shipLayout, ship.Components);

            _events.OnShipChanged(ship);

            for (int i = 0; i < _ship.Model.Satellites.Count; ++i)
                InitializeSatellite(i, _ship.Satellites[i]);
		}

        public void SaveShip() => SaveShip(_ship);

        public void InstallSatellite(int mountPoint, ISatellite satellite)
		{
			RemoveSatellite(mountPoint);
			InitializeSatellite(mountPoint, satellite);
		}

		public bool TryInstallSatellite(int mountPoint, Satellite satellite)
		{
			if (_satellites[mountPoint] == satellite) return true;
			RemoveSatellite(mountPoint);

			if (satellite == null) return true;

            if (!_compatibilityChecker.IsCompatible(satellite, mountPoint))
                return false;

            if (!_context.Inventory.TryRemoveSatellite(satellite))
                return false;

			InitializeSatellite(mountPoint, new CommonSatellite(satellite, Enumerable.Empty<IntegratedComponent>()));
            return true;
		}

		public void RemoveSatellite(int mountPoint)
		{
			var layout = _layouts[ShipElementType.Satellite(mountPoint)];
			var satellite = _satellites[mountPoint];
			if (satellite == null) return;

			SaveSatellite(mountPoint);
			RemoveAllComopnents(layout);
			_events.OnMultipleComponentsChanged();

			_layouts[ShipElementType.Satellite(mountPoint)] = null;
			_satellites[mountPoint] = null;

			_context.Inventory.AddSatellite(satellite.BaseModel);
			_events.OnSatelliteChanged(mountPoint);
		}

		public void SetComponentKeyBinding(IComponentModel component, int key)
		{
			if (key != component.KeyBinding)
				UpdateComponent(component, new ComponentSettings(key, component.Behaviour, component.Locked));
		}

		public void SetComponentBehaviour(IComponentModel component, int behaviour)
		{
			if (behaviour != component.Behaviour)
				UpdateComponent(component, new ComponentSettings(component.KeyBinding, behaviour, component.Locked));
		}


		public bool CanBeUnlocked(IComponentModel component)
		{
			if (!component.Locked)
				return false;
			if (component.Info.Data.Availability == Availability.Common)
				return true;

			return _context.CanBeUnlocked(component.Data);
		}

		public void UnlockComponent(IComponentModel component)
		{
			if (!CanBeUnlocked(component))
				throw new InvalidOperationException();

			if (!_context.Inventory.TryPayForUnlock(component.Info))
				throw new InvalidOperationException();

			UpdateComponent(component, new ComponentSettings(component.KeyBinding, component.Behaviour, false));
		}

		public bool TryInstallComponent(ShipElementType shipElement, UnityEngine.Vector2Int position, ComponentInfo componentInfo, ComponentSettings settings)
		{
			var layout = _layouts[shipElement];
			if (layout == null)
				return false;

			if (!_compatibilityChecker.IsCompatible(componentInfo.Data))
				return false;

			if (!layout.IsSuitableLocation(position.x, position.y, componentInfo.Data))
				return false;

			if (!_context.Inventory.TryRemoveComponent(componentInfo))
				return false;

			var component = layout.InstallComponent(position.x, position.y, componentInfo, settings);
			_events.OnComponentAdded(component);
			return true;
		}

		public void RemoveComponent(IComponentModel component)
		{
			if (component.Locked)
				throw new InvalidOperationException();

			_layouts[component.Location].RemoveComponent(component);
			_context.Inventory.AddComponent(component.Info);
			_events.OnComponentRemoved(component);
		}

		public void RemoveAllComponents()
		{
            foreach (var layout in _layouts.All)
			    RemoveAllComopnents(layout);

			_events.OnMultipleComponentsChanged();
		}

		public void Dispose()
		{
			SaveShip(_ship);
		}

		public bool TryFindComponent(ShipElementType elementType, UnityEngine.Vector2Int position, ComponentInfo info, out IComponentModel component)
		{
			component = _layouts[elementType].FindComponent(position.x, position.y, info);
			return component != null;
		}

        public void SaveShipToPreset(IShipPreset preset)
        {
            var shipLayout = _layouts[ShipElementType.Ship];
            preset.Components.Assign(ExportComponents(shipLayout));

            for (int i = 0; i < _satellites.Count; ++i)
            {
                var satellite = _satellites[i];
                if (satellite != null)
                    satellite = new CommonSatellite(satellite.BaseModel, ExportComponents(_layouts[ShipElementType.Satellite(i)]));

                preset.Satellites[i] = satellite;
            }
        }

        public bool LoadShipFromPreset(IShipPreset preset)
        {
            if (preset.Ship != _ship.Model.BaseModel) return false;

            bool result = true;
            RemoveAllComponents();
            for (int i = 0; i < _satellites.Count; ++i)
                RemoveSatellite(i);

            result &= InstallComponentsFromPreset(preset.Components, ShipElementType.Ship);

            for (int i = 0; i < preset.Satellites.UsedMountPoints; ++i)
                if (preset.Satellites[i] != null)
                    result &= TryInstallSatellite(i, preset.Satellites[i].BaseModel) && 
                        InstallComponentsFromPreset(preset.Satellites[i].Components, ShipElementType.Satellite(i));

            for (int i = 0; i < _satellites.Count; ++i)
                _events.OnSatelliteChanged(i);
            _events.OnMultipleComponentsChanged();

            return result;
        }

        private bool InstallComponentsFromPreset(IEnumerable<IntegratedComponent> components, ShipElementType shipElement)
        {
            bool result = true;
            var layout = _layouts[shipElement];

            foreach (var component in components)
            {
                if (layout.FindComponent(component.X, component.Y, component.Info) != null) continue;
                if (!layout.IsSuitableLocation(component.X, component.Y, component.Info.Data) ||
                    !_compatibilityChecker.IsCompatible(component.Info.Data) ||
                    !_context.Inventory.TryRemoveComponent(component.Info))
                {
                    result = false;
                    continue;
                }

                layout.InstallComponent(component.X, component.Y, component.Info,
                    new ComponentSettings(component.KeyBinding, component.Behaviour, component.Locked));
            }

            return result;
        }

        private ISatellite SaveSatellite(int mountPoint)
		{
            var elementType = ShipElementType.Satellite(mountPoint);
			var satellite = _satellites[mountPoint];
			if (satellite != null && _layouts[elementType].DataChanged)
			{
				satellite.Components.Assign(ExportComponents(_layouts[elementType]));
				_layouts[elementType].DataChanged = false;
			}

			return satellite;
		}

		private void SaveShip(IShip ship)
		{
			if (ship == null) return;

			if (!string.IsNullOrEmpty(_shipName))
				Ship.Name = _shipName;

			var shipLayout = _layouts[ShipElementType.Ship];
			if (shipLayout.DataChanged)
			{
				Ship.Components.Assign(ExportComponents(shipLayout));
				shipLayout.DataChanged = false;
			}

            for (int i = 0; i < _satellites.Count; ++i)
                _ship.Satellites[i] = SaveSatellite(i);
		}

		private static IEnumerable<IntegratedComponent> ExportComponents(ShipLayoutModel layout)
		{
			if (layout == null) yield break;
			foreach (var model in layout.Components.OrderBy(GetComponentSortOrder))
				yield return new IntegratedComponent(model.Info, model.X, model.Y, 
					layout.GetBarrelId(model), model.KeyBinding, model.Behaviour, model.Locked);
		}

        private static int GetComponentSortOrder(IComponentModel componentModel)
        {
            return componentModel.Y * 10000 + componentModel.X;
        }

		private void RemoveAllComopnents(ShipLayoutModel layout, bool keepLocked = true)
		{
			if (layout == null) return;

			foreach (var item in layout.Components)
				if (!keepLocked || !item.Locked)
					_context.Inventory.AddComponent(item.Info);

			layout.RemoveAll(keepLocked);
		}

		private void UpdateComponent(IComponentModel component, ComponentSettings settings)
		{
			_layouts[component.Location].UpdateComponent(component, settings);
			_events.OnComponentModified(component);
		}

		private void InitializeLayout(ShipLayoutModel layout, IEnumerable<IntegratedComponent> components)
		{
			if (components == null) return;

			foreach (var component in components)
			{
				if (!layout.IsSuitableLocation(component.X, component.Y, component.Info.Data) || 
					!_compatibilityChecker.IsCompatible(component.Info.Data))
				{
					GameDiagnostics.Trace.LogError($"Invalid component {component.Info.Data.Name} at [{component.X},{component.Y}]");
					continue;
				}

				layout.InstallComponent(component.X, component.Y, component.Info,
					new ComponentSettings(component.KeyBinding, component.Behaviour, component.Locked));
			}

			layout.DataChanged = false;
		}

		private void InitializeSatellite(int mountPoint, ISatellite satellite)
		{
			_satellites[mountPoint] = satellite;
            var elementType = ShipElementType.Satellite(mountPoint);
            if (satellite != null)
			{
                _layouts[elementType] = new ShipLayoutModel(elementType,
					new ShipLayoutAdapter(satellite.BaseModel.Layout), satellite.BaseModel.Barrels, _compatibilityChecker);
				InitializeLayout(_layouts[elementType], satellite.Components);
			}
			else
			{
				_layouts[elementType] = null;
			}

			_events.OnSatelliteChanged(mountPoint);
		}
	}
}
