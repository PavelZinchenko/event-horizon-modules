using System;
using System.Collections.Generic;
using CombatSimulation.Core;

namespace CombatSimulation.Modes
{
    // What the timer does (phase 4, step 8). Draw is the symmetric rule; the others copy legacy TimeOutMode
    // with its player and enemy reset rules ("Result and timer" in docs/combat_simulator/core.md).
    public enum TimerMode : byte
    {
        // Any deployment or removal resets; the limit is a draw.
        Draw,

        // The limit holds the timer; the adapter calls the next enemy. Never a draw.
        CallNextEnemy,

        // As CallNextEnemy while the enemy side has a ready slot; without one, 3 × the limit is a draw.
        CallNextEnemyOrDraw,

        // After the limit, the active player ships lose armor and shield.
        DrainPlayerHp,
    }

    // The rules of Main's roster battle (RosterMode): two fleets, deployment limits, elimination, the timer and
    // OnlyOneShip. Immutable, so one instance can serve many setups.
    public sealed class RosterRules : IBattleRules
    {
        public static readonly RosterRules Default = new RosterRules();

        public RosterRules(
            long timeLimitTicks = 0,
            int maxActiveShipsPerSide = 1,
            TimerMode timerMode = TimerMode.Draw,
            int playerSide = 0,
            IReadOnlyList<int> maxActiveShipsBySide = null,
            bool onlyOneShip = false)
        {
            if (timeLimitTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(timeLimitTicks), "Time limit must be zero (none) or positive.");
            if (maxActiveShipsPerSide < 1)
                throw new ArgumentOutOfRangeException(nameof(maxActiveShipsPerSide), "A side must allow at least one active ship.");
            if (timerMode > TimerMode.DrainPlayerHp)
                throw new ArgumentOutOfRangeException(nameof(timerMode));
            if (playerSide < 0 || playerSide >= BattleSetup.SideCount)
                throw new ArgumentOutOfRangeException(nameof(playerSide));
            var limits = new int[BattleSetup.SideCount];
            for (var side = 0; side < limits.Length; ++side)
                limits[side] = maxActiveShipsPerSide;
            if (maxActiveShipsBySide != null)
            {
                if (maxActiveShipsBySide.Count != BattleSetup.SideCount)
                    throw new ArgumentException("One limit per side is required.", nameof(maxActiveShipsBySide));
                for (var side = 0; side < limits.Length; ++side)
                {
                    if (maxActiveShipsBySide[side] < 1)
                        throw new ArgumentOutOfRangeException(nameof(maxActiveShipsBySide), "A side must allow at least one active ship.");
                    limits[side] = maxActiveShipsBySide[side];
                }
            }

            TimeLimitTicks = timeLimitTicks;
            MaxActiveShipsPerSide = maxActiveShipsPerSide;
            MaxActiveShipsBySide = Array.AsReadOnly(limits);
            TimerMode = timerMode;
            PlayerSide = playerSide;
            OnlyOneShip = onlyOneShip;
        }

        // Ticks both sides must have an active ship before the timer expires (TimerMode). Zero means no timer.
        public long TimeLimitTicks { get; }

        // The limit of both sides unless maxActiveShipsBySide replaces it; MaxActiveShipsBySide has the ones in force.
        public int MaxActiveShipsPerSide { get; }
        public IReadOnlyList<int> MaxActiveShipsBySide { get; }
        public int MaxActiveShips(int side) => MaxActiveShipsBySide[side];

        public TimerMode TimerMode { get; }

        // The side the legacy timer rules call the player; the other side is the enemy. Draw ignores it.
        public int PlayerSide { get; }
        public int EnemySide => 1 - PlayerSide;

        // Legacy PlayerShipSelectionMode.OnlyOneShip: the first death of a player roster ship ends the battle
        // RosterMode.ShipLostTicks later, unless an elimination comes first; the player reserves stay ready.
        public bool OnlyOneShip { get; }

        // The sides the legacy player rules (the DrainPlayerHp drain, OnlyOneShip) apply to: PlayerSide alone, or
        // both sides with remote owners (phase 7, Q1). Single-player battles are unchanged.
        public bool HasPlayerRules(BattleSetup setup, int side) => side == PlayerSide || setup.HasRemoteOwners;

        // Each side starts with a unit: the battle ends when a side has none left.
        public void Validate(BattleSetup setup)
        {
            var unitsPerSide = new int[BattleSetup.SideCount];
            foreach (var unit in setup.Units) unitsPerSide[unit.Side]++;
            for (var side = 0; side < BattleSetup.SideCount; ++side)
                if (unitsPerSide[side] == 0)
                    throw new ArgumentException($"Side {side} has no roster slot.", nameof(setup));
        }

        public IBattleMode CreateMode(BattleSetup setup) => new RosterMode(setup, this);
    }
}
