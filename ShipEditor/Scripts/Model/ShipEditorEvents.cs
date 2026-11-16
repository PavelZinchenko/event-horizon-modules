using System;
using Constructor.Ships;

namespace ShipEditor.Model
{
	public interface IShipEditorEventTriggers
	{
		void OnComponentAdded(IComponentModel component);
		void OnComponentRemoved(IComponentModel component);
		void OnComponentModified(IComponentModel component);
		void OnShipChanged(IShip ship);
		void OnSatelliteChanged(int mountPoint);
		void OnMultipleComponentsChanged();
	}

	public interface IShipEditorEvents
	{
		event Action<IShip> ShipChanged;
		event Action<int> SatelliteChanged;
		event Action<IComponentModel> ComponentAdded;
		event Action<IComponentModel> ComponentRemoved;
		event Action<IComponentModel> ComponentModified;
		event Action MultipleComponentsChanged;
	}

	public class ShipEditorEvents : IShipEditorEvents, IShipEditorEventTriggers
	{
		public event Action<IShip> ShipChanged;
		public event Action<int> SatelliteChanged;
		public event Action<IComponentModel> ComponentAdded;
		public event Action<IComponentModel> ComponentRemoved;
		public event Action<IComponentModel> ComponentModified;
		public event Action MultipleComponentsChanged;

		public void OnComponentAdded(IComponentModel component) => ComponentAdded?.Invoke(component);
		public void OnComponentRemoved(IComponentModel component) => ComponentRemoved?.Invoke(component);
		public void OnComponentModified(IComponentModel component) => ComponentModified?.Invoke(component);
		public void OnShipChanged(IShip ship) => ShipChanged?.Invoke(ship);
		public void OnSatelliteChanged(int mountPoint) => SatelliteChanged?.Invoke(mountPoint);
		public void OnMultipleComponentsChanged() => MultipleComponentsChanged?.Invoke();
	}
}
