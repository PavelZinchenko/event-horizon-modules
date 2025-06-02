using Constructor.Satellites;
using System.Collections.Generic;

namespace ShipEditor.Model
{
    public struct LayoutList
    {
        private readonly ShipLayoutModel[] _layouts;

        public int Count => _layouts.Length;

        public ShipLayoutModel this[ShipElementType element]
        {
            get => _layouts[(int)element];
            set => _layouts[(int)element] = value;
        }

        public IEnumerable<ShipLayoutModel> All => _layouts.NotNull();

        public LayoutList(Constructor.Ships.IShip ship)
        {
            _layouts = new ShipLayoutModel[ship.Model.Satellites.Count + 1];
        }
    }

    public struct SatelliteList 
    {
        private readonly ISatellite[] _satellites;

        public int Count => _satellites.Length;

        public ISatellite this[int mountPoint]
        {
            get => _satellites[mountPoint];
            set => _satellites[mountPoint] = value;
        }

        public SatelliteList(Constructor.Ships.IShip ship)
        {
            _satellites = new ISatellite[ship.Model.Satellites.Count];
        }
    }
}
