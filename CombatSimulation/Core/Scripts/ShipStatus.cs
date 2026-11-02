using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // The modifiers of a ship's active devices in a tick (phase 5, step 7). Stage 3 collects them; later stages and the
    // snapshot read them.
    [Flags]
    public enum ShipStatusFlags : byte
    {
        None = 0,

        // Legacy FeaturesData.Invulnerable (the collider is off): no hits, no ramming, no physical contacts.
        NoContacts = 1,

        // Throttle 0 and no course: no thrust; the spin brake stays (legacy WarpDrive EngineData).
        EngineOff = 2,

        // Drawn at 10% opacity (legacy WarpDrive FeaturesData.Opacity); the view only.
        Faded = 4,

        // No thrust, no turn and no spin brake: the ship keeps its velocity and spin (legacy Detonator EngineData: turn
        // rate, propulsion and throttle 0).
        Drift = 8,
    }

    internal struct ShipStatus
    {
        public ShipStatusFlags Flags;

        // Devices whose control bit counts as released from the next tick on (legacy ISystemsModification).
        public ulong BlockedDevices;

        // The share from 0 to 1 by which the view moves the ship colour to its device colour (legacy Detonator
        // FeaturesData.Color); the view only.
        public float Tint;
    }

    internal static class ShipStatusRules
    {
        // A ship without contacts keeps its categories but ignores every other category, so no shape pairs with it.
        // Query-only filters have no category; the point-defense query skips such a ship itself.
        public static Filter HullFilter(ShipEntity ship)
        {
            var filter = ship.IsDrone ? CollisionFilters.Drone(ship.Allegiance) : CollisionFilters.Ship(ship.Allegiance);
            return (ship.Status & ShipStatusFlags.NoContacts) != 0 ? new Filter(filter.Categories, ~0UL) : filter;
        }
    }
}
