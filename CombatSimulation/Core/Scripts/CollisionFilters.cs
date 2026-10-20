using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Collision categories: one bit per side and class (physics step 5, owner decision 7). They follow the legacy
    // layer matrix: ships touch every ship; an energy bolt hits enemy ships, drones and missiles, never another
    // energy bolt and never its own side; a missile meets enemy ships, drones, missiles and energy bolts.
    internal static class CollisionFilters
    {
        private const int ShipClass = 0;
        private const int EnergyClass = 1;
        private const int MissileClass = 2;

        public static ulong ShipCategory(int side) => Category(ShipClass, side);
        public static ulong EnergyCategory(int side) => Category(EnergyClass, side);
        public static ulong MissileCategory(int side) => Category(MissileClass, side);

        public static Filter Ship(int side) => new Filter(ShipCategory(side), 0);

        public static Filter Energy(int side) =>
            new Filter(EnergyCategory(side), ShipCategory(side) | EnergyCategory(0) | EnergyCategory(1));

        public static Filter Missile(int side) =>
            new Filter(MissileCategory(side), ShipCategory(side) | EnergyCategory(side) | MissileCategory(side));

        public static Filter Bullet(BulletDefinition definition, int side) =>
            definition.IsMissile ? Missile(side) : Energy(side);

        // Query-only category: targets cannot exclude it through their own collision masks.
        public static Filter PointDefense(int side) =>
            new Filter(0, ~(EnergyCategory(1 - side) | MissileCategory(1 - side)));

        private static ulong Category(int unitClass, int side) => 1UL << (unitClass * BattleSetup.SideCount + side);
    }
}
