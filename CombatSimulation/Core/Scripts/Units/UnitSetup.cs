using System;

namespace CombatSimulation.Core
{
    // One fleet slot at battle setup. The slot keeps its roster ID for the whole battle.
    public sealed class UnitSetup
    {
        // Allegiance.Undefined gives Allegiances.OfSide(side).
        public UnitSetup(UnitId rosterId, int side, ShipDefinition ship, int owner = 0,
            Allegiance allegiance = Allegiance.Undefined)
        {
            if (side < 0 || side >= BattleSetup.SideCount)
                throw new ArgumentOutOfRangeException(nameof(side), "Side must be 0 or 1.");
            if (owner < 0)
                throw new ArgumentOutOfRangeException(nameof(owner), "Owner must be a player index.");
            if (!Allegiances.IsValid(allegiance))
                throw new ArgumentOutOfRangeException(nameof(allegiance));

            UnitId = rosterId;
            Side = side;
            Ship = ship ?? throw new ArgumentNullException(nameof(ship));
            Owner = owner;
            Allegiance = allegiance == Allegiance.Undefined ? Allegiances.OfSide(side) : allegiance;
        }

        public UnitId UnitId { get; }

        // Allegiance. Separate from the owner (investigation 6.3 item 8).
        public int Side { get; }

        // The player index that commits this slot's changes. Every slot has owner 0 in phase 2.
        public int Owner { get; }

        public ShipDefinition Ship { get; }

        // The allegiance of the unit's ships and drones when they deploy. Frontier's level gives Player to the player's
        // unit and Ally to the other side-0 units.
        public Allegiance Allegiance { get; }
    }
}
