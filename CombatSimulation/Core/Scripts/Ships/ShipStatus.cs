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

        // Legacy StealthDevice (phase 5, step 9b): target priority None; the view fades the ship by the viewer's side.
        Stealth = 16,

        // Legacy AcceleratorDevice (phase 5, step 9c): throttle 1, so the brake does not act. EngineOff wins over it.
        FullThrottle = 32,
    }

    internal struct ShipStatus
    {
        public ShipStatusFlags Flags;

        // Devices whose control bit counts as released from the next tick on (legacy ISystemsModification).
        public ulong BlockedDevices;

        // The share from 0 to 1 by which the view moves the ship colour to its device colour (legacy Detonator
        // FeaturesData.Color); the view only.
        public float Tint;

        // From 0 to 1: each armor resistance r of the ship becomes r + share x (ceiling - r), the ceiling of its damage
        // type (legacy IStatsModification on Resistance: worm tail, step 7d).
        public float ResistanceShare;

        // Legacy BrakeDevice (phase 5, step 9c): the sum of the brakes, in units per second squared. The engine applies it
        // while the throttle is 0.001 or less.
        public float Deceleration;

        // Legacy applies each modification in turn, so two shares p and q give 1 - (1 - p)(1 - q).
        public void AddResistanceShare(float share)
        {
            if (share > 0f) ResistanceShare = 1f - (1f - ResistanceShare) * (1f - Math.Min(share, 1f));
        }

        // Legacy AcceleratorDevice (phase 5, step 9c): propulsion and velocity x power; two accelerators multiply.
        public readonly float EngineScale => _scalesEngine ? _engineScale : 1f;

        public void ScaleEngine(float power)
        {
            _engineScale = EngineScale * power;
            _scalesEngine = true;
        }

        private float _engineScale;
        private bool _scalesEngine;
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
