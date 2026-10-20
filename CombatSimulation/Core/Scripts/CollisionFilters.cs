using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Collision categories: one bit per side and class (physics step 5, owner decision 7). They follow the legacy
    // layer matrix: ships touch every ship; an energy bolt hits enemy ships, drones and missiles, never another
    // energy bolt and never its own side. Missiles get their class in step 7.
    internal static class CollisionFilters
    {
        private const int ShipClass = 0;
        private const int EnergyClass = 1;

        public static ulong ShipCategory(int side) => Category(ShipClass, side);
        public static ulong EnergyCategory(int side) => Category(EnergyClass, side);

        public static Filter Ship(int side) => new Filter(ShipCategory(side), 0);

        public static Filter Energy(int side) =>
            new Filter(EnergyCategory(side), ShipCategory(side) | EnergyCategory(0) | EnergyCategory(1));

        private static ulong Category(int unitClass, int side) => 1UL << (unitClass * BattleSetup.SideCount + side);
    }
}
