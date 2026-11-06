using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Collision categories: one bit per allegiance group and class (physics step 5, owner decision 7; phase 4, D13). They
    // follow the legacy layer matrix: ships touch every ship; an energy bolt hits hostile ships, drones and missiles,
    // never another energy bolt and never a friend; a missile meets hostile ships, drones, missiles and energy bolts;
    // drones touch every drone and hostile ships.
    internal static class CollisionFilters
    {
        private const int ShipClass = 0;
        private const int EnergyClass = 1;
        private const int MissileClass = 2;
        private const int DroneClass = 3;

        public static ulong ShipCategory(Allegiance allegiance) => Category(ShipClass, allegiance);
        public static ulong EnergyCategory(Allegiance allegiance) => Category(EnergyClass, allegiance);
        public static ulong MissileCategory(Allegiance allegiance) => Category(MissileClass, allegiance);
        public static ulong DroneCategory(Allegiance allegiance) => Category(DroneClass, allegiance);

        public static Filter Ship(Allegiance allegiance) => new Filter(ShipCategory(allegiance), 0);
        public static Filter Drone(Allegiance allegiance) => new Filter(DroneCategory(allegiance), ShipCategory(allegiance));

        public static Filter Energy(Allegiance allegiance, bool friendlyFire = false) => friendlyFire
            ? new Filter(EnergyCategory(Allegiance.Neutral), AllEnergy)
            : new Filter(EnergyCategory(allegiance), ShipCategory(allegiance) | DroneCategory(allegiance) | AllEnergy);

        public static Filter Missile(Allegiance allegiance) =>
            new Filter(MissileCategory(allegiance), ShipCategory(allegiance) | DroneCategory(allegiance) |
                EnergyCategory(allegiance) | MissileCategory(allegiance));

        // A beam bolt's body pairs with no shape and no bullet (phase 5, step 4e): its ray finds its hits. Category-free
        // queries (point defense) still see it, so they skip it.
        public static Filter Bullet(BulletDefinition definition, Allegiance allegiance) =>
            definition.IsBeamBolt ? NoContacts : definition.IsMissile
                ? definition.FriendlyFire ? new Filter(MissileCategory(Allegiance.Neutral), 0) : Missile(allegiance)
                : Energy(allegiance, definition.FriendlyFire);

        // Legacy collider Source: exclude the firing platform and its units, not every unit of its network owner.
        internal static bool IsSource(EntityId source, ShipEntity ship, TailSegment segment = null) =>
            !source.IsNull && (ship != null ? ship.Id == source || ship.MothershipId == source : segment != null && segment.ShipId == source);

        private static readonly Filter NoContacts = new Filter(0, ~0UL);

        // Query-only category: targets cannot exclude it through their own collision masks.
        public static Filter PointDefense(Allegiance allegiance) =>
            new Filter(0, ~((AllEnergy | AllMissiles | AllDrones) & ~(EnergyCategory(allegiance) |
                MissileCategory(allegiance) | DroneCategory(allegiance))));

        private static readonly ulong AllEnergy = ClassMask(EnergyClass);
        private static readonly ulong AllMissiles = ClassMask(MissileClass);
        private static readonly ulong AllDrones = ClassMask(DroneClass);

        private static ulong ClassMask(int unitClass) => ((1UL << Allegiances.GroupCount) - 1) << (unitClass * Allegiances.GroupCount);

        private static ulong Category(int unitClass, Allegiance allegiance) =>
            1UL << (unitClass * Allegiances.GroupCount + Allegiances.Group(allegiance));
    }
}
