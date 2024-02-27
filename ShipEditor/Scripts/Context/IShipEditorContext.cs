using System.Collections.Generic;
using Constructor;
using CommonComponents.Utils;
using Constructor.Ships;
using Constructor.Satellites;
using GameDatabase.DataModel;

namespace ShipEditor.Context
{
    public interface IShipEditorContext
	{
		IShip Ship { get; }
		IInventoryProvider Inventory { get; }
        IShipDataProvider ShipDataProvider { get; }
        IShipPresetStorage ShipPresetStorage { get; }
        public bool CanBeUnlocked(Component component);
        bool IsShipNameEditable { get; }
    }

    public interface IInventoryProvider 
	{
		IEnumerable<IShip> Ships { get; }
		IReadOnlyCollection<ISatellite> SatelliteBuilds { get; }
		IReadOnlyGameItemCollection<Satellite> Satellites { get; }
		IReadOnlyGameItemCollection<ComponentInfo> Components { get; }

		public void AddComponent(ComponentInfo component);
		public bool TryRemoveComponent(ComponentInfo component);

		public void AddSatellite(Satellite satellite);
		public bool TryRemoveSatellite(Satellite satellite);

		public Economy.Price GetUnlockPrice(ComponentInfo component);
		public bool TryPayForUnlock(ComponentInfo component);
	}

    public interface IShipPresetStorage
    {
        IEnumerable<IShipPreset> GetPresets(Ship ship);
        IShipPreset Create(Ship ship);
        void Delete(IShipPreset preset);
    }
}
