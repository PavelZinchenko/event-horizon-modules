using System.Collections.Generic;

namespace CombatSimulation.Core.Tests
{
    // Code fixtures for core tests. Side 0 slots get roster IDs 1, 2, ...; side 1 slots get 101, 102, ...
    internal static class Fixtures
    {
        public static readonly ShipDefinition Ship = new ShipDefinition(1f, 1f);

        public static List<RosterSlotSetup> Roster(int side0Slots, int side1Slots)
        {
            var roster = new List<RosterSlotSetup>();
            for (var i = 0; i < side0Slots; ++i) roster.Add(new RosterSlotSetup(new RosterId((uint)(1 + i)), 0, Ship));
            for (var i = 0; i < side1Slots; ++i) roster.Add(new RosterSlotSetup(new RosterId((uint)(101 + i)), 1, Ship));
            return roster;
        }

        public static BattleSetup Setup(int side0Slots = 1, int side1Slots = 1, long timeLimitTicks = 0,
            int maxActiveShipsPerSide = 1, ulong battleId = 1, ulong matchSeed = 7) =>
            new BattleSetup(new BattleId(battleId), matchSeed, Roster(side0Slots, side1Slots),
                timeLimitTicks: timeLimitTicks, maxActiveShipsPerSide: maxActiveShipsPerSide);
    }

    // Submits roster commands with increasing command IDs, targeted at the next tick by default.
    internal sealed class Driver
    {
        private ulong _nextCommandId = 1;

        public Driver(BattleSetup setup) => Battle = new Battle(setup) { ThrowOnDefect = true };

        public Battle Battle { get; }
        public long Tick => Battle.Tick;
        public BattleId BattleId => Battle.BattleId;

        public bool Deploy(uint rosterId, long? tick = null) =>
            Battle.Submit(RosterCommand.Deploy(BattleId, _nextCommandId++, tick ?? Tick, new RosterId(rosterId)));

        public bool Retreat(EntityId id, long? tick = null) =>
            Battle.Submit(RosterCommand.Retreat(BattleId, _nextCommandId++, tick ?? Tick, id));

        public bool Surrender(int side, long? tick = null) =>
            Battle.Submit(RosterCommand.Surrender(BattleId, _nextCommandId++, tick ?? Tick, side));

        public bool KillAll(int requestingSide, long? tick = null) =>
            Battle.Submit(RosterCommand.KillAll(BattleId, _nextCommandId++, tick ?? Tick, requestingSide));

        public void Kill(uint rosterId) => Battle.QueueDeath(Entity(rosterId));

        public void Step(long count = 1)
        {
            for (var i = 0; i < count; ++i) Battle.Step();
        }

        public void StepUntil(long tick)
        {
            while (Battle.Tick < tick) Battle.Step();
        }

        public RosterSlot Slot(uint rosterId) => Battle.Roster.Find(new RosterId(rosterId));
        public RosterStatus Status(uint rosterId) => Slot(rosterId).Status;
        public EntityId Entity(uint rosterId) => Slot(rosterId).ActiveShip?.Id ?? default;
    }
}
