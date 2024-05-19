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
	public enum SatelliteLocation
	{
		Left,
		Right,
	}

	public enum ShipElementType
	{
		Ship,
		SatelliteL,
		SatelliteR,
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

		IShipLayoutModel Layout(SatelliteLocation location);
		IShipLayoutModel Layout(ShipElementType elementType);

		Satellite Satellite(SatelliteLocation location);
		bool HasSatellite(SatelliteLocation location);
		void RemoveSatellite(SatelliteLocation location);
		bool TryInstallSatellite(SatelliteLocation location, Satellite satellite);
		void InstallSatellite(SatelliteLocation location, ISatellite satellite);

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
		private ShipElementContainer<ShipLayoutModel> _layout;
		private ShipElementContainer<ISatellite> _satellite;
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
		
		public IEnumerable<IComponentModel> InstalledComponents
		{
			get 
			{
				var components = (IEnumerable<IComponentModel>)_layout[ShipElementType.Ship].Components;
				if (_layout[ShipElementType.SatelliteL] != null)
					components = components.Concat(_layout[ShipElementType.SatelliteL].Components);
				if (_layout[ShipElementType.SatelliteR] != null)
					components = components.Concat(_layout[ShipElementType.SatelliteR].Components);

				return components;
			}
		}

		public IShipLayoutModel Layout(SatelliteLocation location) => _layout[location];
		public IShipLayoutModel Layout(ShipElementType elementType) => _layout[elementType];
		public bool HasSatellite(SatelliteLocation location) => _layout[location] != null;
		public Satellite Satellite(SatelliteLocation location) => _satellite[location]?.Information;
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

			var shipLayout = _layout[ShipElementType.Ship] = new ShipLayoutModel(
				ShipElementType.Ship, ship.Model.Layout, ship.Model.Barrels, _compatibilityChecker);

			InitializeLayout(shipLayout, ship.Components);
			InitializeSatellite(SatelliteLocation.Left, _ship.FirstSatellite);
			InitializeSatellite(SatelliteLocation.Right, _ship.SecondSatellite);

			_events.OnShipChanged(ship);
		}

        public void SaveShip() => SaveShip(_ship);

        public void InstallSatellite(SatelliteLocation location, ISatellite satellite)
		{
			RemoveSatellite(location);
			InitializeSatellite(location, satellite);
		}

		public bool TryInstallSatellite(SatelliteLocation location, Satellite satellite)
		{
			if (_satellite[location] == satellite) return true;
			RemoveSatellite(location);

			if (satellite == null) return true;

            if (!_compatibilityChecker.IsCompatible(satellite))
                return false;

            if (!_context.Inventory.TryRemoveSatellite(satellite))
                return false;

			InitializeSatellite(location, new CommonSatellite(satellite, Enumerable.Empty<IntegratedComponent>()));
            return true;
		}

		public void RemoveSatellite(SatelliteLocation location)
		{
			var layout = _layout[location];
			var satellite = _satellite[location];
			if (satellite == null) return;

			SaveSatellite(location);
			RemoveAllComopnents(layout);
			_events.OnMultipleComponentsChanged();

			_layout[location] = null;
			_satellite[location] = null;

			_context.Inventory.AddSatellite(satellite.Information);
			_events.OnSatelliteChanged(location);
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
			var layout = _layout[shipElement];
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

			_layout[component.Location].RemoveComponent(component);
			_context.Inventory.AddComponent(component.Info);
			_events.OnComponentRemoved(component);
		}

		public void RemoveAllComponents()
		{
			RemoveAllComopnents(_layout[ShipElementType.Ship]);
			RemoveAllComopnents(_layout[ShipElementType.SatelliteL]);
			RemoveAllComopnents(_layout[ShipElementType.SatelliteR]);
			_events.OnMultipleComponentsChanged();
		}

		public void Dispose()
		{
			SaveShip(_ship);
		}

		public bool TryFindComponent(ShipElementType elementType, UnityEngine.Vector2Int position, ComponentInfo info, out IComponentModel component)
		{
			component = _layout[elementType].FindComponent(position.x, position.y, info);
			return component != null;
		}

        public void SaveShipToPreset(IShipPreset preset)
        {
            var shipLayout = _layout[ShipElementType.Ship];
            preset.Components.Assign(ExportComponents(shipLayout));

            var firstSatellite = _satellite[SatelliteLocation.Left];
            if (firstSatellite != null)
                firstSatellite = new CommonSatellite(firstSatellite.Information, ExportComponents(_layout[SatelliteLocation.Left]));

            var secondSatellite = _satellite[SatelliteLocation.Right];
            if (secondSatellite != null)
                secondSatellite = new CommonSatellite(secondSatellite.Information, ExportComponents(_layout[SatelliteLocation.Right]));

            preset.FirstSatellite = firstSatellite;
            preset.SecondSatellite = secondSatellite;
        }

        public bool LoadShipFromPreset(IShipPreset preset)
        {
            if (preset.Ship != _ship.Model.OriginalShip) return false;

            bool result = true;
            RemoveAllComponents();
            RemoveSatellite(SatelliteLocation.Left);
            RemoveSatellite(SatelliteLocation.Right);

            result &= InstallComponentsFromPreset(preset.Components, ShipElementType.Ship);

            if (preset.FirstSatellite != null)
                result &= TryInstallSatellite(SatelliteLocation.Left, preset.FirstSatellite.Information) && 
                    InstallComponentsFromPreset(preset.FirstSatellite.Components, ShipElementType.SatelliteL);

            if (preset.SecondSatellite != null)
                result &= TryInstallSatellite(SatelliteLocation.Right, preset.SecondSatellite.Information) &&
                    InstallComponentsFromPreset(preset.SecondSatellite.Components, ShipElementType.SatelliteR);

            _events.OnSatelliteChanged(SatelliteLocation.Left);
            _events.OnSatelliteChanged(SatelliteLocation.Right);
            _events.OnMultipleComponentsChanged();

            return result;
        }

        private bool InstallComponentsFromPreset(IEnumerable<IntegratedComponent> components, ShipElementType shipElement)
        {
            bool result = true;
            var layout = _layout[shipElement];

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

        private ISatellite SaveSatellite(SatelliteLocation location)
		{
			var satellite = _satellite[location];
			if (satellite != null && _layout[location].DataChanged)
			{
				satellite.Components.Assign(ExportComponents(_layout[location]));
				_layout[location].DataChanged = false;
			}

			return satellite;
		}

		private void SaveShip(IShip ship)
		{
			if (ship == null) return;

			if (!string.IsNullOrEmpty(_shipName))
				Ship.Name = _shipName;

			var shipLayout = _layout[ShipElementType.Ship];
			if (shipLayout.DataChanged)
			{
				Ship.Components.Assign(ExportComponents(shipLayout));
				shipLayout.DataChanged = false;
			}

			_ship.FirstSatellite = SaveSatellite(SatelliteLocation.Left);
			_ship.SecondSatellite = SaveSatellite(SatelliteLocation.Right);
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
			_layout[component.Location].UpdateComponent(component, settings);
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

		private void InitializeSatellite(SatelliteLocation location, ISatellite satellite)
		{
			_satellite[location] = satellite;
			if (satellite != null)
			{
				_layout[location] = new ShipLayoutModel(location.ToShipElement(),
					new ShipLayoutAdapter(satellite.Information.Layout), satellite.Information.Barrels, _compatibilityChecker);
				InitializeLayout(_layout[location], satellite.Components);
			}
			else
			{
				_layout[location] = null;
			}

			_events.OnSatelliteChanged(location);
		}
	}
}
