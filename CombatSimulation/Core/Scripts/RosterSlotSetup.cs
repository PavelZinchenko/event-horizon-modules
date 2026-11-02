using System;

namespace CombatSimulation.Core
{
    // One fleet slot at battle setup. The slot keeps its roster ID for the whole battle.
    public sealed class RosterSlotSetup
    {
        public RosterSlotSetup(RosterId rosterId, int side, ShipDefinition ship, int owner = 0)
        {
            if (side < 0 || side >= BattleSetup.SideCount)
                throw new ArgumentOutOfRangeException(nameof(side), "Side must be 0 or 1.");
            if (owner < 0)
                throw new ArgumentOutOfRangeException(nameof(owner), "Owner must be a player index.");

            RosterId = rosterId;
            Side = side;
            Ship = ship ?? throw new ArgumentNullException(nameof(ship));
            Owner = owner;
        }

        public RosterId RosterId { get; }

        // Allegiance. Separate from the owner (investigation 6.3 item 8).
        public int Side { get; }

        // The player index that commits this slot's changes. Every slot has owner 0 in phase 2.
        public int Owner { get; }

        public ShipDefinition Ship { get; }
    }
}
